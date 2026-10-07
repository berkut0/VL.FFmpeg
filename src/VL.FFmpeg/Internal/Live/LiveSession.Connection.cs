using System.Diagnostics;
using VL.FFmpeg.Internal.Decoding;
using VL.FFmpeg.Interop.AutoGen;
using VL.FFmpeg.Nodes;

namespace VL.FFmpeg.Internal.Live;

internal sealed partial class LiveSession
{
    private async Task Worker()
    {
        FFmpegDemuxContext? demux = null;
        var policy = new LiveConnectionPolicy();
        long connection = -1, processed = -1;
        var softwareFallback = false;
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var changed = _changed.Next;
                LiveOptions options;
                Binding? binding;
                AudioDemand? demand;
                long revision, desiredConnection;
                lock (_gate)
                { options = _options; binding = _binding?.Retain(); demand = _audioDemand; revision = _revision; desiredConnection = _connectionRevision; }
                using var bindingLease = binding;
                DrainRetired(bindings: true);
                if (processed == revision) { await changed.WaitAsync(_lifetime.Token).ConfigureAwait(false); continue; }
                if (connection != desiredConnection)
                {
                    demux?.Dispose(); demux = null;
                    connection = desiredConnection;
                    policy = new();
                    softwareFallback = false;
                }
                if (!options.Enabled || string.IsNullOrWhiteSpace(options.Url) || (binding is null && demand is null))
                {
                    demux?.Dispose(); demux = null;
                    SetStatus(LivePlaybackPhase.Idle, !options.Enabled ? "Disabled." : "Waiting for URL and a media consumer.", revision);
                    processed = revision;
                    continue;
                }

                using var attemptLifetime = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                var token = attemptLifetime.Token;
                long attempt;
                lock (_gate)
                {
                    if (_revision != revision) continue;
                    attempt = ++_attempt;
                    _epoch = 0;
                    ClearOutputLocked();
                    _clock.Reset(0, true, true);
                }
                Task? operation = null;
                try
                {
                    operation = ConnectAndPlay();
                    while (!operation.IsCompleted)
                    {
                        changed = _changed.Next;
                        bool superseded;
                        lock (_gate) superseded = _revision != revision;
                        if (superseded) attemptLifetime.Cancel();
                        await Task.WhenAny(operation, changed).WaitAsync(_lifetime.Token).ConfigureAwait(false);
                        DrainRetired();
                    }
                    await operation.ConfigureAwait(false);
                    throw new IOException("Live source ended.");

                    async Task ConnectAndPlay()
                    {
                        LiveConnectionPolicy.ValidateUrl(options.Url);
                        if (demux is null)
                        {
                            SetStatus(LivePlaybackPhase.Connecting, "Opening live source.", revision);
                            var inputOptions = MediaInputOptions.ForNetwork(options.Url,
                                options.Transport == LiveTransport.Tcp ? "tcp" : "udp");
                            demux = await MediaPipeline.Io(() => new FFmpegDemuxContext(options.Url, token,
                                options: inputOptions), token).ConfigureAwait(false);
                            _diagnostics.ContainerOpened();
                            if (demux.FormatName.Split(',').Any(name => name is "hls" or "dash" or "concat"))
                                throw new NotSupportedException("Playlist streams are outside the initial live contract.");
                        }
                        demux.Resume(token);
                        SetStatus(LivePlaybackPhase.Buffering, "Waiting for live media.", revision);
                        using var run = new LiveRun(this, demux, binding, demand, options.DecodeMode,
                            softwareFallback, revision, attempt, policy, token);
                        await run.Run().ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested || _lifetime.IsCancellationRequested) { }
                catch (Exception error)
                {
                    lock (_gate) if (_revision != revision) continue;
                    if (options.DecodeMode == DecodeMode.Auto && !softwareFallback && error is FFmpegHardwareException)
                    { softwareFallback = true; SetStatus(LivePlaybackPhase.Buffering, "Switching to software decode.", revision); continue; }
                    demux?.Dispose(); demux = null;
                    lock (_gate) ClearOutputLocked();
                    var delay = LiveConnectionPolicy.CanRetry(error) ? policy.NextDelay() : null;
                    if (delay is null)
                    { SetStatus(LivePlaybackPhase.Faulted, LiveConnectionPolicy.Describe(error), revision); processed = revision; }
                    else
                    {
                        SetStatus(LivePlaybackPhase.Reconnecting, $"{LiveConnectionPolicy.Describe(error)} Retry in {delay.Value.TotalSeconds:0} s.", revision);
                        var retry = Stopwatch.StartNew();
                        while (retry.Elapsed < delay)
                        {
                            changed = _changed.Next;
                            lock (_gate) if (_revision != revision) break;
                            await PlaybackWait.ForChange(changed, delay.Value - retry.Elapsed, _lifetime.Token).ConfigureAwait(false);
                            DrainRetired();
                        }
                    }
                }
                finally
                {
                    attemptLifetime.Cancel();
                    if (operation is not null)
                        try { await operation.ConfigureAwait(false); } catch { /* Observed above; join before native retirement. */ }
                }
            }
        }
        finally { demux?.Dispose(); DrainRetired(bindings: true); }
    }
}
