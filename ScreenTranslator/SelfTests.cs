using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScreenTranslator.Services;
using ScreenTranslator.Services.Ocr;
using ScreenTranslator.Services.Translate;

namespace ScreenTranslator;

/// <summary>
/// 命令行自检模式集合(从 App.xaml.cs 抽出):每个 --selftest-* 参数对应一个独立管线验证,
/// 结果写入输出目录的 *.txt / *.png,按约定退出码结束(0=通过,非0=失败)。
/// 返回 true 表示已处理某个自检模式(调用方不应再启动主窗口)。
/// </summary>
public static class SelfTests
{
    /// <summary>匹配到自检参数则执行并返回 true(内部已 Shutdown)。</summary>
    public static async Task<bool> TryRunAsync(string[] args)
    {
        var arg = args.FirstOrDefault(a => a.StartsWith("--"));
        switch (arg)
        {
            case "--diag": Diag_Mode(); return true;
            case "--selftest": await SelftestCaptureAsync(); return true;
            case "--selftest-ocr": await SelftestOcrAsync(); return true;
            case "--selftest-translate": await SelftestTranslateAsync(); return true;
            case "--selftest-diff": SelftestDiff(); return true;
            case "--selftest-ocr-paddle": await SelftestOcrPaddleAsync(); return true;
            case "--selftest-ocr-paddle-screen": await SelftestOcrPaddleScreenAsync(); return true;
            case "--selftest-ocr-hybrid": await SelftestOcrHybridAsync(); return true;
            case "--selftest-ocr-rapid": await SelftestOcrRapidAsync(); return true;
            case "--selftest-ocr-rapid-screen": await SelftestOcrRapidScreenAsync(); return true;
            case "--selftest-e2e": await SelftestE2EAsync(); return true;
            case "--selftest-overlay": SelftestOverlay(); return true;
            case "--selftest-demo": SelftestDemo(); return true;
            case "--selftest-ocr-crop": await SelftestOcrCropAsync(); return true;
            case "--selftest-ocr-screen": await SelftestOcrScreenAsync(); return true;
            case "--selftest-filter": SelftestFilter(); return true;
            default: return false;
        }
    }

    // ---------- 基础设施 ----------

    private static string Out(string name) => Path.Combine(AppContext.BaseDirectory, name);

    private static void WriteAndExit(string path, string content, int code)
    {
        try { File.WriteAllText(path, content); } catch { /* 忽略 */ }
        Application.Current.Shutdown(code);
    }

    /// <summary>运行一个"写结果文件"型自检,统一 try/catch + 退出码。</summary>
    private static async Task RunFileModeAsync(string fileName, Func<StringBuilder, Task<int>> body)
    {
        var path = Out(fileName);
        try
        {
            var sb = new StringBuilder();
            var code = await body(sb);
            WriteAndExit(path, sb.ToString(), code);
        }
        catch (Exception ex)
        {
            WriteAndExit(path, "FAIL " + ex, 1);
        }
    }

    // ---------- 各自检模式 ----------

    /// <summary>--diag:打印屏幕/DPI/显示器布局参数。</summary>
    private static void Diag_Mode()
    {
        var lines = new List<string>
        {
            $"VirtualScreen L={SystemParameters.VirtualScreenLeft} T={SystemParameters.VirtualScreenTop} W={SystemParameters.VirtualScreenWidth} H={SystemParameters.VirtualScreenHeight}",
            $"PrimaryScreen W={SystemParameters.PrimaryScreenWidth} H={SystemParameters.PrimaryScreenHeight}",
            $"Monitors={DisplayLayout.Monitors.Count}",
        };
        foreach (var m in DisplayLayout.Monitors)
            lines.Add($"  Monitor {m.DeviceName}: {m.Left},{m.Top} {m.Width}x{m.Height} dpi={m.DpiX} scale={m.Scale:F2}");
        WriteAndExit(Out("diag.txt"), string.Join("\r\n", lines), 0);
    }

    /// <summary>--selftest:截图 → 存 PNG,验证截图管线。</summary>
    private static async Task SelftestCaptureAsync()
    {
        await RunFileModeAsync("selftest.png.txt", _ =>
        {
            var path = Out("selftest.png");
            if (File.Exists(path)) File.Delete(path);
            var src = ScreenCaptureService.CaptureFrame().Source;
            ScreenCaptureService.SavePng(src, path);
            return Task.FromResult(0);
        });
    }

