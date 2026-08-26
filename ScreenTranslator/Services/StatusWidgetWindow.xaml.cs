using System.Windows;
using System.Windows.Input;

namespace ScreenTranslator.Services;

/// <summary>
/// 灰色半透明悬浮状态框:游戏时查看翻译进度(识别/翻译/完成耗时)+ 快捷切换渲染方式和目标语言。
/// 拖动任意空白处移动;自身区域会被主窗口加入 OCR/覆盖层排除列表,不参与识别。
/// </summary>
public sealed partial class StatusWidgetWindow : Window
{
    /// <summary>点击"渲染方式"按钮(字幕 ↔ 背景采样)。</summary>
    public event Action? StyleToggleRequested;

    /// <summary>点击"目标语言"按钮(中 → 英 → 日 循环)。</summary>
    public event Action? LangCycleRequested;

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

    /// <summary>更新渲染方式按钮文字。</summary>
    public void SetStyleLabel(bool isSubtitle)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => SetStyleLabel(isSubtitle)); return; }
        BtnStyle.Content = isSubtitle ? "字幕" : "背景";
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

    private void OnDragStart(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void OnStyleClick(object sender, RoutedEventArgs e) => StyleToggleRequested?.Invoke();

    private void OnLangClick(object sender, RoutedEventArgs e) => LangCycleRequested?.Invoke();
}
