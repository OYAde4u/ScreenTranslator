using ScreenTranslator.Services.Ocr;

namespace ScreenTranslator.Services.Translate;

/// <summary>
/// OCR 行 → 段落聚合:OCR 按视觉行切分,一句话常被拆成 2~4 行;
/// 按几何关系(垂直间距、水平重叠)把碎片合并为段落,段落内文本用换行连接、
/// 整段一次翻译,保证上下文连贯(对应架构文档 §2.5"批量请求")。
/// </summary>
public static class LineGrouping
{
    /// <summary>段落:一组几何相邻的 OCR 行。</summary>
    public sealed record Paragraph(List<OcrLine> Lines)
    {
        /// <summary>段落文本:行间以换行连接(一次翻译请求的输入)。</summary>
        public string Text => string.Join("\n", Lines.Select(l => l.Text));
    }

    /// <summary>同段判定阈值:垂直间距 &lt; max(行高×1.5, 24px) 且水平重叠 &gt; 30%。</summary>
    private const double GapFactor = 1.5;
    private const double MinGap = 24;
    private const double OverlapRatio = 0.3;

    /// <summary>行数上限:超过的段落按行数切成多段,避免单次请求过大。</summary>
    public const int MaxLinesPerParagraph = 8;

    /// <summary>把 OCR 行聚合成段落(按 y 排序,自上而下扫描)。</summary>
    public static List<Paragraph> Group(IReadOnlyList<OcrLine> lines)
    {
        var sorted = lines.OrderBy(l => l.Y).ThenBy(l => l.X).ToList();
        var paragraphs = new List<Paragraph>();
        var current = new List<OcrLine>();
        OcrLine? prev = null;

        foreach (var line in sorted)
        {
            if (prev != null && !SameParagraph(prev, line))
            {
                AddChunk(paragraphs, current);
                current = new List<OcrLine>();
            }
            current.Add(line);
            prev = line;
        }
        AddChunk(paragraphs, current);
        return paragraphs;
    }

    /// <summary>按行数上限切段(超长段落拆成多段,段内仍保持换行连接)。</summary>
    private static void AddChunk(List<Paragraph> paragraphs, List<OcrLine> chunk)
    {
        for (var i = 0; i < chunk.Count; i += MaxLinesPerParagraph)
        {
            paragraphs.Add(new Paragraph(chunk.Skip(i).Take(MaxLinesPerParagraph).ToList()));
        }
    }

    private static bool SameParagraph(OcrLine a, OcrLine b)
    {
        var gap = b.Y - (a.Y + a.Height);
        if (gap > Math.Max(Math.Max(a.Height, b.Height) * GapFactor, MinGap)) return false;

        // 水平重叠:两行 x 区间交集 / 较短行宽
        var ix = Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X);
        if (ix <= 0) return false;
        var shorter = Math.Min(a.Width, b.Width);
        return shorter > 0 && ix / shorter > OverlapRatio;
    }
}
