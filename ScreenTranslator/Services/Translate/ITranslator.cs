namespace ScreenTranslator.Services.Translate;

/// <summary>
/// 翻译引擎抽象。行级批量接口,结果与输入等长;
/// 单个元素为 null 表示"该行本引擎翻译失败",由管道把失败的行降级到下一引擎(行级降级)。
/// 实现不应因单行失败抛异常;整体不可用(熔断/断网)时返回全 null 即可。
/// </summary>
public interface ITranslator
{
    string Name { get; }

    /// <summary>批量翻译。返回与 texts 等长的数组,null 元素 = 该行失败(管道降级处理)。</summary>
    Task<IReadOnlyList<string?>> TranslateAsync(IReadOnlyList<string> texts, string from, string to,
        CancellationToken ct = default);
}
