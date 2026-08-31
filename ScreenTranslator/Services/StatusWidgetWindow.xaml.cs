using System.Windows;
using System.Windows.Input;

namespace ScreenTranslator.Services;

/// <summary>
/// 灰色半透明悬浮状态框:游戏时查看翻译进度(识别/翻译/完成耗时)+ 快捷操作(立即翻译/自动开关/目标语言)。
/// 拖动任意空白处移动;自身区域会被主窗口加入 OCR/覆盖层排除列表,不参与识别。
/// </summary>
public sealed partial class StatusWidgetWindow : Window
{
    /// <summary>点击"目标语言"按钮(中 → 英 → 日 循环)。</summary>
    public event Action? LangCycleRequested;

    /// <summary>点击"▶"按钮(立即翻译一次)。</summary>
    public event Action? TriggerRequested;

    /// <summary>点击"选区/清选"按钮(进入框选,或清除已有选区)。</summary>
    public event Action? RegionButtonRequested;

    /// <summary>点击"自动"按钮(开关自动触发)。</summary>
    public event Action? AutoToggleRequested;

    /// <summary>点击"×"按钮(隐藏悬浮框)。</summary>
    public event Action? HideRequested;

    public StatusWidgetWindow()
    {
        InitializeComponent();
        // 默认位置:主屏工作区右上角(物理排除框由主窗口按实际位置计算,拖动后自动跟随)
        var wa = SystemParameters.WorkArea;
        Left = wa.Right - Width - 16;
        Top = wa.Top + 16;
    }

    /// <summary>设置状态文本(任意线程可调用)。</summary>
    public void SetStatus(string text)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => SetStatus(text)); return; }
        StatusText.Text = text;
    }

    /// <summary>更新目标语言按钮文字(如 "→中")。</summary>
    public void SetLangLabel(string langTag)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => SetLangLabel(langTag)); return; }
        BtnLang.Content = langTag switch
        {
            "ZH" => "→中",
            "EN" => "→英",
            "JA" => "→日",
            _ => "→?",
        };
    }

    /// <summary>更新自动触发按钮状态(开=高亮蓝底,关=灰底)。</summary>
    public void SetAutoState(bool isOn)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => SetAutoState(isOn)); return; }
        BtnAuto.Content = isOn ? "自动✓" : "自动";
        BtnAuto.Background = new System.Windows.Media.SolidColorBrush(
            isOn ? System.Windows.Media.Color.FromRgb(0x0F, 0x6B, 0xBD)
                 : System.Windows.Media.Color.FromRgb(0x4A, 0x4A, 0x4F));
    }

    /// <summary>更新选区按钮文字(已选区显示"清选")。</summary>
    public void SetRegionState(bool hasRegion)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => SetRegionState(hasRegion)); return; }
        BtnRegion.Content = hasRegion ? "清选" : "选区";
    }

    /// <summary>设置选区模式译文(null/空 = 收起面板,窗口自动缩回单行)。</summary>
    public void SetTranslation(string? text)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => SetTranslation(text)); return; }
        if (string.IsNullOrWhiteSpace(text))
        {
            TranslationPanel.Visibility = Visibility.Collapsed;
            TranslationText.Text = "";
        }
        else
        {
            TranslationText.Text = text;
            TranslationPanel.Visibility = Visibility.Visible;
        }
    }

    private void OnDragStart(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void OnLangClick(object sender, RoutedEventArgs e) => LangCycleRequested?.Invoke();

    private void OnTriggerClick(object sender, RoutedEventArgs e) => TriggerRequested?.Invoke();

    private void OnRegionClick(object sender, RoutedEventArgs e) => RegionButtonRequested?.Invoke();

    private void OnAutoClick(object sender, RoutedEventArgs e) => AutoToggleRequested?.Invoke();

    private void OnHideClick(object sender, RoutedEventArgs e) => HideRequested?.Invoke();
}
