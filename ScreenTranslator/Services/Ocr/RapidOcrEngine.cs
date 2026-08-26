using System.IO;
using System.Runtime.InteropServices;
using RapidOcrNet;
using SkiaSharp;

namespace ScreenTranslator.Services.Ocr;

/// <summary>
/// RapidOcrNet(PP-OCRv6 small 多语,ONNX)引擎:无 PaddleOCRSharp 社区版 "box sizes <100" 限制,
/// 整屏可用;多语识别(Latin + CJK),自带真实置信度(逐字符分数)与四角点坐标。
/// 模型:输出目录 models\v6\(PP-OCRv6_det_small / PP-OCRv6_rec_small / ppocrv6_dict,SHA256 已校验),
/// 分类器复用 RapidOcrNet 包自带 models\v5 cls。
/// 初始化(加载 ONNX,数百 ms)在首次识别的后台线程执行,不卡 UI 启动。
/// </summary>
public sealed class RapidOcrEngine : IOcrEngine, IDisposable
{
    private readonly object _gate = new();
    private RapidOcr? _ocr;
    private bool _initFailed;

    public bool IsAvailable => !_initFailed;

    /// <summary>上一轮识别是否因异常失败(供上层决定是否回退其他引擎;区别于"确实没识别到文字")。</summary>
    public bool LastCallFailed { get; private set; }

    public Task<List<OcrLine>> RecognizeAsync(PixelFrame frame, CancellationToken ct = default)
    {
        // 推理是 CPU 密集活,整体移出调用线程(UI 线程)
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            LastCallFailed = false;
            var engine = GetEngine();
            if (engine is null)
            {
                LastCallFailed = true;   // 初始化失败(缺模型/native):让上层回退
                return new List<OcrLine>();
            }

            var result = new List<OcrLine>();
            var handle = GCHandle.Alloc(frame.Pixels, GCHandleType.Pinned);
            SKBitmap? bmp = null;
            try
            {
                // 直接引用 BGRA 像素(零拷贝);Opaque 忽略 alpha 通道
                var info = new SKImageInfo(frame.Width, frame.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
                bmp = new SKBitmap();
                bmp.InstallPixels(info, handle.AddrOfPinnedObject(), info.RowBytes);

                // PPOCRv6 预设:short-side 自适应 736、无白边,与 v6 det 的导出预处理一致(README 明确要求);
                // DoAngle=false:屏幕文字永远是水平方向,关掉逐行角度分类器(每行一次额外推理),
                // 实测整屏 155 行省 ~700ms(~20%),精度零损失
                var ocr = engine.Detect(bmp, RapidOcrOptions.PPOCRv6 with { DoAngle = false }, ct);

                foreach (var block in ocr.TextBlocks)
                {
                    var text = block.Text?.Trim();
                    if (string.IsNullOrEmpty(text)) continue;

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

                    var score = block.CharScores is { Length: > 0 } cs ? cs.Average() : block.BoxScore;
                    result.Add(new OcrLine(text, minX, minY, maxX - minX, maxY - minY, score));
                }
                Diag.Dump($"rapid-ocr: {frame.Width}x{frame.Height} -> {result.Count} lines, det={ocr.DbNetTime:F0}ms total={ocr.DetectTime:F0}ms");
            }
            catch (OperationCanceledException) { /* 取消:正常 */ }
            catch (Exception ex)
            {
                // 识别失败返回空;上层可回退 Windows OCR。异常落盘便于诊断。
                LastCallFailed = true;
                Diag.Dump("RAPID EXC: " + ex);
            }
            finally
            {
                bmp?.Reset();      // 解除对固定像素的引用,再释放 GCHandle
                bmp?.Dispose();
                handle.Free();
            }
            return result;
        }, ct);
    }

    /// <summary>懒加载引擎(首次调用所在的后台线程执行 InitModels)。</summary>
    private RapidOcr? GetEngine()
    {
        lock (_gate)
        {
            if (_initFailed) return null;
            if (_ocr != null) return _ocr;
            try
            {
                // 预设里的相对路径(models\v6\...)是相对进程 cwd 解析的;自检/快捷方式启动时 cwd 不一定是程序目录,
                // 统一转成 AppContext.BaseDirectory 绝对路径
                var baseDir = AppContext.BaseDirectory;
                var models = RapidOcrModelSet.PPOCRv6Small with
                {
                    DetModelPath = Path.Combine(baseDir, "models", "v6", "PP-OCRv6_det_small.onnx"),
                    ClsModelPath = Path.Combine(baseDir, "models", "v5", "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx"),
                    RecModelPath = Path.Combine(baseDir, "models", "v6", "PP-OCRv6_rec_small.onnx"),
                    KeysPath = Path.Combine(baseDir, "models", "v6", "ppocrv6_dict.txt"),
                };
                var ocr = new RapidOcr();
                // 8 线程:实测 det 1192ms→1010ms(整屏),rec 吞吐同步提升;默认线程数偏保守
                ocr.InitModels(models, 8);
                _ocr = ocr;
                Diag.Dump("rapid-ocr: models loaded (PP-OCRv6 small)");
            }
            catch (Exception ex)
            {
                Diag.Dump("RAPID INIT EXC: " + ex);
                _initFailed = true;
            }
            return _ocr;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _ocr?.Dispose();
            _ocr = null;
        }
    }
}
