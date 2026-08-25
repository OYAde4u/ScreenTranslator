using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScreenTranslator.Services;
using ScreenTranslator.Services.Ocr;
using ScreenTranslator.Services.Translate;

namespace ScreenTranslator;

public partial class MainWindow : Window
{
    private readonly object _gate = new();
    private readonly OverlayManager _overlay = new();
    private readonly HotKeyService _hotkey;
    private readonly IOcrEngine _ocr;
    private readonly TranslationPipeline _pipeline;
    private AutoTriggerService? _autoTrigger;
    private Dictionary<(int X, int Y), long> _prevFingerprints = new();
    private string _targetLang = "ZH";
    private OcrOverlayRenderer.RenderStyle _renderStyle = OcrOverlayRenderer.RenderStyle.Subtitle;

    /// <summary>应用自身窗口区域(物理像素,虚拟屏幕坐标系):覆盖层不绘制、OCR 不识别、脏区不算变化。</summary>
    private Rect _appRect = Rect.Empty;

    private bool _busy;
    private bool _pending;

    /// <summary>截图前隐藏覆盖层后,等待合成器将其移出画面的时间。</summary>
    private const int OverlayHideDelayMs = 35;

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    public MainWindow()
    {
        InitializeComponent();
        // 主引擎:HybridOcrEngine——全部区域优先 RapidOCR(PP-OCRv6 small 多语,ONNX,无社区版框大小限制,整屏高质量);
        // 模型缺失/识别异常时自动回退 Windows.Media.Ocr(系统自带、整屏稳定兜底)。
        _ocr = new HybridOcrEngine();
        Diag.Dump($"engine: {_ocr.GetType().Name} available={_ocr.IsAvailable}");
        // 翻译链:DeepLX(高质量,需自部署)→ Edge(免费批量,国内直连)→ MyMemory(质量一般)→ Echo(原样兜底)
        _pipeline = new TranslationPipeline(new DeepLXTranslator(), new EdgeTranslator(),
            new MyMemoryTranslator(), new EchoTranslator());
        _hotkey = new HotKeyService(this, TranslateOnce);
        Loaded += (_, _) =>
        {
            _overlay.SyncWindows();
            UpdateAppRect();
            var hotkeyOk = _hotkey.Register();
            Log($"热键:{(hotkeyOk ? "已注册 Ctrl+Shift+T" : "注册失败(可能被其他程序占用),请用按钮触发!")};"
                + $"OCR:{( _ocr.IsAvailable ? _ocr.GetType().Name : "不可用!")};"
                + $"显示器:{DisplayLayout.Monitors.Count} 块;目标:{_targetLang}");
        };
        LocationChanged += (_, _) => UpdateAppRect();
        SizeChanged += (_, _) => UpdateAppRect();
        StateChanged += (_, _) => UpdateAppRect();
        Closed += (_, _) =>
        {
            _hotkey.Dispose();
            _autoTrigger?.Dispose();
            _overlay.Dispose();
            (_ocr as IDisposable)?.Dispose();
        };
    }

