namespace VL.FFmpeg.Internal;

/// <summary>Broadcast notification; capture Next before checking the protected condition.</summary>
internal sealed class AsyncPulse
{
    private TaskCompletionSource _next = NewSource();
    public Task Next => Volatile.Read(ref _next).Task;
    public void Pulse() => Interlocked.Exchange(ref _next, NewSource()).TrySetResult();
    private static TaskCompletionSource NewSource() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