    /// <summary>--selftest-ocr:合成已知文本图 → Windows OCR → 校验行数。</summary>
    private static Task SelftestOcrAsync() => RunFileModeAsync("ocr_result.txt", async sb =>
    {
        var engine = new WindowsOcrEngine();
        var lines = await engine.RecognizeAsync(CreateTestFrame());
        sb.AppendLine($"engine_available={engine.IsAvailable} lines={lines.Count} expect=3");
        foreach (var l in lines)
            sb.AppendLine($"{l.X:F0},{l.Y:F0},{l.Width:F0}x{l.Height:F0} [{l.Score:F2}] {l.Text}");
        return lines.Count >= 3 ? 0 : 2;
    });

    /// <summary>--selftest-translate:缓存命中 + 离线降级 + 引擎降级链。</summary>
    private static Task SelftestTranslateAsync() => RunFileModeAsync("translate_result.txt", async sb =>
    {
        // 1) Echo 管道:重复文本只翻一次,第二次调用全缓存命中
        var pipe = new TranslationPipeline(new EchoTranslator());
        var texts = new[] { "Hello", "Hello", "World", "你好", "World" };
        var r1 = await pipe.TranslateAsync(texts, "EN", "ZH");
        var c1 = pipe.CacheCount;
        var r2 = await pipe.TranslateAsync(texts, "EN", "ZH");
        sb.AppendLine($"echo round1=[{string.Join(" | ", r1)}] cache={c1}->{pipe.CacheCount} expect=3->3");
        _ = r2;

        // 2) DeepLX 离线:连接失败 → 行级降级原样返回,管道不抛异常
        var pipe2 = new TranslationPipeline(new DeepLXTranslator());
        var r3 = await pipe2.TranslateAsync(new[] { "Hello world" }, "EN", "ZH");
        sb.AppendLine($"deeplx_offline_fallback=[{string.Join(" | ", r3)}] cache={pipe2.CacheCount}");

        // 3) 引擎降级:主引擎坏 → 备引擎接管
        var pipe3 = new TranslationPipeline(new BrokenTranslator(), new EchoTranslator());
        var r4 = await pipe3.TranslateAsync(new[] { "fallback test" }, "EN", "ZH");
        sb.AppendLine($"fallback_chain=[{string.Join(" | ", r4)}]");
        return 0;
    });

    /// <summary>--selftest-diff:合成两帧(左上+右下黑块),验证脏矩形输出。</summary>
    private static void SelftestDiff() => _ = RunFileModeAsync("diff_result.txt", sb =>
    {
        var a = MakeSolidFrame(800, 600, 255, 255, 255);
        var b = MakeSolidFrame(800, 600, 255, 255, 255);
        FillRect(b, 10, 10, 100, 100, 0, 0, 0);
        FillRect(b, 600, 400, 150, 120, 0, 0, 0);

        var fpA = ScreenDiff.Fingerprint(a);
        var fpB = ScreenDiff.Fingerprint(b);
        var prev = fpA.ToDictionary(f => (f.X, f.Y), f => f.Hash);
        var rects = ScreenDiff.DirtyRects(prev, fpB);
        sb.AppendLine($"dirty_rects={rects.Count} expect=2");
        foreach (var r in rects) sb.AppendLine($"  rect {r.X},{r.Y} {r.W}x{r.H}");
        sb.AppendLine($"same_frame_rects={ScreenDiff.DirtyRects(prev, fpA).Count} expect=0");
        return Task.FromResult(rects.Count == 2 ? 0 : 2);
    });

    /// <summary>--selftest-ocr-paddle:合成图 → PP-OCRv5 识别。</summary>
    private static Task SelftestOcrPaddleAsync() => RunFileModeAsync("paddle_ocr_result.txt", async sb =>
    {
        var engine = new PaddleOcrEngine();
        var lines = await engine.RecognizeAsync(CreateTestFrame());
        sb.AppendLine($"engine_available={engine.IsAvailable} lines={lines.Count} expect=3");
        foreach (var l in lines)
            sb.AppendLine($"{l.X:F0},{l.Y:F0},{l.Width:F0}x{l.Height:F0} [{l.Score:F3}] {l.Text}");
        return lines.Count >= 3 ? 0 : 2;
    });

