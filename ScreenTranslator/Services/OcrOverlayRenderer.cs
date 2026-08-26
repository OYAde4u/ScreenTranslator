using System.Windows.Media;
using ScreenTranslator.Services.Ocr;

namespace ScreenTranslator.Services;

/// <summary>
/// 把 OCR 结果转成覆盖层图元:译文块精确压在原文 bbox 上(悬浮窗式覆盖,类似手机实时翻译)。
/// 渲染方式(1.3.0 起唯一保留):背景采样——取文本外围一圈像素平均色填充 + 对比色文字,
/// 纯色/半透明对话框背景上最自然,像原文被原位替换。
/// </summary>
public static class OcrOverlayRenderer
{
    public sealed record OverlayItem(double X, double Y, double W, double H, Color Bg, string Text, Color Fg, bool Centered);

    /// <summary>生成单行覆盖图元(显示 displayText,如译文;物理像素坐标)。padding 用于盖掉文字抗锯齿边缘。</summary>
    public static OverlayItem BuildOne(PixelFrame frame, OcrLine line, string displayText, double padding = 2.0)
    {
        // 背景采样:取 bbox 外围一圈(文本附近的背景),不混入字形像素,颜色更贴近真实背景、更柔和
        var bg = SampleSurrounding(frame, line.X, line.Y, line.Width, line.Height, ring: 8);
        var fg = ContrastColor(bg);
        return new OverlayItem(
            line.X - padding, line.Y - padding,
            line.Width + padding * 2, line.Height + padding * 2,
            bg, displayText, fg, Centered: false);
    }

    /// <summary>生成覆盖图元列表(原文模式,物理像素坐标)。</summary>
    public static List<OverlayItem> Build(PixelFrame frame, IReadOnlyList<OcrLine> lines, double padding = 2.0)
    {
        var items = new List<OverlayItem>(lines.Count);
        foreach (var line in lines)
        {
            items.Add(BuildOne(frame, line, line.Text, padding));
        }
        return items;
    }

    /// <summary>区域平均色(降采样步长 4,忽略 alpha)。</summary>
    private static Color SampleAverage(PixelFrame f, double x, double y, double w, double h)
    {
        int x0 = Math.Max(0, (int)x), y0 = Math.Max(0, (int)y);
        int x1 = Math.Min(f.Width, (int)Math.Ceiling(x + w));
        int y1 = Math.Min(f.Height, (int)Math.Ceiling(y + h));
        if (x1 <= x0 || y1 <= y0) return Colors.White;

        long r = 0, g = 0, b = 0;
        int n = 0;
        for (int yy = y0; yy < y1; yy += 4)
        {
            var row = yy * f.Width * 4;
            for (int xx = x0; xx < x1; xx += 4)
            {
                var i = row + xx * 4;
                b += f.Pixels[i];
                g += f.Pixels[i + 1];
                r += f.Pixels[i + 2];
                n++;
            }
        }
        if (n == 0) return Colors.White;
        return Color.FromArgb(255, (byte)(r / n), (byte)(g / n), (byte)(b / n));
    }

    /// <summary>
    /// 采样 bbox 外围一圈环形区域(ring 像素宽)的平均色:取"文本附近的背景",
    /// 不含 bbox 内部的字形像素(白底黑字取内部平均会偏灰)。环形落在屏幕外时回退内部平均。
    /// </summary>
    private static Color SampleSurrounding(PixelFrame f, double x, double y, double w, double h, int ring)
    {
        long r = 0, g = 0, b = 0;
        var n = 0;

        void Accumulate(int x0, int y0, int x1, int y1)
        {
            x0 = Math.Max(0, x0); y0 = Math.Max(0, y0);
            x1 = Math.Min(f.Width, x1); y1 = Math.Min(f.Height, y1);
            for (var yy = y0; yy < y1; yy += 2)
            {
                var row = yy * f.Width * 4;
                for (var xx = x0; xx < x1; xx += 2)
                {
                    var i = row + xx * 4;
                    b += f.Pixels[i];
                    g += f.Pixels[i + 1];
                    r += f.Pixels[i + 2];
                    n++;
                }
            }
        }

        var ix0 = (int)x; var iy0 = (int)y;
        var ix1 = (int)Math.Ceiling(x + w); var iy1 = (int)Math.Ceiling(y + h);
        Accumulate(ix0 - ring, iy0 - ring, ix1 + ring, iy0);       // 上
        Accumulate(ix0 - ring, iy1, ix1 + ring, iy1 + ring);       // 下
        Accumulate(ix0 - ring, iy0, ix0, iy1);                     // 左
        Accumulate(ix1, iy0, ix1 + ring, iy1);                     // 右

        if (n == 0) return SampleAverage(f, x, y, w, h);
        return Color.FromArgb(255, (byte)(r / n), (byte)(g / n), (byte)(b / n));
    }

    /// <summary>按背景亮度选择对比色(白底黑字,黑底白字)。</summary>
    public static Color ContrastColor(Color bg)
    {
        var lum = (0.299 * bg.R + 0.587 * bg.G + 0.114 * bg.B) / 255.0;
        return lum > 0.55 ? Colors.Black : Colors.White;
    }
}
