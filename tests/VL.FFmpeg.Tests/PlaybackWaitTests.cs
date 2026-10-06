using NUnit.Framework;
using VL.FFmpeg.Internal;

namespace VL.FFmpeg.Tests;

public sealed class PlaybackWaitTests
{
    [Test]
    public void CancellationWinsEvenWhenTheWakeSignalAlreadyCompleted()
    {
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        Assert.ThrowsAsync<OperationCanceledException>((Func<Task>)(() =>
            PlaybackWait.ForChange(Task.CompletedTask, TimeSpan.FromSeconds(30), stop.Token)));
    }

    [Test]
    public async Task TinyDeadlineDoesNotBecomeASynchronousBusyLoop()
    {
        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var wait = PlaybackWait.ForChange(never.Task, TimeSpan.FromTicks(1), CancellationToken.None);
        Assert.That(wait.IsCompleted, Is.False);
        await wait.WaitAsync(TimeSpan.FromSeconds(1));
    }
}
