namespace VL.FFmpeg.Internal;

internal static class PlaybackWait
{
    public static async Task ForChange(Task changed, TimeSpan delay, CancellationToken token)
    {
        // WaitAsync may return an already completed task without inspecting cancellation.
        // Round UP: Task.Delay/WaitAsync truncate sub-millisecond deadlines to zero.
        token.ThrowIfCancellationRequested();
        var timeout = TimeSpan.FromMilliseconds(Math.Max(1, Math.Ceiling(delay.TotalMilliseconds)));
        try { await changed.WaitAsync(timeout, token).ConfigureAwait(false); }
        catch (TimeoutException) { }
        token.ThrowIfCancellationRequested();
    }

    public static async Task ForEitherChange(Task first, Task second, TimeSpan delay, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var pending = CancellationTokenSource.CreateLinkedTokenSource(token);
        try
        {
            await ForChange(Task.WhenAny(first.WaitAsync(pending.Token), second.WaitAsync(pending.Token)), delay, token)
                .ConfigureAwait(false);
        }
        finally { pending.Cancel(); }
    }
}
