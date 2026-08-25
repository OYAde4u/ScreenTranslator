using System.IO;
using System.Windows.Media.Imaging;
using PaddleOCRSharp;

namespace ScreenTranslator.Services.Ocr;

/// <summary>
/// PaddleOCRSharp(PP-OCRv5)引擎:幻觉比系统 OCR 少,自带四角点坐标与真实置信度。
/// 模型随 NuGet 包拷贝到输出目录 inference\ 下(PP-OCRv5_mobile_{det,rec,cls}_infer)。
/// 初始化失败(缺 native/模型)时 IsAvailable=false,由上层回退 WindowsOcr。
/// </summary>
public sealed class PaddleOcrEngine : IOcrEngine, IDisposable
{
    private readonly PaddleOCREngine? _engine;

    public PaddleOcrEngine()
    {
        try
        {
            var modelDir = Path.Combine(AppContext.BaseDirectory, "inference");
            var config = new OCRModelConfig
            {
                det_infer = Path.Combine(modelDir, "PP-OCRv5_mobile_det_infer"),
                rec_infer = Path.Combine(modelDir, "PP-OCRv5_mobile_rec_infer"),
                cls_infer = Path.Combine(modelDir, "PP-OCRv5_mobile_cls_infer"),
            };
            var parameter = new OCRParameter
            {
                det = true,
                rec = true,
                cls = true,
                enable_mkldnn = true,
                cpu_math_library_num_threads = 4,
            };
            _engine = new PaddleOCREngine(config, parameter);
        }
        catch
        {
            _engine = null;
        }
    }

    public bool IsAvailable => _engine != null;

    public Task<List<OcrLine>> RecognizeAsync(PixelFrame frame, CancellationToken ct = default)
    {
        if (_engine is null) return Task.FromResult(new List<OcrLine>());

        // 同步重活(PNG 编码 + 推理)移出调用线程:原实现在 UI 线程上阻塞数百 ms,是"捕获后卡顿"主因
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var result = new List<OcrLine>();
            try
            {
                var png = EncodePng(frame);
                ct.ThrowIfCancellationRequested();
                var ocr = _engine.DetectText(png);

                foreach (var block in ocr.TextBlocks)
                {
                    var text = block.Text?.Trim();
                    if (string.IsNullOrEmpty(text)) continue;

                    // 四角点 → 外接矩形
                    double minX = double.MaxValue, minY = double.MaxValue;
                    double maxX = double.MinValue, maxY = double.MinValue;
                    foreach (var p in block.BoxPoints)
                    {
                        minX = Math.Min(minX, p.X);
                        minY = Math.Min(minY, p.Y);
                        maxX = Math.Max(maxX, p.X);
                        maxY = Math.Max(maxY, p.Y);
                    }
                    if (maxX <= minX || maxY <= minY) continue;

                    result.Add(new OcrLine(text, minX, minY, maxX - minX, maxY - minY, block.Score));
                }
            }
            catch (Exception ex)
            {
                // 识别失败返回空;上层有过滤/降级,不中断流程。异常落盘便于诊断。
                Diag.Dump("PADDLE EXC: " + ex);
            }
            return result;
        }, ct);
    }

    public void Dispose() => _engine?.Dispose();

    /// <summary>从 BGRA 像素直接编码 PNG(不依赖 BitmapSource:后台线程可用,且避免跨线程访问未冻结对象)。</summary>
    private static byte[] EncodePng(PixelFrame frame)
    {
        var bmp = BitmapSource.Create(frame.Width, frame.Height, 96, 96,
            System.Windows.Media.PixelFormats.Bgra32, null, frame.Pixels, frame.Width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }
}
