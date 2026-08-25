using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace ScreenTranslator.Services.Ocr;

/// <summary>
/// Windows.Media.Ocr 实现(WinRT 系统自带,离线可用,零模型文件,无 Paddle 社区版框大小限制)。
/// 识别策略:输入放大 2 倍再识别(Windows OCR 对小字识别差,放大后精度显著提升);
/// 放大后超过引擎 MaxImageDimension 时自动切块(带重叠,结果去重合并)。
/// 注意:引擎创建/调用需在 STA 线程(UI 线程调用本方法)。
/// </summary>
public sealed class WindowsOcrEngine : IOcrEngine
{
    private readonly OcrEngine? _engine;

    public WindowsOcrEngine()
    {
        _engine = OcrEngine.TryCreateFromUserProfileLanguages();
    }

    public bool IsAvailable => _engine != null;

    public async Task<List<OcrLine>> RecognizeAsync(PixelFrame frame, CancellationToken ct = default)
    {
        var result = new List<OcrLine>();
        if (_engine is null) return result;

        var maxDim = OcrEngine.MaxImageDimension;
        Diag.Dump($"windows-ocr maxDim={maxDim} input={frame.Width}x{frame.Height}");

        // 放大倍数:保证放大后的块 ≤ 引擎上限;上限过小时退回 1x
        var zoom = maxDim >= 1900 ? 2 : 1;
        // 块尺寸(原始像素):硬上限 1100(放大后 ≤2200)。
        // 注:即使 MaxImageDimension 很大(实测 10000),也不能整图放大识别——
        // 超大输入(如 5120x3200)会导致 OCR 漏检部分区域(实测整屏大图漏掉演示窗口文本)。
        var block = Math.Min(1100, Math.Max(300, (int)(maxDim / zoom) - 64));
        const int overlap = 48;                          // 块重叠(避免切断文本行)

        // 统一切块(小图 = 单块,等价整体识别):避免"整体放大后仍超限"导致 WinRT 挂起
        var acc = new List<OcrLine>();
        var stepX = block - overlap;
        var stepY = block - overlap;
        for (var by = 0; by < frame.Height; by += stepY)
        {
            for (var bx = 0; bx < frame.Width; bx += stepX)
            {
                ct.ThrowIfCancellationRequested();
                var bw = Math.Min(block, frame.Width - bx);
                var bh = Math.Min(block, frame.Height - by);
                if (bw <= 0 || bh <= 0) continue;

                var crop = FrameOps.Crop(frame, bx, by, bw, bh);
                var up = ScaleUp(crop, zoom);
                var prep = await Task.Run(() => Preprocess(up));
                var ls = await RecognizeScaledWithTimeoutAsync(prep, zoom, bx, by);
                MergeLines(acc, ls);
            }
        }
        // 半行碎片拼接:切块边界把一行切成两半时,按"同水平线 + x 相邻"拼回整行
        acc = JoinSplits(acc);
        acc.Sort((a, b) => a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.X.CompareTo(b.X));
        return acc;
    }

    /// <summary>
    /// 把同一水平线上的碎片行拼接为整行:y 区间重叠 &gt;60%、x 相邻(间距 ≤12px)、行高相近。
    /// 解决切块边界"半行重复/半行碎片"导致的破碎行与双覆盖块。
    /// </summary>
    private static List<OcrLine> JoinSplits(List<OcrLine> lines)
    {
        var sorted = lines.OrderBy(l => l.Y).ThenBy(l => l.X).ToList();
        var result = new List<OcrLine>(sorted.Count);
        OcrLine? cur = null;
        foreach (var l in sorted)
        {
            if (cur != null && CanJoin(cur, l))
            {
                cur = Join(cur, l);
            }
            else
            {
                if (cur is not null) result.Add(cur);
                cur = l;
            }
        }
        if (cur is not null) result.Add(cur);
        return result;
    }

