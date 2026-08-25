using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ScreenTranslator.Services;

/// <summary>
/// 全局输入钩子(WH_MOUSE_LL + WH_KEYBOARD_LL):监听到"用户交互信号"时通知。
/// 过滤:鼠标点击/滚轮、键盘按键;忽略鼠标移动(防抖由上层处理)。
/// 回调在钩子线程触发,只做标记,不做重活。
/// </summary>
public sealed class InputHookService : IDisposable
{
    private const int WhMouseLl = 14;
    private const int WhKeyboardLl = 13;

    private const int WmLbuttondown = 0x0201;
    private const int WmRbuttondown = 0x0204;
    private const int WmMbuttondown = 0x0207;
    private const int WmXbuttondown = 0x020B;
    private const int WmMousewheel = 0x020A;
    private const int WmKeydown = 0x0100;
    private const int WmSyskeydown = 0x0104;

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    private readonly Action _onInput;
    private HookProc? _mouseProc;
    private HookProc? _keyProc;
    private IntPtr _mouseHook;
    private IntPtr _keyHook;

    public InputHookService(Action onInput) => _onInput = onInput;

    public void Start()
    {
        if (_mouseHook != IntPtr.Zero && _keyHook != IntPtr.Zero) return;
        using var curModule = Process.GetCurrentProcess().MainModule!;
        var hMod = GetModuleHandle(curModule.ModuleName);
        _mouseProc = HookCallback;
        _keyProc = HookCallback;
        _mouseHook = SetWindowsHookEx(WhMouseLl, _mouseProc, hMod, 0);
        _keyHook = SetWindowsHookEx(WhKeyboardLl, _keyProc, hMod, 0);
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var msg = wParam.ToInt32();
            switch (msg)
            {
                case WmLbuttondown:
                case WmRbuttondown:
                case WmMbuttondown:
                case WmXbuttondown:
                case WmMousewheel:
                case WmKeydown:
                case WmSyskeydown:
                    _onInput();
                    break;
            }
        }
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_mouseHook != IntPtr.Zero) UnhookWindowsHookEx(_mouseHook);
        if (_keyHook != IntPtr.Zero) UnhookWindowsHookEx(_keyHook);
        _mouseHook = _keyHook = IntPtr.Zero;
    }
}
