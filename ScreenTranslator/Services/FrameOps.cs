using System.Windows;
using System.Windows.Media.Imaging;

namespace ScreenTranslator.Services;

/// <summary>PixelFrame 通用像素操作(裁剪/复制),消除 MainWindow/WindowsOcrEngine/自检中的重复实现。</summary>
public static class FrameOps
{
    /// <summary>裁剪帧:像素行拷贝 + 共享内存的 CroppedBitmap(冻结失败时忽略,源帧未冻结的调试场景)。</summary>
    public static PixelFrame Crop(PixelFrame frame, int x, int y, int w, int h)
    {
        var pixels = new byte[w * h * 4];
        for (var yy = 0; yy < h; yy++)
        {
            Buffer.BlockCopy(frame.Pixels, ((y + yy) * frame.Width + x) * 4, pixels, yy * w * 4, w * 4);
        }
        var source = new CroppedBitmap(frame.Source, new Int32Rect(x, y, w, h));
        try { source.Freeze(); } catch { /* 源未冻结时跳过(生产帧均已冻结) */ }
        return new PixelFrame(source, pixels, w, h);
    }
}
