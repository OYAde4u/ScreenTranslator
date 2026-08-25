namespace ScreenTranslator.Services.Ocr;

/// <summary>
/// 混合 OCR 引擎(高质量优先):默认全部区域走 RapidOCR(PP-OCRv6 small 多语,ONNX),
/// 模型缺失/初始化失败/识别异常时回退 Windows.Media.Ocr(系统自带,整屏稳定兜底)。
///
/// 依据(本机实测):
/// - RapidOCR 合成图中英全对(置信度 0.987~1.000);真实整屏 2560x1600 识别 116 行、中文无错字,无框大小限制;
/// - Windows OCR 整屏稳定但小字/中文错字多(如 "HeIlo"、"RO c ksta r"、中文逐字插空格);
/// - PaddleOCRSharp 6.2.0 社区版整屏触发 "box sizes <100" 返回 0 行,不再作为主路径(类保留作诊断自检)。
/// </summary>
public sealed class HybridOcrEngine : IOcrEngine, IDisposable
{
    private readonly WindowsOcrEngine _windows = new();
    private readonly RapidOcrEngine _rapid = new();

    public bool IsAvailable => _rapid.IsAvailable || _windows.IsAvailable;

    public async Task<List<OcrLine>> RecognizeAsync(PixelFrame frame, CancellationToken ct = default)
    {
        if (_rapid.IsAvailable)
        {
            var lines = await _rapid.RecognizeAsync(frame, ct);
            if (!_rapid.LastCallFailed)
            {
                Diag.Dump($"hybrid-ocr: rapid {frame.Width}x{frame.Height} -> {lines.Count} lines");
                return lines;
            }
            // 模型缺失/识别异常:回退 Windows 兜底,不丢内容
            Diag.Dump($"hybrid-ocr: rapid failed on {frame.Width}x{frame.Height}, fallback windows");
        }

        var fallback = await _windows.RecognizeAsync(frame, ct);
        Diag.Dump($"hybrid-ocr: windows {frame.Width}x{frame.Height} -> {fallback.Count} lines");
        return fallback;
    }

    public void Dispose() => _rapid.Dispose();
}
