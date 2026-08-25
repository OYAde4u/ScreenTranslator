namespace ScreenTranslator.Services.Ocr;

/// <summary>
/// OCR 引擎抽象。实现可选:
/// - WindowsOcrEngine(Windows.Media.Ocr,离线零依赖,质量一般)
/// - PaddleOcrEngine(联网后经 NuGet 引入,幻觉更少,结果自带四角点坐标)
/// </summary>
public interface IOcrEngine
{
    /// <summary>引擎是否可用(初始化失败时为 false)。</summary>
    bool IsAvailable { get; }

    /// <summary>识别一帧截图,返回每行文本 + bbox(物理像素)。</summary>
    Task<List<OcrLine>> RecognizeAsync(PixelFrame frame, CancellationToken ct = default);
}
