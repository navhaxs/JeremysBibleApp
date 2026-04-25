using System;
using System.Threading;
using System.Threading.Tasks;

namespace MyBibleApp.Services.Sync;

/// <summary>
/// Bridges the Android Intent-based OAuth callback to the async auth service.
/// Platform code (MainActivity) registers <see cref="LaunchUri"/> and calls
/// <see cref="TryHandleCallback"/> when the redirect Intent arrives.
/// </summary>
public static class AndroidOAuthCallbackBridge
{
    /// <summary>
    /// Set by platform code to open a URI in the system browser.
    /// On Android this should fire an ACTION_VIEW Intent.
    /// </summary>
    public static Func<string, Task>? LaunchUri { get; set; }

    private static volatile TaskCompletionSource<string?>? _pendingTcs;

    /// <summary>
    /// Returns a Task that completes when <see cref="TryHandleCallback"/> is called
    /// with the redirect URI from the browser.  Only one pending request is
    /// supported at a time; calling this a second time replaces the previous one.
    /// </summary>
    public static Task<string?> WaitForCallbackAsync(CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.Exchange(ref _pendingTcs, tcs);

        cancellationToken.Register(() =>
        {
            var current = Interlocked.CompareExchange(ref _pendingTcs, null, tcs);
            if (current == tcs)
                tcs.TrySetCanceled(cancellationToken);
        });

        return tcs.Task;
    }

    /// <summary>
    /// Called by the Android host (MainActivity.OnNewIntent) when the browser
    /// redirects back to the app with the OAuth callback URI.
    /// </summary>
    /// <param name="callbackUri">Full redirect URI including query parameters.</param>
    /// <returns>True if there was a pending request that was resolved.</returns>
    public static bool TryHandleCallback(string callbackUri)
    {
        var tcs = Interlocked.Exchange(ref _pendingTcs, null);
        if (tcs == null)
            return false;

        tcs.TrySetResult(callbackUri);
        return true;
    }
}

