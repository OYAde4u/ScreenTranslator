using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;

namespace ScreenTranslator.Services;

/// <summary>
/// 单个显示器上的置顶覆盖窗口。
/// 渲染方案:普通窗口(AllowsTransparency=false)+ SetWindowRgn 把窗口裁剪成"各覆盖块的并集"。
/// 不依赖 WPF 分层窗口(AllowsTransparency/UpdateLayeredWindow)——实测该机器上分层窗口内容
/// 渲染正常但不合成上屏,普通窗口 + 区域裁剪必定上屏,且 GDI 截图可见(可自动化验证)。
/// 样式:WS_EX_TOPMOST + TRANSPARENT(鼠标穿透)+ NOACTIVATE(不抢焦点)。
/// 坐标:进程 system-DPI-aware,DIP 按本显示器 DPI 换算,位置跨屏精确。
/// </summary>
public sealed class OverlayWindow : Window
{
    private const int GwlExstyle = -20;
    private const long WsExTransparent = 0x00000020L;
    private const long WsExNoActivate = 0x08000000L;
    private const long WsExToolWindow = 0x00000080L;

    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmcpDonotround = 1;
    private const int RgnOr = 2;

    [DllImport("user32.dll")]
    private static extern long GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern long SetWindowLong(IntPtr hwnd, int index, long value);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    private static extern int CombineRgn(IntPtr dest, IntPtr src1, IntPtr src2, int mode);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hwnd, IntPtr rgn, bool redraw);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoActivate = 0x0010;

    private readonly Canvas _canvas = new();
    private ScreenMonitor _monitor;
    private IntPtr _hwnd;
    private string _lastSignature = string.Empty;

    /// <summary>本窗口对应的显示器(物理边界,虚拟屏幕坐标系)。</summary>
    public ScreenMonitor Monitor
    {
        get => _monitor;
        set
        {
            _monitor = value;
            // 窗口几何按本显示器 DPI 换算:DWM 会在该屏按 显示器DPI/系统DPI 缩放回来
            Left = value.Left * 96.0 / value.DpiX;
            Top = value.Top * 96.0 / value.DpiX;
            Width = value.Width * 96.0 / value.DpiX;
            Height = value.Height * 96.0 / value.DpiX;
        }
    }

    public OverlayWindow(ScreenMonitor monitor)
    {
        _monitor = monitor;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = false; // 普通窗口,不走分层合成
        Background = Brushes.Black; // 区域外由 SetWindowRgn 裁剪,不遮挡屏幕
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        IsHitTestVisible = false; // 鼠标穿透(WPF 层双保险)
        Content = _canvas;
        SourceInitialized += OnSourceInitialized;
        Monitor = monitor;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        var style = GetWindowLong(_hwnd, GwlExstyle);
        SetWindowLong(_hwnd, GwlExstyle, style | WsExTransparent | WsExNoActivate | WsExToolWindow);

        // 关闭 Win11 圆角,避免区域边缘露白边
        var pref = DwmcpDonotround;
        DwmSetWindowAttribute(_hwnd, DwmwaWindowCornerPreference, ref pref, sizeof(int));

        // 初始区域为空:窗口完全裁剪掉,不可见也不遮挡
        ApplyRegion(Array.Empty<(int X, int Y, int W, int H)>());
    }

    /// <summary>
    /// 重新断言置顶:其他 Topmost 窗口(演示页/游戏/全屏视频)后显示会压过覆盖层,
    /// 每次渲染后调用本方法把覆盖层抢回 z-order 最顶(NOMOVE/NOSIZE/NOACTIVATE,无副作用)。
    /// </summary>
    public void BringToFront()
    {
        if (_hwnd == IntPtr.Zero) return;
        SetWindowPos(_hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    public void ClearItems()
    {
        _canvas.Children.Clear();
        _lastSignature = string.Empty;
        ApplyRegion(Array.Empty<(int X, int Y, int W, int H)>());
    }

    /// <summary>
    /// 设置本窗口的覆盖图元(物理像素,虚拟屏幕坐标系;仅包含落在本显示器内的图元)。
    /// 内容签名不变时跳过重建;每次更新同时重设窗口裁剪区域(块并集)。
    /// </summary>
    public void SetItems(IReadOnlyList<OcrOverlayRenderer.OverlayItem> items)
    {
        // 坐标量化到 4px 网格:OCR 框抖动 1~2px 不应触发整窗重建(闪烁来源);
        // 签名必须包含颜色——否则切换渲染方式(字幕底块↔背景采样)后文本/位置没变,旧色块永远不刷新
        var signature = string.Join("|", items.Select(i =>
            $"{(int)i.X / 4},{(int)i.Y / 4},{(int)i.W / 4},{(int)i.H / 4},{i.Bg.ToString()},{i.Fg.ToString()},{i.Text}"));
        if (signature == _lastSignature) return;
        _lastSignature = signature;

        _canvas.Children.Clear();
        var s = 96.0 / _monitor.DpiX; // 物理像素 → 本窗口 DIP
        var rects = new List<(int X, int Y, int W, int H)>(items.Count);
        foreach (var item in items)
        {
            // 裁剪到本显示器范围
            var x0 = Math.Max(item.X, _monitor.Left);
            var y0 = Math.Max(item.Y, _monitor.Top);
            var x1 = Math.Min(item.X + item.W, _monitor.Left + _monitor.Width);
            var y1 = Math.Min(item.Y + item.H, _monitor.Top + _monitor.Height);
            if (x1 <= x0 || y1 <= y0) continue;

            rects.Add(((int)x0, (int)y0, (int)(x1 - x0), (int)(y1 - y0)));

            var dipX = (x0 - _monitor.Left) * s;
            var dipY = (y0 - _monitor.Top) * s;
            var dipW = (x1 - x0) * s;
            var dipH = (y1 - y0) * s;

            var bgBrush = new SolidColorBrush(item.Bg);
            bgBrush.Freeze();
            var rect = new Rectangle { Width = dipW, Height = dipH, Fill = bgBrush };
            Canvas.SetLeft(rect, dipX);
            Canvas.SetTop(rect, dipY);
            _canvas.Children.Add(rect);

            if (!string.IsNullOrEmpty(item.Text))
            {
                // 字号自适应(多行感知):初始值同时受"行数×行高"和"最长行宽度"约束,
                // 再迭代收缩 7% 直到换行后的实际高度放得下——避免译文过长导致块高膨胀压住相邻块(字体叠加)
                var segs = item.Text.Split('\n');
                var longest = Math.Max(1, segs.Max(x => x.Length));
                var fs = Math.Min(Math.Max(8.0, dipH * 0.7 / Math.Max(1, segs.Length)),
                                  dipW / (longest * 0.62));
                var fgBrush = new SolidColorBrush(item.Fg);
                fgBrush.Freeze();

                // 译文放不下时换行(不再截成省略号);先 Measure 得到实际高度,块高自适应
                var tb = new TextBlock
                {
                    Text = item.Text,
                    FontSize = fs,
                    Foreground = fgBrush,
                    FontFamily = new FontFamily("Microsoft YaHei"),
                    Width = dipW,
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = item.Centered ? TextAlignment.Center : TextAlignment.Left,
                    TextTrimming = TextTrimming.None,
                };
                tb.Measure(new Size(dipW, double.PositiveInfinity));
                while (tb.DesiredSize.Height > dipH + 2 && fs > 8)
                {
                    fs *= 0.93;
                    tb.FontSize = fs;
                    tb.Measure(new Size(dipW, double.PositiveInfinity));
                }
                var textH = tb.DesiredSize.Height;

                // 块高 = max(原文行高, 译文换行后高度 + 边距);只有超出时才长高,保持贴合原文
                var blockH = Math.Max(dipH, textH + 4 * s);
                rects[^1] = ((int)x0, (int)y0, (int)(x1 - x0), (int)(y1 - y0 + (blockH - dipH) / s));
                rect.Height = blockH;

                // 字幕风格垂直居中;背景采样风格紧贴顶部
                var ty = item.Centered ? dipY + Math.Max(0, (blockH - textH) / 2) : dipY + 1 * s;
                Canvas.SetLeft(tb, item.Centered ? dipX : dipX + 3 * s);
                Canvas.SetTop(tb, ty);
                _canvas.Children.Add(tb);
            }
        }
        ApplyRegion(rects);
    }

    /// <summary>把块并集设为窗口裁剪区域(客户区物理坐标);空列表 = 窗口完全不可见。</summary>
    private void ApplyRegion(IReadOnlyList<(int X, int Y, int W, int H)> rects)
    {
        if (_hwnd == IntPtr.Zero) return;

        IntPtr rgn = IntPtr.Zero;
        foreach (var (x, y, w, h) in rects)
        {
            var r = CreateRectRgn(x - _monitor.Left, y - _monitor.Top,
                x - _monitor.Left + w, y - _monitor.Top + h);
            if (rgn == IntPtr.Zero)
            {
                rgn = r;
            }
            else
            {
                CombineRgn(rgn, rgn, r, RgnOr);
                DeleteObject(r);
            }
        }
        if (rgn == IntPtr.Zero) rgn = CreateRectRgn(0, 0, 0, 0);

        // SetWindowRgn 成功后区域所有权移交系统(不可 DeleteObject),失败则自行释放
        if (SetWindowRgn(_hwnd, rgn, true) == 0)
        {
            DeleteObject(rgn);
        }
    }
}
