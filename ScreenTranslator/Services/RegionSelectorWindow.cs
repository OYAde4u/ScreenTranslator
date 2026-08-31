using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ScreenTranslator.Services;

/// <summary>
/// 框选识别区域(截图静止画方案,类似截图工具):
/// 进入时先截一张全屏快照,用一个**不透明**全屏窗口显示这张静止画面,用户在"冻结的屏幕"上拖选。
/// 为什么用快照而不是半透明覆盖:本机大窗口的透明合成都靠不住——
/// AllowsTransparency 分层窗口不上屏、整屏 Opacity 窗口触发"全屏优化"独立翻页(绕过 DWM,透明度丢失→整屏黑)。
/// 不透明窗口显示屏幕快照,视觉上看不到任何变化,从根上绕开所有合成路径;
/// 高亮框与提示条是普通子元素(元素级 alpha 合成由 WPF 内部完成,不受影响)。
/// 结果以物理像素虚拟屏坐标通过 RegionSelected 上报。
/// </summary>
public sealed class RegionSelectorWindow : Window
{
    /// <summary>拖选完成(尺寸 ≥ 16×16 物理像素)时触发,参数为物理像素虚拟屏坐标矩形。</summary>
    public event Action<Rect>? RegionSelected;

    private readonly Border _band;
    private readonly Border _hint;
    private Point _start;
    private bool _dragging;

    public RegionSelectorWindow()
    {
        // 1) 先截屏(覆盖层已在主流程外;调用方负责在打开本窗口前隐藏覆盖层/无需隐藏——
        //    框选时通常没有进行中的翻译)
        var snap = ScreenCaptureService.CaptureSnapshot();
        var frame = snap.Frame;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Background = Brushes.Black;
        Cursor = Cursors.Cross;
        Focusable = true;
        // 进程 system-DPI-aware,WPF 按 96DPI 处理:1 DIP = 1 物理像素,窗口尺寸直接用物理值
        Left = DisplayLayout.VirtualScreen.Left;
        Top = DisplayLayout.VirtualScreen.Top;
        Width = frame.Width;
        Height = frame.Height;

        var canvas = new Canvas { Width = frame.Width, Height = frame.Height };
        canvas.Children.Add(new Image
        {
            Source = frame.Source,
            Width = frame.Width,
            Height = frame.Height,
            Stretch = Stretch.Fill,
        });

        // 高亮框:元素级半透明填充(子元素 alpha 由 WPF 内部合成,不受窗口合成问题影响)
        _band = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x59, 0x0F, 0x6B, 0xBD)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x0F, 0x6B, 0xBD)),
            BorderThickness = new Thickness(2),
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };
        canvas.Children.Add(_band);

        // 顶部操作提示条
        _hint = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x30)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14, 8, 14, 8),
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = "按住左键拖选识别区域,Esc / 右键取消",
                Foreground = Brushes.White,
                FontSize = 15,
            },
        };
        _hint.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(_hint, (frame.Width - _hint.DesiredSize.Width) / 2);
        Canvas.SetTop(_hint, frame.Height * 0.3);
        canvas.Children.Add(_hint);

        Content = canvas;

        MouseLeftButtonDown += OnDown;
        MouseMove += OnMove;
        MouseLeftButtonUp += OnUp;
        MouseRightButtonDown += (_, _) => Close();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };

        // Source 持有独立像素拷贝(见 ReleaseFrame 注释),缓冲可直接归还
        ScreenCaptureService.ReleaseFrame(frame);
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
        _hint.Visibility = Visibility.Collapsed;
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        var p = e.GetPosition(this);
        Canvas.SetLeft(_band, Math.Min(_start.X, p.X));
        Canvas.SetTop(_band, Math.Min(_start.Y, p.Y));
        _band.Width = Math.Abs(p.X - _start.X);
        _band.Height = Math.Abs(p.Y - _start.Y);
        _band.Visibility = Visibility.Visible;
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
        if (rect.Width >= 16 && rect.Height >= 16)
            RegionSelected?.Invoke(rect);
        Close();
    }
}
