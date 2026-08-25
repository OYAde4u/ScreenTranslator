using System.Buffers;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ScreenTranslator.Services;

/// <summary>
/// 全屏截图服务:纯 P/Invoke(GDI BitBlt + DIB 读像素),零第三方依赖。
/// 逐显示器捕获:每个显示器用 CreateDC(设备名) 建独立 DC —— 副屏/多 GPU(笔记本独显+核显)
/// 场景下,从主屏 DC 直接 BitBlt 会得到黑区,逐屏捕获可完整覆盖虚拟屏幕。
/// 坐标:物理像素,原点 = 虚拟屏幕左上角(与 DisplayLayout/OCR bbox 一致)。
/// </summary>
public static class ScreenCaptureService
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateDC(string? lpszDriver, string? lpszDevice, string? lpszOutput, IntPtr lpInitData);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest, int w, int h,
        IntPtr hdcSrc, int xSrc, int ySrc, int rop);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hbmp, uint start, uint lines,
        byte[]? bits, ref BitmapInfoHeader bmi, uint usage);

    private const int SrcCopy = 0x00CC0020;
    private const uint DibRgbColors = 0;

    private const int SmXvirtualscreen = 76;
    private const int SmYvirtualscreen = 77;
    private const int SmCxvirtualscreen = 78;
    private const int SmCyvirtualscreen = 79;

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint biSize;
        public int biWidth;
        public int biHeight; // 负数 = 自顶向下行序
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    /// <summary>一次完整捕获:逐显示器截图拼接为虚拟屏幕 BGRA32 帧(物理像素)。</summary>
    public static ScreenSnapshot CaptureSnapshot()
    {
        var left = GetSystemMetrics(SmXvirtualscreen);
        var top = GetSystemMetrics(SmYvirtualscreen);
        var width = GetSystemMetrics(SmCxvirtualscreen);
        var height = GetSystemMetrics(SmCyvirtualscreen);

        // 池化缓冲:避免每帧 2~4 份全屏数组分配(GC 停顿是卡顿来源之一)
        var pixels = ArrayPool<byte>.Shared.Rent(width * height * 4);
        Array.Clear(pixels, 0, width * height * 4);

        var monitors = DisplayLayout.Monitors;
        var anyOk = false;
        foreach (var m in monitors)
        {
            var dstX = m.Left - left;
            var dstY = m.Top - top;
            anyOk |= CaptureMonitorInto(m, dstX, dstY, width, pixels);
        }

        // 全部失败(极端环境)→ 回退旧路径:从主屏 DC 一次性 BitBlt 全虚拟屏
        if (!anyOk)
        {
            CaptureVirtualFallback(left, top, width, height, pixels);
        }

        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        source.Freeze(); // 冻结:后台线程创建,UI/OCR 线程安全使用
        return new ScreenSnapshot(new PixelFrame(source, pixels, width, height), monitors);
    }

    /// <summary>兼容旧接口:返回整帧(自检/调试用)。</summary>
    public static PixelFrame CaptureFrame() => CaptureSnapshot().Frame;

    /// <summary>捕获完成后归还池化缓冲(帧的 Source 已持有拷贝,归还后仍可安全显示/编码)。</summary>
    public static void ReleaseFrame(PixelFrame frame)
    {
        if (frame.Pixels.Length > 0)
        {
            try { ArrayPool<byte>.Shared.Return(frame.Pixels); } catch { /* 忽略:非池化缓冲 */ }
        }
    }

    /// <summary>捕获单个显示器区域并写入虚拟帧对应位置(逐行拷贝)。</summary>
    private static bool CaptureMonitorInto(ScreenMonitor m, int dstX, int dstY, int frameWidth, byte[] framePixels)
    {
        IntPtr dc = IntPtr.Zero, memDc = IntPtr.Zero, hBitmap = IntPtr.Zero;
        try
        {
            // 用显示器设备名建 DC:跨适配器也能截到(副屏不再黑屏)
            dc = CreateDC(m.DeviceName, m.DeviceName, null, IntPtr.Zero);
            if (dc == IntPtr.Zero)
            {
                dc = CreateDC("DISPLAY", m.DeviceName, null, IntPtr.Zero);
            }
            if (dc == IntPtr.Zero) return false;

            memDc = CreateCompatibleDC(dc);
            hBitmap = CreateCompatibleBitmap(dc, m.Width, m.Height);
            if (hBitmap == IntPtr.Zero) return false;

            var old = SelectObject(memDc, hBitmap);
            try
            {
                if (!BitBlt(memDc, 0, 0, m.Width, m.Height, dc, 0, 0, SrcCopy)) return false;
            }
            finally
            {
                SelectObject(memDc, old);
            }

            var bmi = new BitmapInfoHeader
            {
                biSize = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                biWidth = m.Width,
                biHeight = -m.Height, // 自顶向下
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0,
            };
            var monitorPixels = ArrayPool<byte>.Shared.Rent(m.Width * m.Height * 4);
            try
            {
                var got = GetDIBits(memDc, hBitmap, 0, (uint)m.Height, monitorPixels, ref bmi, DibRgbColors);
                if (got == 0) return false;

                // 逐行拷入虚拟帧对应位置
                for (var y = 0; y < m.Height; y++)
                {
                    var srcRow = y * m.Width * 4;
                    var dstRow = (dstY + y) * frameWidth * 4 + dstX * 4;
                    Buffer.BlockCopy(monitorPixels, srcRow, framePixels, dstRow, m.Width * 4);
                }
                return true;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(monitorPixels);
            }
        }
        catch
        {
            return false;
        }
        finally
        {
            if (hBitmap != IntPtr.Zero) DeleteObject(hBitmap);
            if (memDc != IntPtr.Zero) DeleteDC(memDc);
            if (dc != IntPtr.Zero) DeleteDC(dc);
        }
    }

    /// <summary>回退路径:从主屏 DC 一次性 BitBlt 整个虚拟屏(单显示器/老环境)。</summary>
    private static void CaptureVirtualFallback(int left, int top, int width, int height, byte[] pixels)
    {
        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memDc = CreateCompatibleDC(screenDc);
        IntPtr hBitmap = CreateCompatibleBitmap(screenDc, width, height);
        try
        {
            var old = SelectObject(memDc, hBitmap);
            try
            {
                BitBlt(memDc, 0, 0, width, height, screenDc, left, top, SrcCopy);
            }
            finally
            {
                SelectObject(memDc, old);
            }

            var bmi = new BitmapInfoHeader
            {
                biSize = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                biWidth = width,
                biHeight = -height,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0,
            };
            GetDIBits(memDc, hBitmap, 0, (uint)height, pixels, ref bmi, DibRgbColors);
        }
        finally
        {
            if (hBitmap != IntPtr.Zero) DeleteObject(hBitmap);
            if (memDc != IntPtr.Zero) DeleteDC(memDc);
            if (screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>把 BitmapSource 存为 PNG(调试用;应在后台线程调用)。</summary>
    public static void SavePng(BitmapSource src, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(src));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }
}

/// <summary>一次截图快照:虚拟屏幕帧 + 捕获时的显示器布局。</summary>
public sealed record ScreenSnapshot(PixelFrame Frame, IReadOnlyList<ScreenMonitor> Monitors);