    private void UpdateAppRect()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || WindowState == WindowState.Minimized || !IsVisible ||
            !GetWindowRect(hwnd, out var r))
        {
            _appRect = Rect.Empty;
        }
        else
        {
            var vs = DisplayLayout.VirtualScreen;
            _appRect = new Rect(r.Left - vs.X, r.Top - vs.Y, r.Right - r.Left, r.Bottom - r.Top);
        }
        _overlay.AppExcludeRect = _appRect;
    }

    /// <summary>主流程:截图(先隐藏覆盖层)→ 脏区 diff → 区域 OCR → 翻译 → 覆盖渲染。全部重活在后台线程。</summary>
    private async void TranslateOnce()
    {
        Diag.Dump("TranslateOnce: enter");
        lock (_gate)
        {
            if (_busy)
            {
                _pending = true; // 运行中再触发:合并为一次待办,不叠加并发
                return;
            }
            _busy = true;
        }
        try
        {
            await RunPipelineAsync();
        }
        catch (Exception ex)
        {
            Diag.Dump("TranslateOnce EXC: " + ex);
            Log("失败:" + ex.Message);
        }
        finally
        {
            _overlay.ShowAll();
            bool again;
            lock (_gate)
            {
                _busy = false;
                again = _pending;
                _pending = false;
            }
            if (again) TranslateOnce();
        }
    }

    private async Task RunPipelineAsync()
    {
        Diag.Dump($"pipeline: start appRect={_appRect}");
        // 1) 截图前隐藏覆盖层:覆盖层(分层窗口)会被 BitBlt 捕获,若不隐藏会形成"翻译自身输出"的反馈循环
        _overlay.HideAll();
        if (_overlay.HasItems) await Task.Delay(OverlayHideDelayMs);

        // 2) 后台:逐显示器截图(副屏/多 GPU 也完整)+ 分块指纹 + 脏区
        PixelFrame? frame = null;
        try
        {
            var snap = await Task.Run(ScreenCaptureService.CaptureSnapshot);
            frame = snap.Frame;
            Log($"截图 {frame.Width}x{frame.Height},识别…");

            var dirty = await Task.Run(() =>
            {
                var fps = ScreenDiff.Fingerprint(frame);
                var rects = ScreenDiff.DirtyRects(_prevFingerprints, fps);
                _prevFingerprints = fps.ToDictionary(f => (f.X, f.Y), f => f.Hash);
                return rects;
            });
            Log($"变化 {dirty.Count} 区");

            // 3) 脏区决定 OCR 范围:无变化跳过;小区域只裁剪识别(全屏 OCR 是最重的一步)
            var regions = BuildOcrRegions(dirty, _appRect, frame.Width, frame.Height, out var fullFrame);
            if (regions.Count == 0)
            {
                Log("无有效变化,跳过识别");
                return;
            }

            var lines = new List<OcrLine>();
            foreach (var (rx, ry, rw, rh) in regions)
            {
                // 全屏时直接用原始帧(避免 CropFrame 拷贝;也便于对照定位)
                PixelFrame ocrInput = (rx == 0 && ry == 0 && rw == frame.Width && rh == frame.Height)
                    ? frame
                    : FrameOps.Crop(frame, rx, ry, rw, rh);
                long bsum = 0, n = 0;
                for (var i = 0; i < ocrInput.Pixels.Length && n < 200000; i += 4, n++) bsum += ocrInput.Pixels[i];
                Diag.Dump($"ocrInput {ocrInput.Width}x{ocrInput.Height} avgB={(n > 0 ? bsum / n : -1)}");
                var ls = await _ocr.RecognizeAsync(ocrInput);
                foreach (var l in ls)
                    lines.Add(l with { X = l.X + rx, Y = l.Y + ry });
            }
            Log($"识别 {lines.Count} 行({(fullFrame ? "全屏" : regions.Count + " 块区域")}),过滤…");
            Diag.Dump($"ocr lines={lines.Count}: {string.Join(" | ", lines.Select(l => l.Text).Take(25))}");

            var filtered = OcrLineFilter.Apply(lines, _targetLang,
                ignoreRects: _appRect.IsEmpty ? null :
                    new[] { ((int)_appRect.X, (int)_appRect.Y, (int)_appRect.Width, (int)_appRect.Height) });
            Diag.Dump($"after filter={filtered.Count}: {string.Join(" | ", filtered.Select(l => l.Text).Take(25))}");

            if (filtered.Count == 0)
            {
                _overlay.Clear();
                Log(lines.Count == 0
                    ? "未识别到文字:请确认屏幕上有文字内容(全屏 OCR 需几秒,请稍候)"
                    : $"识别 {lines.Count} 行但均无需翻译(多为已是目标语言):屏幕上需要有外文内容才会出现覆盖块,或切换目标语言试试");
                return;
            }
            Log($"过滤后 {filtered.Count} 行,翻译…");

            // 段落聚合:一句话被 OCR 拆成多行时合并为段落(行间 \n 连接),整段一次请求,
            // 译文按 \n 拆回——上下文连贯,通顺度大幅提升(对应架构 §2.5 批量请求)。
            var translated = await TranslateParagraphsAsync(filtered);
            Diag.Dump($"translated={translated.Count}: {string.Join(" | ", translated.Take(25))}");

            // 4) 后台:生成覆盖图元(字幕底块/背景采样;CPU 密集,不占 UI 线程)
            // 逐行跳过"译文 == 原文"的行(引擎失败的行保留原文):用原文覆盖原文没有意义,
            // 只给真正翻出来的行画块;全部未翻出时状态栏提示,不画任何块。
            var pairs = filtered.Zip(translated)
                .Where(p => !string.Equals(p.Second, p.First.Text, StringComparison.Ordinal))
                .ToList();
            if (pairs.Count == 0)
            {
                _overlay.Clear();
                Log("翻译服务不可用(DeepLX/Edge/MyMemory 均失败),未覆盖原文——请检查网络后重试");
                return;
            }
            if (pairs.Count < filtered.Count)
                Log($"提示:{filtered.Count - pairs.Count} 行未翻出(保留原文不覆盖)");

            var items = await Task.Run(() =>
                pairs
                    .Select(p => OcrOverlayRenderer.BuildOne(frame, p.First, p.Second, _renderStyle))
                    .ToList());

            // 5) UI:增量渲染覆盖层(自动跳过应用窗口区域;内容未变时内部跳过重建)
            _overlay.SetItems(items);
            Diag.Dump($"rendered={items.Count}: " + string.Join(" | ",
                items.Select(i => $"({i.X:F0},{i.Y:F0},{i.W:F0}x{i.H:F0}){i.Text}").Take(20)));

            // 翻译引擎状态提示(如 DeepLX 限流/未启动、Edge 故障)
            var engineMsg = (_pipeline.PrimaryTranslator as DeepLXTranslator)?.LastError
                ?? (_pipeline.Engines.OfType<EdgeTranslator>().FirstOrDefault()?.LastError);
            if (engineMsg != null)
            {
                Log($"翻译引擎提示:{engineMsg}(已自动切换备用翻译)");
                Diag.Dump($"translator: {engineMsg}");
            }

            // 结果列表(调试:原文 → 译文 + 坐标)
            OcrList.ItemsSource = filtered.Zip(translated)
                .Take(80)
                .Select(p => $"({p.First.X:F0},{p.First.Y:F0}) {p.First.Text} => {p.Second}").ToList();

            // 预览:后台降采样(全屏位图直接赋给 Image 会触发整幅 GPU 上传)
            var preview = await Task.Run(() => MakePreview(frame.Source, 480));
            PreviewImage.Source = preview;

            // 调试截图(可选,默认关:每帧全屏 PNG 编码是卡顿来源之一,移出 UI 线程)
            if (ChkSaveShot.IsChecked == true)
            {
                var shotDir = Path.Combine(AppContext.BaseDirectory, "shots");
                Directory.CreateDirectory(shotDir);
                var file = Path.Combine(shotDir, $"shot_{DateTime.Now:HHmmss_fff}.png");
                _ = Task.Run(() =>
                {
                    try { ScreenCaptureService.SavePng(frame.Source, file); } catch { /* 忽略 */ }
                });
                Log($"完成:{filtered.Count} 行已覆盖,缓存 {_pipeline.CacheCount} 条,截图 {Path.GetFileName(file)}");
            }
            else
            {
                Log($"完成:{filtered.Count} 行已覆盖,缓存 {_pipeline.CacheCount} 条");
            }
        }
        finally
        {
            if (frame is not null) ScreenCaptureService.ReleaseFrame(frame);
        }
    }

    /// <summary>
    /// 段落聚合翻译:OCR 行按几何关系聚合成段落(一句话拆成多行时合并),
    /// 段落按源语言分组(日/韩/中/英各自检测),每组一次批量请求;
    /// 译文按换行拆回与行一一对应(行数不匹配时回退原文,保证不丢内容)。
    /// </summary>
    private async Task<IReadOnlyList<string>> TranslateParagraphsAsync(List<OcrLine> filtered)
    {
        var result = new Dictionary<OcrLine, string>();
        var paragraphs = LineGrouping.Group(filtered);

        // 按源语言分组:每段独立检测(含假名→JA,含韩文→KO,汉字主导→ZH,否则 EN)
        foreach (var langGroup in paragraphs.GroupBy(p => OcrLineFilter.DetectSourceLang(p.Text)))
        {
            var from = langGroup.Key;
            var paraTexts = langGroup.Select(p => p.Text).ToList();
            var translated = await _pipeline.TranslateAsync(paraTexts, from, _targetLang);

            foreach (var (para, tr) in langGroup.Zip(translated))
            {
                var parts = (tr ?? string.Empty).Split('\n');
                if (parts.Length == para.Lines.Count)
                {
                    for (var i = 0; i < parts.Length; i++)
                        result[para.Lines[i]] = parts[i].Trim();
                }
                else
                {
                    // 译文行数不匹配(引擎偶发丢/并换行):逐行重试(走管道行级缓存),仍失败的行保留原文、不渲染
                    Diag.Dump($"paragraph split mismatch: lines={para.Lines.Count} parts={parts.Length}, per-line retry");
                    var lineTexts = para.Lines.Select(l => l.Text).ToList();
                    var lineTr = await _pipeline.TranslateAsync(lineTexts, from, _targetLang);
                    for (var i = 0; i < para.Lines.Count; i++)
                        result[para.Lines[i]] = (lineTr[i] ?? para.Lines[i].Text).Trim();
                }
            }
        }

        return filtered.Select(l => result.TryGetValue(l, out var t) ? t : l.Text).ToList();
    }

    /// <summary>
    /// 由脏区构造 OCR 区域:丢弃应用窗口引起的脏区;小变化合并为一条"全宽横带"(x=0,宽=整屏,只裁垂直方向)。
    /// 为什么全宽:文本行是水平的,bounding-box 裁剪会把长行横向切成碎片(如 "and up the road, he wa"),
    /// 碎片翻译破碎、覆盖块只盖住半行;全宽横带保证不横向切断任何一行,纵向切边由 margin 保护。
    /// 成本可控:RapidOCR 检测有 MaxSideLen=2000 硬上限 + 宽高比 8 信箱化,横带耗时按高度比例缩放。
    /// 带高超过半屏时退回全屏。
    /// </summary>
    private static List<(int X, int Y, int W, int H)> BuildOcrRegions(
        List<(int X, int Y, int W, int H)> dirty, Rect appRect, int fw, int fh, out bool full)
    {
        full = false;
        // 丢弃"几乎完全落在 app 窗口内"的脏区(如 app 自身状态栏变化);
        // 注意不能用"中心在窗口内"判断——全屏脏区合并后中心恰在居中的 app 窗口内,会把全屏误杀。
        var relevant = dirty
            .Where(r => appRect.IsEmpty || IntersectRatio(r, appRect) < 0.5)
            .ToList();
        // 兜底:全部被过滤时(理论上不应发生)退回全屏,保证 OCR 一定会执行
        if (relevant.Count == 0) return new List<(int, int, int, int)> { (0, 0, fw, fh) };

        const int margin = 32; // 纵向边距:保护带顶/带底的完整文本行(常见行高 ≤32px)
        var y0 = Math.Max(0, relevant.Min(r => r.Y) - margin);
        var y1 = Math.Min(fh, relevant.Max(r => r.Y + r.H) + margin);
        if ((long)fw * (y1 - y0) > fw * (long)fh / 2)
        {
            full = true;
            return new List<(int, int, int, int)> { (0, 0, fw, fh) };
        }
        return new List<(int, int, int, int)> { (0, y0, fw, y1 - y0) };
    }

    /// <summary>脏区与矩形交集面积占脏区面积的比例(0~1)。</summary>
    private static double IntersectRatio((int X, int Y, int W, int H) r, Rect rect)
    {
        var ix = Math.Max(0, Math.Min(r.X + r.W, rect.X + rect.Width) - Math.Max(r.X, rect.X));
        var iy = Math.Max(0, Math.Min(r.Y + r.H, rect.Y + rect.Height) - Math.Max(r.Y, rect.Y));
        var area = r.W * (double)r.H;
        return area <= 0 ? 0 : ix * iy / area;
    }

    /// <summary>预览降采样(等比缩小到 maxWidth,冻结以便跨线程使用)。</summary>
    private static BitmapSource MakePreview(BitmapSource source, int maxWidth)
    {
        var scale = Math.Min(1.0, maxWidth / (double)source.PixelWidth);
        if (scale >= 1.0) return source;
        var tb = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        tb.Freeze();
        return tb;
    }

    private void OnTestClick(object sender, RoutedEventArgs e) => TranslateOnce();

    /// <summary>公开触发一次翻译(自检/自动化用)。</summary>
    public void TriggerTranslate() => TranslateOnce();

    /// <summary>演示:弹出一个英文测试页窗口,稍后自动触发翻译,立刻看到悬浮覆盖效果。</summary>
    private void OnDemoClick(object sender, RoutedEventArgs e)
    {
        DemoWindow.Show();
        Log("演示页已弹出,1.5 秒后自动翻译…");

        // 等演示窗口渲染完成再触发(首次全屏 OCR 需数秒,请稍候)
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1500),
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            TranslateOnce();
        };
        timer.Start();
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        _overlay.Clear();
        _prevFingerprints = new Dictionary<(int X, int Y), long>();
        Log("覆盖层已清除,基线重置");
    }

    private void OnAutoChecked(object sender, RoutedEventArgs e)
    {
        _autoTrigger = new AutoTriggerService(TranslateOnce);
        _autoTrigger.Start();
        Log("自动触发已开启(点击/滚轮/按键)");
    }

    private void OnAutoUnchecked(object sender, RoutedEventArgs e)
    {
        _autoTrigger?.Dispose();
        _autoTrigger = null;
        Log("自动触发已关闭");
    }

    private void OnLangChanged(object sender, RoutedEventArgs e)
    {
        // XAML 初始化期间(StatusText 未就绪)也会触发本事件,判空保护
        if (LangBox.SelectedItem is ComboBoxItem item && item.Tag is string lang)
        {
            _targetLang = lang;
            Log($"目标语言:{lang}");
        }
    }

    private void OnStyleChanged(object sender, RoutedEventArgs e)
    {
        if (StyleBox.SelectedItem is ComboBoxItem item && item.Tag is string style)
        {
            _renderStyle = style == "Background"
                ? OcrOverlayRenderer.RenderStyle.Background
                : OcrOverlayRenderer.RenderStyle.Subtitle;
            Log($"渲染方式:{( _renderStyle == OcrOverlayRenderer.RenderStyle.Subtitle ? "字幕底块" : "背景采样")}");
        }
    }

    private void Log(string msg)
    {
        // XAML 初始化期间 StatusText 可能尚未创建
        if (StatusText != null) StatusText.Text = $"{DateTime.Now:HH:mm:ss.fff} {msg}";
    }

}
