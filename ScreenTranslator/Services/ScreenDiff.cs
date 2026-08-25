namespace ScreenTranslator.Services;

/// <summary>
/// 屏幕区域变化检测:分块指纹(FNV-1a,降采样抗噪)对比前后两帧,输出脏矩形。
/// 用途:M4 起"只重翻变化区域";也用于防止无变化时重复 OCR。
/// </summary>
public static class ScreenDiff
{
    public const int BlockSize = 64;
    private const int SampleStep = 8;
    private const long FnvOffset = 1469598103934665603;
    private const long FnvPrime = 1099511628211;

    public sealed record BlockFingerprint(int X, int Y, long Hash);

    /// <summary>计算一帧的分块指纹。</summary>
    public static List<BlockFingerprint> Fingerprint(PixelFrame frame)
    {
        var list = new List<BlockFingerprint>((frame.Width / BlockSize + 1) * (frame.Height / BlockSize + 1));
        for (var by = 0; by < frame.Height; by += BlockSize)
        {
            for (var bx = 0; bx < frame.Width; bx += BlockSize)
            {
                list.Add(new BlockFingerprint(bx, by, HashBlock(frame, bx, by)));
            }
        }
        return list;
    }

    /// <summary>对比两帧指纹,返回脏矩形列表(每行合并相邻变化块)。</summary>
    public static List<(int X, int Y, int W, int H)> DirtyRects(
        IReadOnlyDictionary<(int X, int Y), long> prev, IReadOnlyList<BlockFingerprint> cur)
    {
        var dirty = new List<(int X, int Y)>();
        foreach (var b in cur)
        {
            if (!prev.TryGetValue((b.X, b.Y), out var old) || old != b.Hash)
                dirty.Add((b.X, b.Y));
        }
        if (dirty.Count == 0) return new List<(int, int, int, int)>();

        // 按行合并相邻块
        var rects = new List<(int X, int Y, int W, int H)>();
        foreach (var row in dirty.GroupBy(d => d.Y).OrderBy(g => g.Key))
        {
            var xs = row.Select(d => d.X).Distinct().OrderBy(x => x).ToList();
            var start = xs[0];
            var prevX = xs[0];
            for (var i = 1; i <= xs.Count; i++)
            {
                var x = i < xs.Count ? xs[i] : prevX + BlockSize * 2;
                if (x > prevX + BlockSize)
                {
                    rects.Add((start, row.Key, prevX + BlockSize - start, BlockSize));
                    if (i < xs.Count) start = x;
                }
                prevX = x;
            }
        }

        // 垂直合并:同 (X, W) 且 Y 相接的矩形并成一个
        var merged = new List<(int X, int Y, int W, int H)>();
        foreach (var group in rects.GroupBy(r => (r.X, r.W)))
        {
            var sorted = group.OrderBy(r => r.Y).ToList();
            var (cx, cy, cw, ch) = sorted[0];
            for (var i = 1; i < sorted.Count; i++)
            {
                var r = sorted[i];
                if (r.Y <= cy + ch)
                {
                    ch = Math.Max(ch, r.Y + r.H - cy);
                }
                else
                {
                    merged.Add((cx, cy, cw, ch));
                    (cx, cy, cw, ch) = r;
                }
            }
            merged.Add((cx, cy, cw, ch));
        }
        return merged;
    }

    private static long HashBlock(PixelFrame f, int bx, int by)
    {
        long h = FnvOffset;
        var x1 = Math.Min(f.Width, bx + BlockSize);
        var y1 = Math.Min(f.Height, by + BlockSize);
        for (var y = by; y < y1; y += SampleStep)
        {
            var row = y * f.Width * 4;
            for (var x = bx; x < x1; x += SampleStep)
            {
                var i = row + x * 4;
                // 只取高 4 位,抗轻微渲染抖动
                h ^= f.Pixels[i] & 0xF0; h *= FnvPrime;
                h ^= f.Pixels[i + 1] & 0xF0; h *= FnvPrime;
                h ^= f.Pixels[i + 2] & 0xF0; h *= FnvPrime;
            }
        }
        return h;
    }
}
