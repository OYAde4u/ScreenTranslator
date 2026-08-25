using System.Windows.Threading;

namespace ScreenTranslator.Services;

/// <summary>
/// 自动触发服务:输入钩子 → 300ms 防抖 → 触发翻译一轮;另有最小间隔冷却,防止高频触发刷屏。
/// 计时在 UI 线程(DispatcherTimer),钩子线程只打时间戳,无跨线程问题。
/// </summary>
public sealed class AutoTriggerService : IDisposable
{
    private readonly InputHookService _hook;
    private readonly DispatcherTimer _timer;
    private readonly Action _onTrigger;
    private readonly TimeSpan _debounce = TimeSpan.FromMilliseconds(300);
    private readonly TimeSpan _minInterval = TimeSpan.FromMilliseconds(800);

    private long _lastSignalTicks = DateTime.MinValue.Ticks;
    private DateTime _lastRun = DateTime.MinValue;

    public AutoTriggerService(Action onTrigger)
    {
        _onTrigger = onTrigger;
        _hook = new InputHookService(() => Interlocked.Exchange(ref _lastSignalTicks, DateTime.Now.Ticks));
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += (_, _) => Check();
    }

    public void Start()
    {
        _hook.Start();
        _timer.Start();
    }

    private void Check()
    {
        var signal = new DateTime(Interlocked.Read(ref _lastSignalTicks));
        if (signal == DateTime.MinValue) return;
        var now = DateTime.Now;
        // 防抖:信号后安静 300ms 才算一次完整交互
        if (now - signal >= _debounce && now - _lastRun >= _minInterval)
        {
            _lastRun = now;
            Interlocked.Exchange(ref _lastSignalTicks, DateTime.MinValue.Ticks);
            _onTrigger();
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _hook.Dispose();
    }
}
