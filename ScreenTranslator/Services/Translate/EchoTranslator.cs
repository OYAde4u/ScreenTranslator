namespace ScreenTranslator.Services.Translate;

/// <summary>回显引擎:原样返回(离线演示/兜底,保证管道永不中断、永不返回 null)。</summary>
public sealed class EchoTranslator : ITranslator
{
    public string Name => "Echo";

    public Task<IReadOnlyList<string?>> TranslateAsync(IReadOnlyList<string> texts, string from, string to,
        CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<string?>>(texts.ToArray());
    }
}
