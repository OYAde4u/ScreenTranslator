namespace ScreenTranslator.Services.Ocr;

/// <summary>
/// 反幻觉过滤("哪些文本不该翻"):
/// 1. 长度过滤:trim 后 &lt;2 跳过(单个字母/数字/图标误识);
/// 2. 置信度过滤:低于阈值跳过(Paddle 引擎有真实置信度,WindowsOcr 恒为 1);
/// 3. 垃圾过滤:纯符号/纯数字/短码/URL路径等无意义文本跳过;
/// 4. 语言过滤:已是目标语言的行跳过(中文界面不翻中文);日/韩文与中文严格区分,
///    目标 ZH 时只过滤中文行,日/韩/英文行保留翻译(修复日文汉字被误判为中文的问题);
/// 5. 忽略区域:黑名单矩形内的行跳过(如自己程序的 UI)。
/// </summary>
public static class OcrLineFilter
{
    /// <summary>应用全部过滤规则。</summary>
    public static List<OcrLine> Apply(IReadOnlyList<OcrLine> lines, string targetLang,
        double minScore = 0.5, IReadOnlyList<(int X, int Y, int W, int H)>? ignoreRects = null)
    {
        var result = new List<OcrLine>(lines.Count);
        foreach (var line in lines)
        {
            var text = line.Text.Trim();
            if (text.Length < 2) continue;
            if (line.Score < minScore) continue;
            if (IsGarbage(text)) continue;            // 纯符号
            if (IsNumericOnly(text)) continue;        // 纯数字(数值/金币等)
            if (IsShortCode(text)) continue;          // 含数字的短码(A1/F5)
            if (IsPathLike(text)) continue;           // URL/文件路径
            if (IsTargetLanguage(text, targetLang)) continue;
            if (ignoreRects != null && ignoreRects.Any(r => Overlaps(line, r))) continue;
            result.Add(line with { Text = text });
        }
        return result;
    }

    // ---------- 垃圾文本 ----------

    /// <summary>乱码过滤:不含任何字母/数字/CJK/假名/韩文的行(纯标点/装饰符号)直接丢弃。</summary>
    public static bool IsGarbage(string text)
    {
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c)) return false;
            if (c >= 0x4E00 && c <= 0x9FFF) return false;
            if (c >= 0x3040 && c <= 0x30FF) return false; // 假名
            if (c >= 0xAC00 && c <= 0xD7AF) return false; // 韩文
        }
        return true;
    }

    /// <summary>纯数字行(伤害数值、金币数),至少 2 位数字。</summary>
    public static bool IsNumericOnly(string text)
    {
        var digits = 0;
        foreach (var c in text)
        {
            if (char.IsDigit(c)) { digits++; continue; }
            if (c is ' ' or ',' or '.' or ':' or '-' or '+' or '%') continue;
            return false;
        }
        return digits >= 2;
    }

    /// <summary>含数字的短码(快捷键/编号,如 "A1"、"F5"、"1A2b"),无空格且 &lt;6 字符。</summary>
    public static bool IsShortCode(string text)
    {
        if (text.Length >= 6) return false;
        if (text.Any(char.IsWhiteSpace)) return false;
        return text.Any(char.IsDigit) && text.Any(char.IsLetter);
    }

    /// <summary>URL/文件路径——翻译无意义。注意:不再按"含冒号"一刀切(会误杀 "NPC: ..." 这类对话);只过滤协议://、盘符路径与多斜杠路径。</summary>
    public static bool IsPathLike(string text)
    {
        if (text.Contains("://")) return true;                       // http:// 等协议
        if (text.Length >= 3 && char.IsLetter(text[0]) && text[1] == ':' && (text[2] is '\\' or '/')) return true; // C:\ 盘符
        var slashes = text.Count(c => c is '/' or '\\');
        if (slashes >= 2) return true;
        return false;
    }

    // ---------- 语言检测(中文/日文/韩文严格区分) ----------

    /// <summary>中文:汉字占比 &gt; 12% 且假名数量不超过汉字一半(排除日文汉字干扰)。</summary>
    public static bool IsChinese(string text)
    {
        if (text.Length == 0) return false;
        int han = 0, kana = 0;
        foreach (var c in text)
        {
            if (c >= 0x4E00 && c <= 0x9FFF) han++;
            else if (c >= 0x3040 && c <= 0x30FF) kana++;
        }
        return han * 1.0 / text.Length > 0.12 && kana * 2 < han;
    }

    /// <summary>日文:含平/片假名(0x3040~0x30FF)即视为日文(汉字+假名混合也是日文)。</summary>
    public static bool IsJapanese(string text)
    {
        foreach (var c in text)
        {
            if (c >= 0x3040 && c <= 0x30FF) return true;
        }
        return false;
    }

    /// <summary>韩文:含韩文音节/谚文字母。</summary>
    public static bool IsKorean(string text)
    {
        foreach (var c in text)
        {
            if ((c >= 0xAC00 && c <= 0xD7AF) || (c >= 0x1100 && c <= 0x11FF)) return true;
        }
        return false;
    }

    /// <summary>兼容旧接口:是否东亚(CJK)文本(源语言检测用)。</summary>
    public static bool IsCjk(string text) => IsChinese(text) || IsJapanese(text) || IsKorean(text);

    /// <summary>文本是否已是目标语言(支持 ZH/JA/EN):目标 ZH 只滤中文,日/韩/英文行保留翻译。</summary>
    public static bool IsTargetLanguage(string text, string targetLang)
    {
        return targetLang switch
        {
            "ZH" => IsChinese(text),
            "JA" => IsJapanese(text),
            "EN" => !IsChinese(text) && !IsJapanese(text) && !IsKorean(text),
            _ => false,
        };
    }

    /// <summary>按文本内容检测源语言(段落/行级):日文→JA,韩文→KO,中文→ZH,否则 EN。</summary>
    public static string DetectSourceLang(string text)
    {
        if (IsJapanese(text)) return "JA";
        if (IsKorean(text)) return "KO";
        if (IsChinese(text)) return "ZH";
        return "EN";
    }

    /// <summary>行面积的 70% 以上落在忽略矩形内才过滤(只排除"几乎完全被 app 窗口盖住"的行,不误杀重叠内容)。</summary>
    private static bool Overlaps(OcrLine line, (int X, int Y, int W, int H) rect)
    {
        var ix = Math.Max(0, Math.Min(line.X + line.Width, rect.X + rect.W) - Math.Max(line.X, rect.X));
        var iy = Math.Max(0, Math.Min(line.Y + line.Height, rect.Y + rect.H) - Math.Max(line.Y, rect.Y));
        var inter = ix * iy;
        var area = line.Width * line.Height;
        return area > 0 && inter >= area * 0.7;
    }
}
