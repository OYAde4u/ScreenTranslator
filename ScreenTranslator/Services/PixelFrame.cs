using System.Windows.Media.Imaging;

namespace ScreenTranslator.Services;

/// <summary>
/// 一帧屏幕截图的完整数据:BitmapSource(显示用)+ BGRA32 像素数组(背景采样/OCR 输入用)。
/// 坐标约定:全部为物理像素,原点 = 虚拟屏幕左上角。
/// </summary>
public sealed record PixelFrame(BitmapSource Source, byte[] Pixels, int Width, int Height);