    /// <summary>--selftest-ocr-paddle-screen:真实全屏 + 多种尺寸裁剪 → PP-OCRv5,摸清社区版可用输入规模。</summary>
    private static Task SelftestOcrPaddleScreenAsync() => RunFileModeAsync("paddle_screen_ocr_result.txt", async sb =>
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var frame = ScreenCaptureService.CaptureFrame();
        sw.Stop();
        sb.AppendLine($"capture_ms={sw.ElapsedMilliseconds} frame={frame.Width}x{frame.Height}");
        using var engine = new PaddleOcrEngine();
        sb.AppendLine($"available={engine.IsAvailable}");
        if (!engine.IsAvailable) return 2;

        // 全屏 + 若干代表性裁剪(演示页/窗口级/对话框级),定位"box sizes <100"限制的触发规模
        var cases = new List<(string Name, int X, int Y, int W, int H)>
        {
            ("full", 0, 0, frame.Width, frame.Height),
            ("crop_2000x1000", 300, 300, Math.Min(2000, frame.Width - 300), Math.Min(1000, frame.Height - 300)),
            ("crop_1200x780", 1300, 420, Math.Min(1200, frame.Width - 1300), Math.Min(780, frame.Height - 420)),
            ("crop_800x500", 300, 300, Math.Min(800, frame.Width - 300), Math.Min(500, frame.Height - 300)),
            ("crop_500x300", 600, 500, Math.Min(500, frame.Width - 600), Math.Min(300, frame.Height - 500)),
        };
        foreach (var c in cases)
        {
            if (c.W <= 0 || c.H <= 0) continue;
            var input = c.Name == "full" ? frame : FrameOps.Crop(frame, c.X, c.Y, c.W, c.H);
            var t = System.Diagnostics.Stopwatch.StartNew();
            var lines = await engine.RecognizeAsync(input);
            t.Stop();
            sb.AppendLine($"[{c.Name}] {c.W}x{c.H} ocr_ms={t.ElapsedMilliseconds} lines={lines.Count}");
            foreach (var l in lines.Take(c.Name == "full" ? 20 : 12))
                sb.AppendLine($"  {l.X + c.X:F0},{l.Y + c.Y:F0},{l.Width:F0}x{l.Height:F0} [{l.Score:F3}] {l.Text}");
        }
        return 0;
    });

    /// <summary>--selftest-ocr-hybrid:合成图应路由到 RapidOCR(高质量主路径),验证混合引擎。</summary>
    private static Task SelftestOcrHybridAsync() => RunFileModeAsync("hybrid_ocr_result.txt", async sb =>
    {
        using var engine = new HybridOcrEngine();
        var lines = await engine.RecognizeAsync(CreateTestFrame());
        sb.AppendLine($"engine_available={engine.IsAvailable} lines={lines.Count} expect=3");
        foreach (var l in lines)
            sb.AppendLine($"{l.X:F0},{l.Y:F0},{l.Width:F0}x{l.Height:F0} [{l.Score:F3}] {l.Text}");
        return lines.Count >= 3 ? 0 : 2;
    });

    /// <summary>--selftest-ocr-rapid:合成图 → RapidOCR(PP-OCRv6 small),验证多语模型识别质量。</summary>
    private static Task SelftestOcrRapidAsync() => RunFileModeAsync("rapid_ocr_result.txt", async sb =>
    {
        using var engine = new RapidOcrEngine();
        var t = System.Diagnostics.Stopwatch.StartNew();
        var lines = await engine.RecognizeAsync(CreateTestFrame());
        t.Stop();
        sb.AppendLine($"engine_available={engine.IsAvailable} lines={lines.Count} ocr_ms={t.ElapsedMilliseconds} expect=3");
        foreach (var l in lines)
            sb.AppendLine($"{l.X:F0},{l.Y:F0},{l.Width:F0}x{l.Height:F0} [{l.Score:F3}] {l.Text}");
        return lines.Count >= 3 ? 0 : 2;
    });

    /// <summary>--selftest-ocr-rapid-screen:真实全屏 → RapidOCR(PP-OCRv6 small),验证整屏可用性与耗时。</summary>
    private static Task SelftestOcrRapidScreenAsync() => RunFileModeAsync("rapid_screen_ocr_result.txt", async sb =>
    {
        var frame = ScreenCaptureService.CaptureFrame();
        using var engine = new RapidOcrEngine();
        var t = System.Diagnostics.Stopwatch.StartNew();
        var lines = await engine.RecognizeAsync(frame);
        t.Stop();
        sb.AppendLine($"frame={frame.Width}x{frame.Height} ocr_ms={t.ElapsedMilliseconds} lines={lines.Count} available={engine.IsAvailable}");
        foreach (var l in lines.Take(80))
            sb.AppendLine($"{l.X:F0},{l.Y:F0},{l.Width:F0}x{l.Height:F0} [{l.Score:F3}] {l.Text}");
        return engine.IsAvailable && lines.Count > 0 ? 0 : 2;
    });

    /// <summary>--selftest-e2e:合成帧 → OCR → 过滤 → 翻译(Echo)→ 渲染,全链路验证。</summary>
    private static Task SelftestE2EAsync() => RunFileModeAsync("e2e_result.txt", async sb =>
    {
        var frame = CreateTestFrame();
        var engine = new WindowsOcrEngine();
        var lines = await engine.RecognizeAsync(frame);
        var filtered = OcrLineFilter.Apply(lines, "ZH");
        var pipe = new TranslationPipeline(new EchoTranslator());
        var from = filtered.Any(l => OcrLineFilter.IsCjk(l.Text)) ? "ZH" : "EN";
        var translated = await pipe.TranslateAsync(filtered.Select(l => l.Text).ToList(), from, "ZH");
        var items = filtered.Select((l, i) => OcrOverlayRenderer.BuildOne(frame, l, translated[i])).ToList();

        sb.AppendLine($"lines={lines.Count} filtered={filtered.Count} translated={translated.Count} items={items.Count} from={from}");
        foreach (var (l, t) in filtered.Zip(translated))
            sb.AppendLine($"  [{l.X:F0},{l.Y:F0}] {l.Text} => {t}");
        var ok = filtered.Count >= 2 && items.Count == filtered.Count && items.All(i => i.W > 0 && i.H > 0);
        return ok ? 0 : 2;
    });

    /// <summary>--selftest-overlay:屏幕中央画测试块,5 秒后截屏验证悬浮窗真正上屏。</summary>
    private static void SelftestOverlay()
    {
        var path = Out("overlay_test.png");
        var log = Out("overlay_log.txt");
        try
        {
            var overlay = new OverlayManager();
            var vs = DisplayLayout.VirtualScreen;
            var item = new OcrOverlayRenderer.OverlayItem(
                vs.X + vs.Width / 2 - 320, vs.Y + vs.Height / 2 - 90, 640, 180,
                Color.FromArgb(235, 14, 14, 16),
                "OVERLAY TEST 悬浮窗测试 12345", Colors.White, Centered: true);
            overlay.SetItems(new[] { item });
            File.WriteAllText(log, "items set\n");

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(5000);
                    var frame = ScreenCaptureService.CaptureFrame();
                    ScreenCaptureService.SavePng(frame.Source, path);
                }
                catch (Exception ex) { File.AppendAllText(log, "STEPFAIL " + ex + "\n"); }
                Application.Current.Shutdown(0);
            });
        }
        catch (Exception ex)
        {
            try { File.AppendAllText(log, "CTORFAIL " + ex + "\n"); } catch { }
            Application.Current.Shutdown(1);
        }
    }

    /// <summary>--selftest-demo:主窗口 + 英文演示页 → 自动触发翻译 → 截屏验证覆盖块。</summary>
    private static void SelftestDemo()
    {
        var log = Out("demo_log.txt");
        try
        {
            var win = new MainWindow();
            win.Show();
            DemoWindow.Show();
            File.WriteAllText(log, "windows shown\n");

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(2500);
                    await win.Dispatcher.InvokeAsync(() => win.TriggerTranslate());
                    await Task.Delay(15000); // 全屏 OCR + 翻译 + 渲染
                    var frame = ScreenCaptureService.CaptureFrame();
                    ScreenCaptureService.SavePng(frame.Source, Out("demo_test.png"));
                    File.AppendAllText(log, "captured\n");
                }
                catch (Exception ex) { File.AppendAllText(log, "STEPFAIL " + ex + "\n"); }
                Application.Current.Shutdown(0);
            });
        }
        catch (Exception ex)
        {
            try { File.AppendAllText(log, "CTORFAIL " + ex + "\n"); } catch { }
            Application.Current.Shutdown(1);
        }
    }

    /// <summary>--selftest-ocr-crop:截全屏 → 裁剪演示窗口区域 → 直接识别(隔离切块逻辑)。</summary>
    private static Task SelftestOcrCropAsync() => RunFileModeAsync("crop_ocr_result.txt", async sb =>
    {
        var frame = ScreenCaptureService.CaptureFrame();
        int x = 1300, y = 420, w = 1200, h = 780;
        var crop = FrameOps.Crop(frame, x, y, w, h);
        var engine = new WindowsOcrEngine();
        var t = System.Diagnostics.Stopwatch.StartNew();
        var lines = await engine.RecognizeAsync(crop);
        t.Stop();
        sb.AppendLine($"crop {w}x{h} ocr_ms={t.ElapsedMilliseconds} lines={lines.Count}");
        foreach (var l in lines.Take(40))
            sb.AppendLine($"{l.X + x:F0},{l.Y + y:F0},{l.Width:F0}x{l.Height:F0} [{l.Score:F2}] {l.Text}");
        return 0;
    });

    /// <summary>--selftest-ocr-screen:真实全屏 → Windows OCR,输出行数/耗时/内容。</summary>
    private static Task SelftestOcrScreenAsync() => RunFileModeAsync("screen_ocr_result.txt", async sb =>
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var frame = ScreenCaptureService.CaptureFrame();
        sw.Stop();
        var engine = new WindowsOcrEngine();
        var t2 = System.Diagnostics.Stopwatch.StartNew();
        var lines = await engine.RecognizeAsync(frame);
        t2.Stop();
        sb.AppendLine($"capture_ms={sw.ElapsedMilliseconds} ocr_ms={t2.ElapsedMilliseconds} lines={lines.Count} available={engine.IsAvailable}");
        foreach (var l in lines.Take(60))
            sb.AppendLine($"{l.X:F0},{l.Y:F0},{l.Width:F0}x{l.Height:F0} [{l.Score:F2}] {l.Text}");
        return lines.Count > 0 ? 0 : 2;
    });

    /// <summary>--selftest-filter:日文不被误杀、垃圾文本被过滤、段落聚合。</summary>
    private static void SelftestFilter() => _ = RunFileModeAsync("filter_result.txt", sb =>
    {
        var lines = new List<OcrLine>
        {
            new("这是中文测试文本", 10, 10, 200, 30, 1.0),
            new("こんにちは世界", 10, 50, 200, 30, 1.0),
            new("冒険者ギルドへようこそ", 10, 90, 260, 30, 1.0),
            new("Hello world this is english", 10, 130, 300, 30, 1.0),
            new("NPC: We need your help", 10, 160, 300, 30, 1.0),
            new("12345", 10, 170, 100, 30, 1.0),
            new("F5", 10, 210, 50, 30, 1.0),
            new("http://127.0.0.1:1188", 10, 250, 300, 30, 1.0),
            new("C:\\tools\\demo.exe", 10, 260, 300, 30, 1.0),
            new("한국어 테스트", 10, 290, 200, 30, 1.0),
        };
        var zh = OcrLineFilter.Apply(lines, "ZH");
        sb.AppendLine($"target=ZH kept={zh.Count} expect=5: " + string.Join(" | ", zh.Select(l => l.Text)));
        sb.AppendLine($"IsPathLike(NPC对话)={OcrLineFilter.IsPathLike("NPC: We need your help")} expect=False");
        sb.AppendLine($"IsChinese(日文汉字)={OcrLineFilter.IsChinese("冒険者ギルドへようこそ")} expect=False");
        sb.AppendLine($"DetectSourceLang(こんにちは)={OcrLineFilter.DetectSourceLang("こんにちは")} expect=JA");
        sb.AppendLine($"DetectSourceLang(hello)={OcrLineFilter.DetectSourceLang("Hello world")} expect=EN");

        var paras = LineGrouping.Group(new List<OcrLine>
        {
            new("I was wondering if you could", 10, 10, 300, 30, 1.0),
            new("help me with this quest", 10, 44, 250, 30, 1.0),
            new("A completely different line", 10, 200, 300, 30, 1.0),
        });
        sb.AppendLine($"paragraphs={paras.Count} expect=2");
        foreach (var p in paras) sb.AppendLine($"  para[{p.Lines.Count}] {p.Text.Replace("\n", "\\n")}");

        var ok = zh.Count == 5 && paras.Count == 2 && !OcrLineFilter.IsChinese("冒険者ギルドへようこそ");
        return Task.FromResult(ok ? 0 : 2);
    });

    // ---------- 合成帧工具 ----------

    /// <summary>合成纯色帧(BGRA)。</summary>
    public static PixelFrame MakeSolidFrame(int w, int h, byte r, byte g, byte b)
    {
        var pixels = new byte[w * h * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = b; pixels[i + 1] = g; pixels[i + 2] = r; pixels[i + 3] = 255;
        }
        var src = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, w * 4);
        return new PixelFrame(src, pixels, w, h);
    }

    /// <summary>在帧上填一个矩形(BGRA 像素直写)。</summary>
    public static void FillRect(PixelFrame f, int x, int y, int w, int h, byte r, byte g, byte b)
    {
        for (var yy = y; yy < y + h; yy++)
        {
            for (var xx = x; xx < x + w; xx++)
            {
                if (xx < 0 || yy < 0 || xx >= f.Width || yy >= f.Height) continue;
                var i = (yy * f.Width + xx) * 4;
                f.Pixels[i] = b; f.Pixels[i + 1] = g; f.Pixels[i + 2] = r; f.Pixels[i + 3] = 255;
            }
        }
    }

    /// <summary>合成 800x300 白底三行黑字测试图(中英混合),用于确定性验证 OCR。</summary>
    public static PixelFrame CreateTestFrame()
    {
        const int w = 800, h = 300;
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h));
            dc.DrawText(MakeText("Hello ScreenTranslator 12345", 36), new Point(40, 40));
            dc.DrawText(MakeText("屏幕翻译测试 ABCDEFG", 36), new Point(40, 120));
            dc.DrawText(MakeText("line three 999", 36), new Point(40, 200));
        }
        rtb.Render(dv);

        var pixels = new byte[w * h * 4];
        rtb.CopyPixels(pixels, w * 4, 0);
        return new PixelFrame(rtb, pixels, w, h);
    }

    private static FormattedText MakeText(string s, double size) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Microsoft YaHei"), size, Brushes.Black, 1.0);

    /// <summary>测试用:永远失败的引擎(验证降级链)。</summary>
    private sealed class BrokenTranslator : ITranslator
    {
        public string Name => "Broken";
        public Task<IReadOnlyList<string?>> TranslateAsync(IReadOnlyList<string> texts, string from, string to,
            CancellationToken ct = default)
            => throw new InvalidOperationException("engine down");
    }
}

