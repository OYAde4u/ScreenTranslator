using System.Windows.Media;
using ScreenTranslator.Services.Ocr;

namespace ScreenTranslator.Services;

/// <summary>
/// 把 OCR 结果转成覆盖层图元:译文块精确压在原文 bbox 上(悬浮窗式覆盖,类似手机实时翻译)。
/// 两种渲染风格:
/// - Subtitle(推荐):近不透明深色底块 + 白字,任何背景(图片/视频/复杂 UI)都清晰可读,类似小米实时翻译的字幕块;
/// - Background:采样原文背景主色填充 + 对比色文字,纯色背景时最自然(像原文被原位替换)。
/// </summary>
public static class OcrOverlayRenderer
{
    public enum RenderStyle
    {
        /// <summary>深色字幕底块 + 白字,居中(小米实时翻译风格)。</summary>
        Subtitle,

        /// <summary>采样背景色填充 + 黑/白对比字(原位替换风格)。</summary>
        Background,
    }

    public sealed record OverlayItem(double X, double Y, double W, double H, Color Bg, string Text, Color Fg, bool Centered);

    /// <summary>生成单行覆盖图元(显示 displayText,如译文;物理像素坐标)。padding 用于盖掉文字抗锯齿边缘。</summary>
    public static OverlayItem BuildOne(PixelFrame frame, OcrLine line, string displayText,
        RenderStyle style = RenderStyle.Subtitle, double padding = 2.0)
    {
        if (style == RenderStyle.Subtitle)
        {
            // 近不透明深色底块:盖住原文,白字清晰(复杂背景也适用)
            return new OverlayItem(
                line.X - padding - 1, line.Y - padding - 1,
                line.Width + (padding + 1) * 2, line.Height + (padding + 1) * 2,
                Color.FromArgb(235, 14, 14, 16), displayText, Colors.White, Centered: true);
        }

        var bg = SampleAverage(frame, line.X, line.Y, line.Width, line.Height);
        var fg = ContrastColor(bg);
        return new OverlayItem(
            line.X - padding, line.Y - padding,
            line.Width + padding * 2, line.Height + padding * 2,
            bg, displayText, fg, Centered: false);
    }

    /// <summary>生成覆盖图元列表(原文模式,物理像素坐标)。</summary>
    public static List<OverlayItem> Build(PixelFrame frame, IReadOnlyList<OcrLine> lines,
        RenderStyle style = RenderStyle.Subtitle, double padding = 2.0)
    {
        var items = new List<OverlayItem>(lines.Count);
        foreach (var line in lines)
        {
            items.Add(BuildOne(frame, line, line.Text, style, padding));
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

    /// <summary>按背景亮度选择对比色(白底黑字,黑底白字)。</summary>
    public static Color ContrastColor(Color bg)
    {
        var lum = (0.299 * bg.R + 0.587 * bg.G + 0.114 * bg.B) / 255.0;
        return lum > 0.55 ? Colors.Black : Colors.White;
    }
}
