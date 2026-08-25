namespace ScreenTranslator.Services.Translate;

/// <summary>
/// 翻译管道:真 LRU 缓存 + 批量去重 + 并发控制 + 行级多引擎降级。
/// - 缓存:同文本永不重复翻译(游戏里重复台词命中率极高,命中延迟为 0);LRU 淘汰热台词不被误驱逐;
/// - 批量去重:同批内重复文本只请求一次;
/// - 并发:引擎请求受信号量限制,不压垮代理;
/// - 行级降级:主引擎翻译失败的行才落入下一引擎,成功的行直接保留——
///   不再"一行失败整批重翻"(双倍延迟),DeepLX 限流时未限流行仍由 DeepLX 出译文;
/// - 永不抛异常:全部引擎失败的行原样返回。记录各行实际使用的引擎名(供 UI 提示)。
/// </summary>
public sealed class TranslationPipeline
{
    private readonly IReadOnlyList<ITranslator> _engines;
    private readonly Dictionary<string, (LinkedListNode<string> Node, string Value)> _cache = new();
    private readonly LinkedList<string> _lru = new();
    private readonly SemaphoreSlim _engineGate = new(4);
    private readonly object _lock = new();
    private const int MaxCacheEntries = 4096;

    public TranslationPipeline(params ITranslator[] engines)
    {
        _engines = engines;
    }

    public int CacheCount { get { lock (_lock) return _cache.Count; } }

    /// <summary>首选翻译引擎(供 UI 读取状态,如 DeepLX 错误信息)。</summary>
    public ITranslator? PrimaryTranslator => _engines.FirstOrDefault();

    /// <summary>引擎链(供 UI 读取各引擎状态)。</summary>
    public IReadOnlyList<ITranslator> Engines => _engines;

    /// <summary>本轮实际完成翻译的引擎名(多个引擎分行完成时用 "+" 连接,如 "DeepLX+MyMemory")。</summary>
    public string? LastEngineName { get; private set; }

    public async Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> texts, string from, string to,
        CancellationToken ct = default)
    {
        var results = new string[texts.Count];

        // 1) 缓存查找(LRU:命中移动到尾部)
        var missing = new List<(int Index, string Text)>();
        for (var i = 0; i < texts.Count; i++)
        {
            var key = Key(from, to, texts[i]);
            lock (_lock)
            {
                if (_cache.TryGetValue(key, out var entry))
                {
                    _lru.Remove(entry.Node);
                    _lru.AddLast(entry.Node);
                    results[i] = entry.Value;
                    continue;
                }
            }
            missing.Add((i, texts[i]));
        }
        if (missing.Count == 0) return results;

        // 2) 去重后待翻文本
        var unique = missing.Select(m => m.Text).Distinct().ToArray();

        // 3) 行级降级:translated[j] = null 的行进入下一引擎,直到全部有值或引擎耗尽
        var translated = new string?[unique.Length];
        var usedEngines = new List<string>();
        var pending = Enumerable.Range(0, unique.Length).ToList();
        foreach (var engine in _engines)
        {
            if (pending.Count == 0) break;
            var r = await TryTranslateAsync(engine,
                pending.Select(j => unique[j]).ToArray(), from, to, ct);
            if (r is null) continue; // 引擎整体异常(未按约定返回 null 而抛错):整批降级

            var stillPending = new List<int>();
            var used = false;
            for (var k = 0; k < pending.Count; k++)
            {
                if (r[k] is { } t) { translated[pending[k]] = t; used = true; }
                else stillPending.Add(pending[k]);
            }
            if (used) usedEngines.Add(engine.Name);
            pending = stillPending;
        }
        LastEngineName = usedEngines.Count > 0 ? string.Join("+", usedEngines.Distinct()) : null;

        // 4) 回填 + 写缓存(LRU 淘汰);所有引擎都失败的行原样返回
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < unique.Length; i++)
            map[unique[i]] = translated[i] ?? unique[i];

        foreach (var (idx, text) in missing)
        {
            results[idx] = map[text];
            lock (_lock)
            {
                var key = Key(from, to, text);
                if (_cache.TryGetValue(key, out var existing))
                {
                    _lru.Remove(existing.Node);
                    _cache.Remove(key);
                }
                while (_cache.Count >= MaxCacheEntries && _lru.First is not null)
                {
                    _cache.Remove(_lru.First.Value);
                    _lru.RemoveFirst();
                }
                var node = _lru.AddLast(key);
                _cache[key] = (node, map[text]);
            }
        }
        return results;
    }

    /// <summary>引擎调用(并发受限)。正常返回行级结果(null 元素=行失败);引擎抛异常返回 null(整批降级)。</summary>
    private async Task<IReadOnlyList<string?>?> TryTranslateAsync(ITranslator engine, IReadOnlyList<string> texts,
        string from, string to, CancellationToken ct)
    {
        try
        {
            await _engineGate.WaitAsync(ct);
            try
            {
                var r = await engine.TranslateAsync(texts, from, to, ct);
                return r.Count == texts.Count ? r : null;
            }
            finally
            {
                _engineGate.Release();
            }
        }
        catch
        {
            return null;
        }
    }

    private static string Key(string from, string to, string text) => from + "|" + to + "|" + text;
}