/// <summary>英文演示页窗口(MainWindow 的"演示"按钮与 --selftest-demo 共用)。</summary>
public static class DemoWindow
{
    private static readonly string[] Lines =
    {
        "Hello Screen Translator",
        "This is a live demo page",
        "English text will be covered",
        "with Chinese subtitles",
        "Translate me please 12345",
    };

    public static void Show()
    {
        var demo = new Window
        {
            Title = "翻译演示页(英文测试文本)",
            Width = 760,
            Height = 480,
            // 放到屏幕右侧,避开居中的 app 主窗口(避免 OCR 结果被 app 区域过滤误杀)
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = SystemParameters.PrimaryScreenWidth - 760 - 60,
            Top = (SystemParameters.PrimaryScreenHeight - 480) / 2,
            Background = Brushes.White,
            WindowStyle = WindowStyle.ToolWindow,
            ShowActivated = false,
            Topmost = true, // 确保不被其他窗口遮挡
        };
        var sp = new StackPanel { Margin = new Thickness(40), VerticalAlignment = VerticalAlignment.Center };
        foreach (var t in Lines)
        {
            sp.Children.Add(new TextBlock
            {
                Text = t,
                FontSize = 28,
                Foreground = Brushes.Black,
                Margin = new Thickness(0, 10, 0, 10),
            });
        }
        demo.Content = sp;
        demo.Show();
    }
}
