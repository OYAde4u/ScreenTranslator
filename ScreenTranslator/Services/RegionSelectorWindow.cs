using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace ScreenTranslator.Services;

/// <summary>
/// 框选识别区域:覆盖整个虚拟屏的暗化层 + 拖拽高亮框;左键拖选,Esc/右键取消。
/// 本机不合成 AllowsTransparency 分层窗口(悬浮框已实测验证),故:
/// - 暗化层 = 整窗 Opacity=0.25 的黑色窗口(不透明窗口,系统正常合成);
/// - 高亮框 = 独立的半透明纯色小窗口跟随拖动(同样走 Opacity 机制)。
/// 结果以 DIP 虚拟屏坐标通过 RegionSelected 上报,由调用方换算物理像素。
/// </summary>
public sealed class RegionSelectorWindow : Window
{
    /// <summary>拖选完成(尺寸 ≥ 20×12 DIP)时触发,参数为 DIP 虚拟屏坐标矩形。</summary>
    public event Action<Rect>? RegionSelected;

    private readonly Window _band;
    private Point _start;
    private bool _dragging;

    public RegionSelectorWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Background = Brushes.Black;
        Opacity = 0.25;
        Cursor = Cursors.Cross;
        Focusable = true;
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        _band = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Topmost = true,
            Background = new SolidColorBrush(Color.FromRgb(0x0F, 0x6B, 0xBD)),
            Opacity = 0.35,
            IsHitTestVisible = false,
            Width = 0,
            Height = 0,
        };

        MouseLeftButtonDown += OnDown;
        MouseMove += OnMove;
        MouseLeftButtonUp += OnUp;
        MouseRightButtonDown += (_, _) => Cancel();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Cancel(); };
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        Focus(); // 确保 Esc 键能被收到
    }

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        _start = e.GetPosition(this);
        _dragging = true;
        CaptureMouse();
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        var p = e.GetPosition(this);
        _band.Left = Left + Math.Min(_start.X, p.X);
        _band.Top = Top + Math.Min(_start.Y, p.Y);
        _band.Width = Math.Abs(p.X - _start.X);
        _band.Height = Math.Abs(p.Y - _start.Y);
        if (!_band.IsVisible) _band.Show();
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        var p = e.GetPosition(this);
        var rect = new Rect(
            Left + Math.Min(_start.X, p.X),
            Top + Math.Min(_start.Y, p.Y),
            Math.Abs(p.X - _start.X),
            Math.Abs(p.Y - _start.Y));
        _band.Close();
        if (rect.Width >= 20 && rect.Height >= 12)
            RegionSelected?.Invoke(rect);
        Close();
    }

    private void Cancel()
    {
        _dragging = false;
        _band.Close();
        Close();
    }
}
