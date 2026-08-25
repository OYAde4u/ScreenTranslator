namespace ScreenTranslator.Services.Ocr;

/// <summary>OCR 识别出的一行文本及其精确位置(物理像素坐标,原点 = 虚拟屏幕左上角)。</summary>
public sealed record OcrLine(string Text, double X, double Y, double Width, double Height, double Score);
