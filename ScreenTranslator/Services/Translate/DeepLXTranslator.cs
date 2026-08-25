using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace ScreenTranslator.Services.Translate;

/// <summary>
/// DeepLX 引擎(DeepL 免费接口的本地代理,质量最好,需自部署,默认 127.0.0.1:1188)。
/// 请求格式:POST /translate { "text": "...", "source_lang": "EN", "target_lang": "ZH" }
/// 响应格式:{ "code": 200, "data": { "translations": [ { "text": "..." } ] } }
/// 单行失败返回 null(由管道行级降级),熔断期间整批快速返回 null。
/// </summary>
public sealed class DeepLXTranslator : ITranslator
{
    private readonly HttpClient _http;
    private readonly string _endpoint;
    private readonly SemaphoreSlim _gate = new(4); // 并发上限
    private long _downUntilTicks; // 熔断:DeepLX 不可用时快速降级,避免逐行等待超时

    /// <summary>最近一次失败原因(供 UI 提示,如 429 限流)。</summary>
    public string? LastError { get; private set; }

    public DeepLXTranslator(string endpoint = "http://127.0.0.1:1188/translate")
    {
        _endpoint = endpoint;
        // 短超时 + 禁用代理:DeepLX 未部署时快速失败降级,不让用户干等
        // (系统代理会把 127.0.0.1 请求转发到代理并挂起,导致每批请求都要等满超时)
        var handler = new HttpClientHandler { UseProxy = false };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) };
    }

    public string Name => "DeepLX";

    public async Task<IReadOnlyList<string?>> TranslateAsync(IReadOnlyList<string> texts, string from, string to,
        CancellationToken ct = default)
    {
        var results = new string?[texts.Count];
        // 逐行并发(受 Semaphore 限制),DeepLX 单文本接口;失败的行保持 null
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
            // 熔断检查:DeepLX 已确认不可用时,本行直接失败,让管道降级,不逐行等超时
            if (DateTime.UtcNow.Ticks < Interlocked.Read(ref _downUntilTicks))
                return;

            var payload = new DeepLxRequest
            {
                Text = text,
                SourceLang = MapLang(from),
                TargetLang = MapLang(to),
            };
            using var resp = await _http.PostAsJsonAsync(_endpoint, payload, ct);
            resp.EnsureSuccessStatusCode();
            var body = await resp.Content.ReadFromJsonAsync<DeepLxResponse>(cancellationToken: ct);
            results[index] = body?.Data?.Translations?.FirstOrDefault()?.Text;
            // 成功:恢复健康(清除熔断)
            Interlocked.Exchange(ref _downUntilTicks, 0);
        }
        catch (Exception ex)
        {
            // 失败:打开熔断 60 秒;记录原因(429 = DeepL 官方限流,需配置 API Key);本行保持 null 由管道降级
            Interlocked.Exchange(ref _downUntilTicks, DateTime.UtcNow.AddSeconds(60).Ticks);
            LastError = ex is HttpRequestException hre && hre.StatusCode is { } sc
                ? $"DeepLX HTTP {(int)sc}({sc}):{(sc == System.Net.HttpStatusCode.TooManyRequests ? "DeepL 限流,请配置 DeepL API Key 后重启 DeepLX" : "请检查 DeepLX 服务")}"
                : "DeepLX 连接失败(未启动?)";
        }
        finally
        {
            // 必须释放信号量:否则 DeepLX 不可用时前 4 个请求异常后,后续请求永久死等,整批翻译挂死
            _gate.Release();
        }
    }

    private static string MapLang(string lang) => lang.ToUpperInvariant() switch
    {
        "ZH" or "ZH-CN" or "ZH-HANS" => "ZH",
        "EN" => "EN",
        "JA" => "JA",
        "KO" => "KO",
        "FR" => "FR",
        "DE" => "DE",
        "RU" => "RU",
        _ => lang.ToUpperInvariant(),
    };

    private sealed class DeepLxRequest
    {
        [JsonPropertyName("text")] public string Text { get; set; } = "";
        [JsonPropertyName("source_lang")] public string SourceLang { get; set; } = "";
        [JsonPropertyName("target_lang")] public string TargetLang { get; set; } = "";
    }

    private sealed class DeepLxResponse
    {
        [JsonPropertyName("code")] public int Code { get; set; }
        [JsonPropertyName("data")] public DeepLxData? Data { get; set; }
    }

    private sealed class DeepLxData
    {
        [JsonPropertyName("translations")] public List<DeepLxTranslation>? Translations { get; set; }
    }

    private sealed class DeepLxTranslation
    {
        [JsonPropertyName("text")] public string? Text { get; set; }
    }
}
