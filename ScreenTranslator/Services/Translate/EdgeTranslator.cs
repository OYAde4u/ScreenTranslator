using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScreenTranslator.Services.Translate;

/// <summary>
/// Edge/微软翻译免费接口 v2(免鉴权):2026-07 上游移除了 token 端点(edge.microsoft.com/translate/auth,404),
/// 后继端点为 edge.microsoft.com/translate/translatetext(参考 read-frog 的迁移):
/// - POST body 是纯 JSON 字符串数组(不再是 [{Text}] 形状);
/// - 必须带浏览器 User-Agent,否则 400 "Client Browser Version not supported";
/// - 服务端会对文本跑 HTML 标签对齐器:裸 "&lt;" 会被粘成伪标签,入参先 HTML 转义、出参解码一次;
/// - 天然支持批量(一次请求多条文本),与段落聚合翻译配合最好;实测译文保留 \n 换行;
/// - 批量失败整批返回 null(由管道行级降级到 MyMemory)。
/// </summary>
public sealed class EdgeTranslator : ITranslator
{
    private const string TranslateUrl =
        "https://edge.microsoft.com/translate/translatetext?isEnterpriseClient=false";

    // 无浏览器 UA 时端点返回 400 "Client Browser Version not supported"
    private const string BrowserUa =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36 Edg/120.0.0.0";

    private readonly HttpClient _http;
    private long _downUntilTicks;

    public string Name => "Edge";

    /// <summary>最近一次失败原因(供 UI 提示)。</summary>
    public string? LastError { get; private set; }

    public EdgeTranslator()
    {
        // 国内直连,不走系统代理(代理反而可能把请求挂起)
        var handler = new HttpClientHandler { UseProxy = false };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(6) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(BrowserUa);
    }

    public async Task<IReadOnlyList<string?>> TranslateAsync(IReadOnlyList<string> texts, string from, string to,
        CancellationToken ct = default)
    {
        var results = new string?[texts.Count];
        if (texts.Count == 0) return results;
        if (DateTime.UtcNow.Ticks < Interlocked.Read(ref _downUntilTicks)) return results; // 熔断中

        // 分批:单批最多 50 条,避免超长请求
        const int chunkSize = 50;
        for (var start = 0; start < texts.Count; start += chunkSize)
        {
            var count = Math.Min(chunkSize, texts.Count - start);
            var ok = await TranslateChunkAsync(texts, results, start, count, from, to, ct);
            if (!ok) return results; // 熔断已打开,剩余批次不再尝试
        }
        return results;
    }

    /// <summary>翻译一个批次(写入 results[start..start+count))。返回 false = 熔断/放弃。</summary>
    private async Task<bool> TranslateChunkAsync(IReadOnlyList<string> texts, string?[] results,
        int start, int count, string from, string to, CancellationToken ct)
    {
        try
        {
            var url = TranslateUrl + "&from=" + MapLang(from) + "&to=" + MapLang(to);
            // 纯字符串数组 + HTML 转义(服务端 HTML 对齐器会把裸 < 粘成伪标签;转义实体可无损往返)
            var body = texts.Skip(start).Take(count).Select(WebUtility.HtmlEncode).ToArray();

            using var resp = await _http.PostAsJsonAsync(url, body, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var errBody = await resp.Content.ReadAsStringAsync(ct);
                return Trip(null, $"Edge HTTP {(int)resp.StatusCode} {errBody[..Math.Min(120, errBody.Length)]}");
            }
            return await FillAsync(resp, results, start, ct);
        }
        catch (Exception ex)
        {
            return Trip(ex, "Edge 连接失败");
        }
    }

    private async Task<bool> FillAsync(HttpResponseMessage resp, string?[] results, int start, CancellationToken ct)
    {
        var arr = JsonSerializer.Deserialize<List<EdgeResponse>>(
            await resp.Content.ReadAsStringAsync(ct));
        if (arr is null) return Trip(null, "Edge 响应解析失败");
        for (var i = 0; i < arr.Count; i++)
        {
            var t = arr[i].Translations?.FirstOrDefault()?.Text;
            if (!string.IsNullOrEmpty(t))
            {
                // 入参做过 HTML 转义,回包里的实体解码一次还原
                results[start + i] = WebUtility.HtmlDecode(t).Trim();
            }
        }
        Interlocked.Exchange(ref _downUntilTicks, 0);
        return true;
    }

    /// <summary>打开熔断 60 秒,返回 false。</summary>
    private bool Trip(Exception? ex, string msg)
    {
        Interlocked.Exchange(ref _downUntilTicks, DateTime.UtcNow.AddSeconds(60).Ticks);
        LastError = ex is not null ? msg + ":" + ex.Message : msg;
        Diag.Dump("edge: " + LastError);
        return false;
    }

    private static string MapLang(string lang) => lang.ToUpperInvariant() switch
    {
        "ZH" or "ZH-CN" or "ZH-HANS" => "zh-Hans",
        "EN" => "en",
        "JA" => "ja",
        "KO" => "ko",
        "FR" => "fr",
        "DE" => "de",
        "RU" => "ru",
        _ => lang.ToLowerInvariant(),
    };

    private sealed class EdgeResponse
    {
        [JsonPropertyName("translations")] public List<EdgeTranslation>? Translations { get; set; }
    }

    private sealed class EdgeTranslation
    {
        [JsonPropertyName("text")] public string? Text { get; set; }
    }
}
