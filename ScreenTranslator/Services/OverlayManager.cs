using System.Windows;
using System.Windows.Threading;
using ScreenTranslator.Services.Ocr;

namespace ScreenTranslator.Services;

/// <summary>
/// 覆盖层管理器:为每个显示器维护一个 OverlayWindow(逐屏 DPI 换算,跨屏位置精确),
/// 负责把 OCR 图元路由到对应显示器、排除应用自身窗口区域、整体隐藏/显示(截图时避免覆盖层进入画面)。
/// 显示器插拔/分辨率变化时自动重建窗口(2s 轮询,无 SystemEvents 依赖)。
/// </summary>
public sealed class OverlayManager : IDisposable
{
    private readonly List<OverlayWindow> _windows = new();
    private readonly DispatcherTimer _layoutTimer;
    private bool _disposed;

    /// <summary>应用自身窗口区域(物理像素,虚拟屏幕坐标系):该区域内的图元不绘制。</summary>
    public Rect AppExcludeRect { get; set; } = Rect.Empty;

    /// <summary>额外排除区域(如悬浮状态框):这些区域内的图元同样不绘制。</summary>
    public IReadOnlyList<Rect> ExtraExcludes { get; set; } = Array.Empty<Rect>();

    /// <summary>当前是否绘制了图元(决定截图前是否需要隐藏)。</summary>
    public bool HasItems { get; private set; }

    public OverlayManager()
    {
        SyncWindows();
        _layoutTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _layoutTimer.Tick += (_, _) =>
        {
            if (DisplayLayout.Refresh()) SyncWindows();
        };
        _layoutTimer.Start();
    }

    /// <summary>按当前显示器布局重建覆盖窗口(布局变化时调用)。</summary>
    public void SyncWindows()
    {
        // 删除多余窗口
        for (var i = _windows.Count - 1; i >= 0; i--)
        {
            var w = _windows[i];
            if (DisplayLayout.Monitors.All(m => m != w.Monitor))
            {
                w.Close();
                _windows.RemoveAt(i);
            }
        }
        // 新增窗口
        foreach (var m in DisplayLayout.Monitors)
        {
            if (_windows.Any(w => w.Monitor == m)) continue;
            var win = new OverlayWindow(m) { ShowActivated = false };
            _windows.Add(win);
            win.Show();
        }
    }

    /// <summary>按物理坐标把图元路由到各显示器覆盖窗口,跳过应用自身窗口区域。</summary>
    public void SetItems(IReadOnlyList<OcrOverlayRenderer.OverlayItem> items)
    {
        HasItems = items.Count > 0;
        var app = AppExcludeRect;
        var extra = ExtraExcludes;
        var filtered = items
            .Where(i => (app.IsEmpty || !Intersects(i, app))
                        && extra.All(ex => ex.IsEmpty || !Intersects(i, ex)))
            .ToList();

        foreach (var win in _windows)
        {
            var mine = filtered.Where(i => MonitorContains(win.Monitor, i)).ToList();
            win.SetItems(mine);
            // 渲染后重新断言置顶:后弹出的 Topmost 窗口(演示页/游戏等)会压过覆盖层
            win.BringToFront();
        }
    }

    public void Clear()
    {
        HasItems = false;
        foreach (var w in _windows) w.ClearItems();
    }

    /// <summary>截图前调用:隐藏所有覆盖窗口(避免覆盖层进入下一帧画面)。</summary>
    public void HideAll()
    {
        if (!HasItems) return;
        foreach (var w in _windows) w.Visibility = Visibility.Hidden;
    }

    /// <summary>截图/渲染后恢复显示。</summary>
    public void ShowAll()
    {
        if (!HasItems) return;
        foreach (var w in _windows)
        {
            w.Visibility = Visibility.Visible;
            w.BringToFront();
        }
    }

    /// <summary>图元面积 70% 以上落在 app 窗口内才排除(与 OcrLineFilter 的忽略规则一致,不误杀重叠内容)。</summary>
    private static bool Intersects(OcrOverlayRenderer.OverlayItem i, Rect r)
    {
        var ix = Math.Max(0, Math.Min(i.X + i.W, r.X + r.Width) - Math.Max(i.X, r.X));
        var iy = Math.Max(0, Math.Min(i.Y + i.H, r.Y + r.Height) - Math.Max(i.Y, r.Y));
        var area = i.W * i.H;
        return area > 0 && ix * iy >= area * 0.7;
    }

    private static bool MonitorContains(ScreenMonitor m, OcrOverlayRenderer.OverlayItem i)
    {
        var cx = i.X + i.W / 2;
        var cy = i.Y + i.H / 2;
        return cx >= m.Left && cx < m.Left + m.Width && cy >= m.Top && cy < m.Top + m.Height;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _layoutTimer.Stop();
        foreach (var w in _windows) w.Close();
        _windows.Clear();
    }
}
