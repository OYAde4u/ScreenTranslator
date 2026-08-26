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

        // 背景采样:取 bbox 外围一圈(文本附近的背景),不混入字形像素,颜色更贴近真实背景、更柔和
        var bg = SampleSurrounding(frame, line.X, line.Y, line.Width, line.Height, ring: 8);
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

    /// <summary>
    /// 生成段落级覆盖图元(字幕风格):一个深色整块覆盖段落全部行的联合区域(含行间隙),
    /// 译文按行 \n 连接后在块内整体换行,左对齐。
    /// 为什么按段落而不是逐行:游戏对话框/小说段落行距密,逐行块各自膨胀会互相叠压(字体叠加),
    /// 且行间残句(如句尾被 OCR 拆出的"に。")会露出;整块覆盖从结构上消除这两个问题。
    /// </summary>
    public static OverlayItem BuildParagraph(IReadOnlyList<(OcrLine Line, string Text)> lines,
        double padding = 4.0)
    {
        var x0 = lines.Min(l => l.Line.X) - padding;
        var y0 = lines.Min(l => l.Line.Y) - padding;
        var x1 = lines.Max(l => l.Line.X + l.Line.Width) + padding;
        var y1 = lines.Max(l => l.Line.Y + l.Line.Height) + padding;
        var text = string.Join("\n", lines.Select(l => l.Text));
        return new OverlayItem(x0, y0, x1 - x0, y1 - y0,
            Color.FromArgb(235, 14, 14, 16), text, Colors.White, Centered: false);
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