    private static bool CanJoin(OcrLine a, OcrLine b)
    {
        if (b.X < a.X) return false; // 已按 X 排序,b 在右侧
        var yOverlap = Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y);
        var minH = Math.Min(a.Height, b.Height);
        if (minH <= 0 || yOverlap / minH < 0.6) return false;
        var gap = b.X - (a.X + a.Width);
        if (gap < -4 || gap > 12) return false;
        if (Math.Abs(a.Height - b.Height) / minH > 0.5) return false;
        return true;
    }

    private static OcrLine Join(OcrLine a, OcrLine b)
    {
        var x = Math.Min(a.X, b.X);
        var y = Math.Min(a.Y, b.Y);
        var right = Math.Max(a.X + a.Width, b.X + b.Width);
        var bottom = Math.Max(a.Y + a.Height, b.Y + b.Height);
        var separator = a.Text.Length > 0 && !char.IsWhiteSpace(a.Text[^1]) && !char.IsWhiteSpace(b.Text[0]) ? " " : "";
        return new OcrLine(a.Text + separator + b.Text,
            x, y, right - x, bottom - y, Math.Max(a.Score, b.Score));
    }

    /// <summary>识别单块并带超时保护(WinRT OCR 对超限图可能挂起而不是抛异常)。</summary>
    private async Task<List<OcrLine>> RecognizeScaledWithTimeoutAsync(PixelFrame scaled, int zoom, int offX, int offY)
    {
        var task = RecognizeScaledAsync(scaled, zoom, offX, offY);
        var done = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(15)));
        if (done != task)
        {
            Diag.Dump($"windows-ocr TIMEOUT block {scaled.Width}x{scaled.Height}");
            return new List<OcrLine>();
        }
        return await task;
    }

    /// <summary>识别一张已放大的帧;bbox 还原到"原始 + 偏移"坐标系。</summary>
    private async Task<List<OcrLine>> RecognizeScaledAsync(PixelFrame scaled, int zoom, int offX, int offY)
    {
        var result = new List<OcrLine>();
        if (_engine is null) return result;

        var softwareBitmap = ToSoftwareBitmap(scaled.Pixels, scaled.Width, scaled.Height);
        var ocrResult = await _engine.RecognizeAsync(softwareBitmap);

        foreach (var line in ocrResult.Lines)
        {
            // 行 bbox = 该行所有 word bbox 的并集
            double lx = double.MaxValue, ly = double.MaxValue, rx = double.MinValue, ry = double.MinValue;
            foreach (var word in line.Words)
            {
                var r = word.BoundingRect;
                lx = Math.Min(lx, r.X);
                ly = Math.Min(ly, r.Y);
                rx = Math.Max(rx, r.X + r.Width);
                ry = Math.Max(ry, r.Y + r.Height);
            }
            if (rx <= lx || ry <= ly) continue;

            var w = (rx - lx) / zoom;
            var h = (ry - ly) / zoom;
            result.Add(new OcrLine(line.Text,
                lx / zoom + offX, ly / zoom + offY, w, h,
                HeuristicScore(line.Text, w, h)));
        }
        return result;
    }

    /// <summary>
    /// 启发式置信度(WinRT OCR 不提供置信度,用几何与字符特征近似):
    /// 起评 0.9,按"行高异常 / 宽高比异常 / 生僻符号占比"扣分,
    /// 让 OcrLineFilter 的 minScore 过滤重新生效,拦截明显的识别幻觉。
    /// </summary>
    private static double HeuristicScore(string text, double w, double h)
    {
        var score = 0.9;
        if (h < 8 || h > 120) score -= 0.4;                    // 行高异常:噪声点/整块背景
        if (w / Math.Max(1.0, h) < 0.35 && text.Length > 1) score -= 0.3; // 竖长条多字:图标/边框误识
        var weird = text.Count(c => !char.IsLetterOrDigit(c) && !char.IsWhiteSpace(c)
            && !(c >= 0x4E00 && c <= 0x9FFF) && !(c >= 0x3040 && c <= 0x30FF)
            && !(c >= 0xAC00 && c <= 0xD7AF) && ".,!?;:'\"()-–—…、。!?%&@#*+/<>=~".IndexOf(c) < 0);
        if (text.Length > 0 && weird * 1.0 / text.Length > 0.4) score -= 0.5; // 生僻符号 >40%:乱码
        return Math.Max(0.1, score);
    }

    /// <summary>把帧放大 zoom 倍(TransformedBitmap 双线性,冻结以便跨线程)。</summary>
    private static PixelFrame ScaleUp(PixelFrame frame, int zoom)
    {
        var tb = new TransformedBitmap(frame.Source, new ScaleTransform(zoom, zoom));
        tb.Freeze();
        var w = tb.PixelWidth;
        var h = tb.PixelHeight;
        var pixels = new byte[w * h * 4];
        tb.CopyPixels(pixels, w * 4, 0);
        return new PixelFrame(tb, pixels, w, h);
    }

    /// <summary>
    /// OCR 预处理(后台线程,纯像素运算):
    /// 灰度化 + 自动对比度拉伸(1%~99% 分位)+ 深色背景反色(黑底白字 → 白底黑字)。
    /// Windows OCR 对"白底黑字 + 高对比度"识别率显著更高,这一步对屏幕小字提升明显。
    /// </summary>
    private static PixelFrame Preprocess(PixelFrame frame)
    {
        var src = frame.Pixels;
        var dst = new byte[src.Length];
        var hist = new int[256];
        long sum = 0;
        var n = frame.Width * frame.Height;
        for (var i = 0; i < src.Length; i += 4)
        {
            var lum = (src[i + 2] * 299 + src[i + 1] * 587 + src[i] * 114) / 1000;
            hist[lum]++;
            sum += lum;
        }
        var avg = sum / Math.Max(1, n);

        // 自动对比度:取 1% 与 99% 亮度分位
        int lo = 0, hi = 255;
        var target = Math.Max(1, n / 100);
        var acc = 0;
        for (var k = 0; k < 256; k++) { acc += hist[k]; if (acc >= target) { lo = k; break; } }
        acc = 0;
        for (var k = 255; k >= 0; k--) { acc += hist[k]; if (acc >= target) { hi = k; break; } }
        if (hi - lo < 40) { lo = 0; hi = 255; }
        var scale = 255.0 / Math.Max(1, hi - lo);

        // 明显深色背景(黑底白字/深色 UI)→ 反色,识别率大幅提升
        var invert = avg < 100;

        for (var i = 0; i < src.Length; i += 4)
        {
            var lum = (src[i + 2] * 299 + src[i + 1] * 587 + src[i] * 114) / 1000;
            var v = (int)((lum - lo) * scale);
            v = Math.Clamp(v, 0, 255);
            if (invert) v = 255 - v;
            dst[i] = dst[i + 1] = dst[i + 2] = (byte)v;
            dst[i + 3] = 255;
        }
        return new PixelFrame(frame.Source, dst, frame.Width, frame.Height);
    }

    /// <summary>
    /// 合并块结果:bbox IoU &gt; 0.5 视为同一行(文本不必相同——切块边界常把同一行识成两半,
    /// 文本相同的旧规则会漏判,导致两个覆盖块叠在一起),保留文本更长/置信度更高的那个。
    /// </summary>
    private static void MergeLines(List<OcrLine> acc, List<OcrLine> fresh)
    {
        foreach (var l in fresh)
        {
            var dupIdx = -1;
            for (var i = 0; i < acc.Count; i++)
            {
                var e = acc[i];
                // 快速粗筛:中心距过远直接跳过(避免全量 IoU 计算)
                if (Math.Abs(e.Y - l.Y) > Math.Max(e.Height, l.Height)) continue;
                if (Iou(e, l) > 0.5) { dupIdx = i; break; }
            }
            if (dupIdx < 0)
            {
                acc.Add(l);
            }
            else
            {
                var e = acc[dupIdx];
                if (l.Text.Length > e.Text.Length || l.Score > e.Score) acc[dupIdx] = l;
            }
        }
    }

    /// <summary>两行的 bbox 交并比(0~1)。</summary>
    private static double Iou(OcrLine a, OcrLine b)
    {
        var ix = Math.Max(0, Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X));
        var iy = Math.Max(0, Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y));
        var inter = ix * iy;
        if (inter <= 0) return 0;
        var union = a.Width * a.Height + b.Width * b.Height - inter;
        return union <= 0 ? 0 : inter / union;
    }

    private static SoftwareBitmap ToSoftwareBitmap(byte[] bgra, int w, int h)
    {
        var sb = new SoftwareBitmap(BitmapPixelFormat.Bgra8, w, h, BitmapAlphaMode.Ignore);
        var writer = new DataWriter();
        writer.WriteBytes(bgra);
        sb.CopyFromBuffer(writer.DetachBuffer());
        return sb;
    }
}
