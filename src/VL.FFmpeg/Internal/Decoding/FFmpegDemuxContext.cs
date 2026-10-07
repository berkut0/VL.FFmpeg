using System.Runtime.InteropServices;
using System.Diagnostics;
using VL.FFmpeg.Internal.Interop;
using VL.FFmpeg.Interop.AutoGen;

namespace VL.FFmpeg.Internal.Decoding;

/// <summary>One persistent container. Only the I/O owner reads or seeks it.</summary>
internal unsafe sealed class FFmpegDemuxContext : IDisposable
{
    private AVFormatContext* _context;
    private readonly AVIOInterruptCB_callback _interrupt;
    private Func<bool> _isCancelled;
    private readonly MediaInputOptions? _options;
    private long _deadline;
    public AVFormatContext* Context => _context;
    public string Filename { get; }
    public double OriginSeconds { get; }
    public TimeSpan Duration { get; }
    public string FormatName => Marshal.PtrToStringUTF8((nint)_context->iformat->name) ?? "";

    public FFmpegDemuxContext(string filename, CancellationToken token, string? runtimePath = null,
        MediaInputOptions? options = null)
    {
        Filename = filename;
        _options = options;
        _isCancelled = () => token.IsCancellationRequested;
        _interrupt = _ => Volatile.Read(ref _isCancelled)() || Expired ? 1 : 0;
        var runtime = FFmpegRuntime.Probe(runtimePath);
        if (!runtime.Available) throw new InvalidOperationException(runtime.Status);
        try
        {
            _context = ffmpeg.avformat_alloc_context();
            if (_context is null) throw new OutOfMemoryException();
            _context->interrupt_callback = new() { callback = _interrupt };
            AVDictionary* nativeOptions = null;
            try
            {
                if (options?.NativeOptions is { } values)
                    foreach (var (key, value) in values)
                        Check(ffmpeg.av_dict_set(&nativeOptions, key, value, 0), "set input option");
                BeginOperation(options?.OpenTimeout);
                var context = _context;
                var result = ffmpeg.avformat_open_input(&context, filename, null, &nativeOptions);
                _context = context;
                CheckOperation(result, "open media");
                if (ffmpeg.av_dict_count(nativeOptions) > 0)
                    throw new NotSupportedException("FFmpeg did not accept the requested input options.");
                CheckOperation(ffmpeg.avformat_find_stream_info(_context, null), "inspect media streams");
            }
            finally { _deadline = 0; ffmpeg.av_dict_free(&nativeOptions); }
            OriginSeconds = _context->start_time == ffmpeg.AV_NOPTS_VALUE ? 0 : _context->start_time / (double)ffmpeg.AV_TIME_BASE;
            Duration = _context->duration > 0 && _context->duration != ffmpeg.AV_NOPTS_VALUE
                ? TimeSpan.FromSeconds(_context->duration / (double)ffmpeg.AV_TIME_BASE) : TimeSpan.Zero;
        }
        catch { Dispose(); throw; }
    }

    public void SetCancellation(CancellationToken token) => Volatile.Write(ref _isCancelled, () => token.IsCancellationRequested);
    public void Resume(CancellationToken token)
    {
        SetCancellation(token);
        if (_context->pb is not null) { _context->pb->error = 0; _context->pb->eof_reached = 0; }
    }
    public int Read(AVPacket* packet)
    {
        BeginOperation(_options?.ReadTimeout);
        try
        {
            var result = ffmpeg.av_read_frame(_context, packet);
            CheckInterruption();
            return result;
        }
        finally { _deadline = 0; }
    }

    private bool Expired => Volatile.Read(ref _deadline) is var end && end != 0 && Stopwatch.GetTimestamp() >= end;
    private void BeginOperation(TimeSpan? timeout)
        => Volatile.Write(ref _deadline, timeout is { } limit && limit > TimeSpan.Zero
            ? Stopwatch.GetTimestamp() + (long)(limit.TotalSeconds * Stopwatch.Frequency) : 0);
    private void CheckInterruption()
    {
        if (Volatile.Read(ref _isCancelled)()) throw new OperationCanceledException();
        if (Expired) throw new TimeoutException("FFmpeg network operation timed out.");
    }
    private void CheckOperation(int result, string operation) { CheckInterruption(); Check(result, operation); }

    public void Seek(TimeSpan position)
    {
        var target = checked((long)Math.Round((position.TotalSeconds + OriginSeconds) * ffmpeg.AV_TIME_BASE));
        Check(ffmpeg.av_seek_frame(_context, -1, target, ffmpeg.AVSEEK_FLAG_BACKWARD), "seek media");
        if (_context->pb is not null)
        {
            _context->pb->error = 0;
            _context->pb->eof_reached = 0;
        }
    }

    public void Dispose()
    {
        if (_context is not null)
        {
            var context = _context;
            ffmpeg.avformat_close_input(&context);
            _context = null;
        }
        GC.KeepAlive(_interrupt);
    }

    internal static void Check(int result, string operation)
    {
        if (result >= 0) return;
        Span<byte> buffer = stackalloc byte[ffmpeg.AV_ERROR_MAX_STRING_SIZE];
        fixed (byte* p = buffer)
        {
            ffmpeg.av_strerror(result, p, (ulong)buffer.Length);
            throw new FFmpegDecodeException(operation, result, Marshal.PtrToStringUTF8((nint)p) ?? "FFmpeg error");
        }
    }
}
