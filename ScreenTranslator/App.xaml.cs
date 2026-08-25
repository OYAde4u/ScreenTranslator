using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace ScreenTranslator;

public partial class App : Application
{
    [DllImport("user32.dll")]
    private static extern bool SetProcessDPIAware();

    protected override async void OnStartup(StartupEventArgs e)
    {
        // 全局异常落盘(调试用)
        var crashPath = Path.Combine(AppContext.BaseDirectory, "crash.txt");
        DispatcherUnhandledException += (_, args) =>
        {
            File.WriteAllText(crashPath, "DISPATCH " + args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            File.WriteAllText(crashPath, "APPDOMAIN " + args.ExceptionObject);

        // 声明 DPI 感知:截图/覆盖窗口/OCR bbox 全程使用同一套物理像素坐标系
        SetProcessDPIAware();

        // 命令行自检模式(--diag / --selftest-*):命中即执行并退出,不创建主窗口
        if (await SelfTests.TryRunAsync(e.Args)) return;

        base.OnStartup(e);

        // 手动创建主窗口(StartupUri 已移除,保证 selftest 模式不创建 UI)
        new MainWindow().Show();
    }
}
