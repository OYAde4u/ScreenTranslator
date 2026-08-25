using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ScreenTranslator.Services;

/// <summary>全局热键服务(Ctrl+Shift+T 手动触发一轮翻译)。</summary>
public sealed class HotKeyService : IDisposable
{
    public const int HotkeyId = 0x5354; // 'ST'

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint VkT = 0x54;
    private const int WmHotkey = 0x0312;

    private readonly Window _owner;
    private readonly Action _callback;
    private HwndSource? _source;

    public HotKeyService(Window owner, Action callback)
    {
        _owner = owner;
        _callback = callback;
    }

    public bool Register()
    {
        var hwnd = new WindowInteropHelper(_owner).Handle;
        _source = HwndSource.FromHwnd(hwnd);
        _source?.AddHook(WndProc);
        var ok = RegisterHotKey(hwnd, HotkeyId, ModControl | ModShift, VkT);
        if (!ok)
        {
            // 注册失败:清理 hook,避免残留
            _source?.RemoveHook(WndProc);
            _source = null;
        }
        return ok;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            _callback();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        var hwnd = new WindowInteropHelper(_owner).Handle;
        UnregisterHotKey(hwnd, HotkeyId);
        _source?.RemoveHook(WndProc);
    }
}
