using System.IO;

namespace ScreenTranslator.Services;

/// <summary>统一诊断转储:写 pipe_dump.txt(追加,失败静默)。替代各处重复的 Dump 实现。</summary>
public static class Diag
{
    private static readonly string Path_ = System.IO.Path.Combine(AppContext.BaseDirectory, "pipe_dump.txt");

    public static void Dump(string msg)
    {
        try
        {
            File.AppendAllText(Path_, $"{DateTime.Now:HH:mm:ss.fff} {msg}\r\n");
        }
        catch { /* 诊断写盘失败不影响主流程 */ }
    }
}
