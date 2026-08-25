using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScreenTranslator.Services.Translate;

/// <summary>
/// Edge/必应翻译免费接口(Edge 浏览器内置翻译所用端点,国内网络可直接访问,无需 key)。
/// 流程:GET https://edge.microsoft.com/translate/auth 取 JWT → POST cognitive.microsofttranslator.com。
/// 天然支持批量(一次请求多条文本),与段落聚合翻译配合最好;
/// 批量失败整批返回 null(由管道行级降级到 MyMemory)。
/// </summary>
public sealed class EdgeTranslator : ITranslator
{
    private const string AuthUrl = "https://edge.microsoft.com/translate/auth";
    private const string TranslateUrl =
        "https://api-edge.cognitive.microsofttranslator.com/translate?api-version=3.0";

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private string? _token;
    private long _tokenExpireTicks;
    private long _downUntilTicks;

    public string Name => "Edge";

    /// <summary>最近一次失败原因(供 UI 提示)。</summary>
    public string? LastError { get; private set; }

    public EdgeTranslator()
    {
        // 国内直连,不走系统代理(代理反而可能把请求挂起)
        var handler = new HttpClientHandler { UseProxy = false };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(6) };
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
            var token = await GetTokenAsync(ct);
            if (token is null) return false;

            var url = TranslateUrl + "&from=" + MapLang(from) + "&to=" + MapLang(to);
            var body = texts.Skip(start).Take(count).Select(t => new EdgeRequest { Text = t }).ToArray();

            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.Add("Authorization", "Bearer " + token);
            req.Content = JsonContent.Create(body);

            using var resp = await _http.SendAsync(req, ct);
            if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                // token 提前失效:清缓存重试一次
                InvalidateToken();
                token = await GetTokenAsync(ct);
                if (token is null) return false;
                using var req2 = new HttpRequestMessage(HttpMethod.Post, url);
                req2.Headers.Add("Authorization", "Bearer " + token);
                req2.Content = JsonContent.Create(body);
                using var resp2 = await _http.SendAsync(req2, ct);
                if (!resp2.IsSuccessStatusCode) return Trip(ex: null, $"Edge HTTP {(int)resp2.StatusCode}");
                return await FillAsync(resp2, results, start, ct);
            }
            if (!resp.IsSuccessStatusCode) return Trip(ex: null, $"Edge HTTP {(int)resp.StatusCode}");
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
            var t = arr[i].Translations?.FirstOrDefault()?.Text?.Trim();
            if (!string.IsNullOrEmpty(t)) results[start + i] = t;
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

    /// <summary>取 JWT(带缓存,提前 60 秒过期;并发去重)。失败返回 null 并打开短熔断。</summary>
    private async Task<string?> GetTokenAsync(CancellationToken ct)
    {
        if (_token is not null && DateTime.UtcNow.Ticks < _tokenExpireTicks) return _token;
        await _tokenGate.WaitAsync(ct);
        try
        {
            if (_token is not null && DateTime.UtcNow.Ticks < _tokenExpireTicks) return _token;
            var jwt = await _http.GetStringAsync(AuthUrl, ct);
            if (string.IsNullOrWhiteSpace(jwt)) { Trip(null, "Edge auth 返回空"); return null; }
            _token = jwt.Trim();
            // JWT 有效期 10 分钟,保守按 8 分钟缓存
            _tokenExpireTicks = DateTime.UtcNow.AddMinutes(8).Ticks;
            return _token;
        }
        catch (Exception ex)
        {
            Trip(ex, "Edge auth 失败");
            return null;
        }
        finally
        {
            _tokenGate.Release();
        }
    }

    private void InvalidateToken()
    {
        _token = null;
        _tokenExpireTicks = 0;
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

    private sealed class EdgeRequest
    {
        [JsonPropertyName("Text")] public string Text { get; set; } = "";
    }

    private sealed class EdgeResponse
    {
        [JsonPropertyName("translations")] public List<EdgeTranslation>? Translations { get; set; }
    }

    private sealed class EdgeTranslation
    {
        [JsonPropertyName("text")] public string? Text { get; set; }
    }
}
