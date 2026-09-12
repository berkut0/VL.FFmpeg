using System.Collections.Concurrent;
using NUnit.Framework;
using VL.FFmpeg.Internal;
using VL.FFmpeg.Nodes;

namespace VL.FFmpeg.Tests;

public sealed class PlaybackControlTests
{
    [Test]
    public void UnchangedValuesDoNotPublish()
    {
        var publishCount = 0;
        var control = new PlaybackControl(_ => publishCount++);
        var initial = control.Options;

        control.SetPlay(true);
        control.SetLoop(false);
        control.SetDecodeMode(DecodeMode.Auto);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(control.Options, Is.SameAs(initial));
            Assert.That(publishCount, Is.Zero);
        }
    }

    [Test]
    public void SeekAlwaysCreatesANewRequest()
    {
        var control = new PlaybackControl(_ => { });

        control.Seek(3d);
        var first = control.Options;
        control.Seek(3d);
        var second = control.Options;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first.SeekRequestId, Is.EqualTo(1));
            Assert.That(second.SeekRequestId, Is.EqualTo(2));
            Assert.That(second.Revision, Is.EqualTo(2));
        }
    }

    [Test]
    public void ConcurrentChangesAreMergedAndPublishedInRevisionOrder()
    {
        var publishedRevisions = new ConcurrentQueue<long>();
        var control = new PlaybackControl(options => publishedRevisions.Enqueue(options.Revision));

        Parallel.Invoke(
            () => control.SetPlay(false),
            () => control.SetLoop(true),
            () => control.SetDecodeMode(DecodeMode.Hardware));

        var options = control.Options;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(options.Play, Is.False);
            Assert.That(options.Loop, Is.True);
            Assert.That(options.DecodeMode, Is.EqualTo(DecodeMode.Hardware));
            Assert.That(options.Revision, Is.EqualTo(3));
            Assert.That(publishedRevisions, Is.EqualTo(new long[] { 1, 2, 3 }));
        }
    }
}
