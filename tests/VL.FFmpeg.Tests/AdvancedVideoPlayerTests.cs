using NUnit.Framework;
using VL.FFmpeg.Nodes;

namespace VL.FFmpeg.Tests;

public sealed class AdvancedVideoPlayerTests
{
    [Test]
    public void NodeHasOnlyOutputs()
    {
        var update = typeof(AdvancedVideoPlayer).GetMethod(nameof(AdvancedVideoPlayer.Update));
        var parameters = update!.GetParameters();
        var processNode = typeof(AdvancedVideoPlayer).GetCustomAttributesData().Single(
            attribute => attribute.AttributeType.Name == "ProcessNodeAttribute");
        var displayName = processNode.NamedArguments.Single(
            argument => argument.MemberName == "Name").TypedValue.Value;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(displayName, Is.EqualTo("VideoPlayer (Advanced Controls)"));
            Assert.That(parameters, Is.Not.Empty);
            Assert.That(parameters, Has.Some.Matches<System.Reflection.ParameterInfo>(
                parameter => parameter.Name == "videoSource"));
            Assert.That(parameters, Has.Some.Matches<System.Reflection.ParameterInfo>(
                parameter => parameter.Name == "audioSource"));
            Assert.That(parameters, Has.Some.Matches<System.Reflection.ParameterInfo>(
                parameter => parameter.Name == "control"));
            Assert.That(parameters.All(parameter => parameter.IsOut), Is.True);
        }
    }

    [Test]
    public void OpenPublishesOneAtomicRequestFromTheBeginning()
    {
        using var player = new AdvancedVideoPlayer();
        var control = GetControl(player);

        control.Open("clip.mp4", play: false);

        var options = player.Source.Options;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(options.Filename, Is.EqualTo("clip.mp4"));
            Assert.That(options.Play, Is.False);
            Assert.That(options.SeekTime, Is.Zero);
            Assert.That(options.SeekRequestId, Is.EqualTo(1));
            Assert.That(options.Revision, Is.EqualTo(1));
            Assert.That(control.Filename, Is.EqualTo("clip.mp4"));
        }
    }

    [Test]
    public void SeekAndStopAreRepeatableAtomicCommands()
    {
        using var player = new AdvancedVideoPlayer();
        var control = GetControl(player);

        control.Open("clip.mp4");
        control.Seek(3d);
        var afterSeek = player.Source.Options;
        control.Seek(3d);
        var repeatedSeek = player.Source.Options;
        control.Stop();
        var stopped = player.Source.Options;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(afterSeek.SeekTime, Is.EqualTo(3d));
            Assert.That(repeatedSeek.SeekRequestId, Is.EqualTo(afterSeek.SeekRequestId + 1));
            Assert.That(stopped.Play, Is.False);
            Assert.That(stopped.SeekTime, Is.Zero);
            Assert.That(stopped.SeekRequestId, Is.EqualTo(repeatedSeek.SeekRequestId + 1));
            Assert.That(stopped.Revision, Is.EqualTo(repeatedSeek.Revision + 1));
        }
    }

    [Test]
    public void PausePlayLoopDecodeModeAndCloseUpdateTransport()
    {
        using var player = new AdvancedVideoPlayer();
        var control = GetControl(player);

        control.Open("clip.mp4");
        control.Pause();
        Assert.That(player.Source.Options.Play, Is.False);

        control.Play();
        control.SetLoop(true);
        control.SetDecodeMode(DecodeMode.Software);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(player.Source.Options.Play, Is.True);
            Assert.That(player.Source.Options.Loop, Is.True);
            Assert.That(player.Source.Options.DecodeMode, Is.EqualTo(DecodeMode.Software));
        }

        control.Close();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(player.Source.Options.Filename, Is.Empty);
            Assert.That(player.Source.Options.Play, Is.False);
            Assert.That(player.Source.Options.SeekTime, Is.Zero);
        }
    }

    [Test]
    public void DisposedPlayerRejectsRemoteCommands()
    {
        var player = new AdvancedVideoPlayer();
        var control = GetControl(player);

        ((IDisposable)player).Dispose();

        Assert.Throws<ObjectDisposedException>((Action)control.Play);
    }

    private static VideoPlayerControl GetControl(AdvancedVideoPlayer player)
    {
        player.Update(
            out _,
            out _,
            out var control,
            out _,
            out _,
            out _,
            out _,
            out _,
            out _,
            out _,
            out _,
            out _);
        return control;
    }
}
