using System;

namespace MyBibleApp.Services;

internal sealed class AppLifecycleService
{
    private static readonly Lazy<AppLifecycleService> SharedInstance = new(() => new());
    public static AppLifecycleService Instance => SharedInstance.Value;

    public event EventHandler? Suspended;
    public event EventHandler? Resumed;

    private bool _suspended;

    public void Suspend()
    {
        if (_suspended) return;
        _suspended = true;
        Suspended?.Invoke(this, EventArgs.Empty);
    }

    public void Resume()
    {
        if (!_suspended) return;
        _suspended = false;
        Resumed?.Invoke(this, EventArgs.Empty);
    }
}
