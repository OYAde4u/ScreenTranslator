using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace ScreenTranslator.Services.Translate;

/// <summary>
/// MyMemory 免费翻译引擎(https://api.mymemory.translated.net,无需 key,有免费限额)。
/// 质量较低(翻译记忆库),作为 DeepLX/Google 均失败后的第三引擎,仅优于 Echo 原文。
/// 单条最长 ~500 字符,超长截断;单行失败返回 null(由管道行级降级)。
/// </summary>
public sealed class MyMemoryTranslator : ITranslator
{
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(4);
    private long _downUntilTicks;

    public string Name => "MyMemory";

    public MyMemoryTranslator()
    {
        var handler = new HttpClientHandler { UseProxy = false };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(4) };
    }

    public async Task<IReadOnlyList<string?>> TranslateAsync(IReadOnlyList<string> texts, string from, string to,
        CancellationToken ct = default)
    {
        var results = new string?[texts.Count];
        var tasks = texts.Select((t, i) => TranslateOneAsync(t, i, from, to, ct, results)).ToArray();
        await Task.WhenAll(tasks);
        return results;
    }

    private async Task TranslateOneAsync(string text, int index, string from, string to,
        CancellationToken ct, string?[] results)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (DateTime.UtcNow.Ticks < Interlocked.Read(ref _downUntilTicks))
                return; // 熔断中:本行失败,交给管道降级

            // 单条长度限制 ~500 字符
            var q = text.Length > 480 ? text[..480] : text;
            var url = "https://api.mymemory.translated.net/get?q=" + Uri.EscapeDataString(q)
                      + "&langpair=" + MapLang(from) + "|" + MapLang(to);

            using var resp = await _http.GetAsync(url, ct);
            resp.EnsureSuccessStatusCode();
            var body = await resp.Content.ReadFromJsonAsync<MmResponse>(cancellationToken: ct);
            var t = body?.ResponseData?.TranslatedText?.Trim();
            if (string.IsNullOrEmpty(t) || t.Contains("MYMEMORY WARNING") || t.StartsWith("QUERY LENGTH LIMIT"))
                return; // 额度用尽/异常响应:本行失败

            results[index] = t;
            Interlocked.Exchange(ref _downUntilTicks, 0); // 成功恢复健康
        }
        catch
        {
            Interlocked.Exchange(ref _downUntilTicks, DateTime.UtcNow.AddSeconds(30).Ticks);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string MapLang(string lang) => lang.ToUpperInvariant() switch
    {
        "ZH" or "ZH-CN" or "ZH-HANS" => "zh-CN",
        "EN" => "en",
        "JA" => "ja",
        "KO" => "ko",
        "FR" => "fr",
        "DE" => "de",
        "RU" => "ru",
        _ => lang.ToLowerInvariant(),
    };

    private sealed class MmResponse
    {
        [JsonPropertyName("responseData")]
        public MmResponseData? ResponseData { get; set; }
    }

    private sealed class MmResponseData
    {
        [JsonPropertyName("translatedText")]
        public string? TranslatedText { get; set; }
    }
}
