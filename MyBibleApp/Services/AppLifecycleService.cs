using System;
using System.Threading;

namespace MyBibleApp.Services;

internal sealed class AppLifecycleService
{
    private static readonly Lazy<AppLifecycleService> SharedInstance =
        new(() => new(), LazyThreadSafetyMode.ExecutionAndPublication);
    public static AppLifecycleService Instance => SharedInstance.Value;

    public event EventHandler? Suspended;
    public event EventHandler? Resumed;

    // 0 = running, 1 = suspended. Interlocked guard makes Suspend/Resume safe
    // to call from any thread (Android lifecycle vs Avalonia UI thread).
    private int _suspended;

    public void Suspend()
    {
        if (Interlocked.CompareExchange(ref _suspended, 1, 0) != 0) return;
        Suspended?.Invoke(this, EventArgs.Empty);
    }

    public void Resume()
    {
        if (Interlocked.CompareExchange(ref _suspended, 0, 1) != 1) return;
        Resumed?.Invoke(this, EventArgs.Empty);
    }
}
