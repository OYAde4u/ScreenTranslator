using System.Runtime.InteropServices;
using System.Windows;

namespace ScreenTranslator.Services;

/// <summary>
/// 显示器布局服务:枚举所有显示器(物理像素边界 + 设备名 + 每屏 DPI),维护虚拟屏幕范围。
/// 进程为 system-DPI-aware:GetSystemMetrics/GetMonitorInfo 均为物理像素;
/// 覆盖层渲染时按"目标显示器 DPI"做物理像素 → DIP 换算,抵消 DWM 跨屏缩放,保证每屏位置精确。
/// </summary>
public sealed record ScreenMonitor(int Left, int Top, int Width, int Height, string DeviceName, uint DpiX)
{
    /// <summary>该显示器 DPI 相对 96 的缩放系数。</summary>
    public double Scale => DpiX / 96.0;
}

public static class DisplayLayout
{
    private static readonly object Lock = new();

    /// <summary>当前显示器列表(物理像素,虚拟屏幕坐标系)。</summary>
    public static IReadOnlyList<ScreenMonitor> Monitors { get; private set; } = Array.Empty<ScreenMonitor>();

    /// <summary>虚拟屏幕(所有显示器并集,物理像素)。</summary>
    public static Rect VirtualScreen { get; private set; } = Rect.Empty;

    /// <summary>系统 DPI 缩放系数(主屏)。</summary>
    public static double SystemScale { get; private set; } = 1.0;

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, IntPtr lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfoEx lpmi);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    private const int SmXvirtualscreen = 76;
    private const int SmYvirtualscreen = 77;
    private const int SmCxvirtualscreen = 78;
    private const int SmCyvirtualscreen = 79;
    private const int MdTEffectiveDpi = 0;

    static DisplayLayout() => Refresh();

    /// <summary>重新枚举显示器;布局变化返回 true(供覆盖层重建窗口)。</summary>
    public static bool Refresh()
    {
        var list = new List<ScreenMonitor>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (hMonitor, _, _, _) =>
        {
            var mi = new MonitorInfoEx { cbSize = (uint)Marshal.SizeOf<MonitorInfoEx>() };
            if (GetMonitorInfo(hMonitor, ref mi))
            {
                uint dpiX = 96, dpiY = 96;
                if (GetDpiForMonitor(hMonitor, MdTEffectiveDpi, out dpiX, out dpiY) != 0)
                {
                    dpiX = 96;
                }
                var r = mi.rcMonitor;
                list.Add(new ScreenMonitor(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top,
                    mi.szDevice ?? string.Empty, dpiX));
            }
            return true;
        }, IntPtr.Zero);

        lock (Lock)
        {
            var changed = list.Count != Monitors.Count
                || !Monitors.SequenceEqual(list);
            if (changed || Monitors.Count == 0)
            {
                Monitors = list;
                var vl = GetSystemMetrics(SmXvirtualscreen);
                var vt = GetSystemMetrics(SmYvirtualscreen);
                var vw = GetSystemMetrics(SmCxvirtualscreen);
                var vh = GetSystemMetrics(SmCyvirtualscreen);
                VirtualScreen = new Rect(vl, vt, vw, vh);
                SystemScale = GetDpiForSystem() / 96.0;
            }
            return changed;
        }
    }

    /// <summary>查找包含指定物理像素点的显示器。</summary>
    public static ScreenMonitor? FindMonitorAt(double x, double y)
    {
        foreach (var m in Monitors)
        {
            if (x >= m.Left && x < m.Left + m.Width && y >= m.Top && y < m.Top + m.Height)
                return m;
        }
        return null;
    }

    /// <summary>把物理像素坐标换算为指定显示器窗口内的 DIP(抵消 DWM 跨屏缩放)。</summary>
    public static double ToDip(double physical, ScreenMonitor m) => physical * 96.0 / m.DpiX;
}
