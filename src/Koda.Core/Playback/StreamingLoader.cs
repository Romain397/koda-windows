using System.Buffers;
using System.Net;
using System.Net.Http.Headers;

namespace Koda.Core.Playback;

/// <summary>Supplies ranged audio reads from a bounded, per-voice memory buffer.</summary>
public sealed class StreamingLoader : IDisposable
{
    public const int BufferSize = 256 * 1024;
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(15);
    private static readonly HttpClient SharedHttp = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        MaxResponseDrainSize = 0,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    }) { Timeout = Timeout.InfiniteTimeSpan };

    private readonly ResolvedStream _stream;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _pump = new(1, 1);
    private readonly List<Pending> _requests = [];
    private long _length;
    private byte[]? _buffer;
    private long _windowStart;
    private int _buffered;
    private long _fetchEnd;
    private CancellationTokenSource? _fetch;
    private Exception? _failure;
    private bool _disposed;

    private sealed class Pending(long offset, byte[] destination)
    {
        public byte[] Destination { get; } = destination;
        public long Offset { get; } = offset;
        public int Copied { get; set; }
        public TaskCompletionSource<long> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public StreamingLoader(ResolvedStream stream, HttpMessageHandler? handler = null)
    {
        if (!IsAudioUri(stream.Url)) throw new InvalidDataException("Invalid address for the audio stream.");
        _stream = stream;
        _length = Math.Max(0, stream.ContentLength ?? 0);
        _http = handler is null ? SharedHttp : new HttpClient(handler, disposeHandler: false)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    public async Task<long> GetLengthAsync(CancellationToken cancellation = default)
    {
        if (Interlocked.Read(ref _length) == 0)
            await ReadAsync(0, new byte[1], cancellation).ConfigureAwait(false);
        return Interlocked.Read(ref _length);
    }

    public async Task<long> ReadAsync(long offset, byte[] destination, CancellationToken cancellation = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        Pending request;
        await _pump.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // Frame reads normally hit memory. Avoid a task completion hop or a cancellation
            // registration for the thousands of small AAC frames in an ordinary song.
            var wanted = Wanted(offset, destination.Length);
            var copied = CopyAvailable(offset, destination.AsSpan(0, wanted));
            if (copied == wanted) return copied;
            if (_failure is not null) throw new IOException("The audio stream failed.", _failure);
            request = new Pending(offset, destination) { Copied = copied };
            _requests.Add(request);
            FetchIfNeeded();
        }
        finally
        {
            _pump.Release();
        }

        using var registration = cancellation.Register(() => _ = WithdrawAsync(request, cancellation));
        return await request.Done.Task.ConfigureAwait(false);
    }

    private async Task WithdrawAsync(Pending request, CancellationToken cancellation)
    {
        await _pump.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_requests.Remove(request)) return;
            request.Done.TrySetCanceled(cancellation);
            if (_requests.Count == 0) CancelFetch();
            else FetchIfNeeded();
        }
        finally
        {
            _pump.Release();
        }
    }

    private int Wanted(long offset, int count)
    {
        var limit = _length == 0 ? long.MaxValue : _length;
        return offset >= limit ? 0 : (int)Math.Min(count, limit - offset);
    }

    private int CopyAvailable(long offset, Span<byte> destination)
    {
        var windowOffset = offset - _windowStart;
        if (_buffer is null || windowOffset < 0 || windowOffset >= _buffered) return 0;

        var count = (int)Math.Min(destination.Length, _buffered - windowOffset);
        _buffer.AsSpan((int)windowOffset, count).CopyTo(destination);
        return count;
    }

    private void Serve()
    {
        for (var index = _requests.Count - 1; index >= 0; index--)
        {
            var request = _requests[index];
            var wanted = Wanted(request.Offset, request.Destination.Length);
            if (request.Copied < wanted)
                request.Copied += CopyAvailable(request.Offset + request.Copied,
                    request.Destination.AsSpan(request.Copied, wanted - request.Copied));
            if (request.Copied >= wanted) request.Done.TrySetResult(request.Copied);
            else if (_failure is not null) request.Done.TrySetException(_failure);
            else continue;
            _requests.RemoveAt(index);
        }

        FetchIfNeeded();
    }

    private void FetchIfNeeded()
    {
        if (_disposed || _failure is not null || _requests.Count == 0) return;
        var needed = _requests.Min(request => request.Offset + request.Copied);
        if (_fetch is not null && needed >= _windowStart && needed < _fetchEnd) return;
        CancelFetch();
        _windowStart = needed;
        _buffered = 0;
        var limit = _length == 0 ? long.MaxValue : _length;
        _fetchEnd = needed + Math.Min(BufferSize, limit - needed);
        var end = _fetchEnd;
        var fetch = new CancellationTokenSource(IdleTimeout);
        _fetch = fetch;
        _ = Task.Run(() => FetchAsync(needed, end, fetch));
    }

    private void CancelFetch()
    {
        var fetch = _fetch;
        _fetch = null;
        fetch?.Cancel();
    }

    private async Task FetchAsync(long offset, long end, CancellationTokenSource fetch)
    {
        Exception? failure = null;
        var chunk = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            using var response = await SendAsync(offset, end, fetch.Token).ConfigureAwait(false);
            var length = ResponseLength(response, offset, end, out var responseEnd);
            await _pump.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_fetch != fetch) return;
                if (_length != 0 && _length != length)
                    throw new InvalidDataException("The audio stream changed length.");
                Interlocked.Exchange(ref _length, length);
                _fetchEnd = responseEnd;
            }
            finally
            {
                _pump.Release();
            }

            await using var body = await response.Content.ReadAsStreamAsync(fetch.Token).ConfigureAwait(false);
            for (var remaining = responseEnd - offset; remaining > 0;)
            {
                var read = await body.ReadAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, remaining)), fetch.Token)
                    .ConfigureAwait(false);
                if (read == 0) throw new EndOfStreamException("The audio range ended early.");
                remaining -= read;
                fetch.CancelAfter(IdleTimeout);
                await _pump.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (_fetch != fetch) return;
                    _buffer ??= new byte[BufferSize];
                    chunk.AsSpan(0, read).CopyTo(_buffer.AsSpan(_buffered));
                    _buffered += read;
                    Serve();
                }
                finally
                {
                    _pump.Release();
                }
            }
        }
        catch (Exception error)
        {
            failure = error;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
            await _pump.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_fetch == fetch)
                {
                    _fetch = null;
                    _failure = failure;
                    Serve();
                }
            }
            finally
            {
                fetch.Dispose();
                _pump.Release();
            }
        }
    }

    private async Task<HttpResponseMessage> SendAsync(long offset, long end, CancellationToken cancellation)
    {
        var uri = _stream.Url;
        for (var redirects = 0; ; redirects++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Range = new RangeHeaderValue(offset, end - 1);
            request.Headers.TryAddWithoutValidation("User-Agent", _stream.UserAgent);
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation)
                .ConfigureAwait(false);
            if (response.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.Found
                or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect))
                return response;

            var location = response.Headers.Location;
            response.Dispose();
            if (redirects >= 3 || location is null || !Uri.TryCreate(uri, location, out var next) || !IsAudioUri(next))
                throw new IOException("The audio server sent an invalid redirect.");
            uri = next;
        }
    }

    private static bool IsAudioUri(Uri uri) =>
        uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && uri.UserInfo.Length == 0
        && (uri.IdnHost.Equals("googlevideo.com", StringComparison.OrdinalIgnoreCase)
            || uri.IdnHost.EndsWith(".googlevideo.com", StringComparison.OrdinalIgnoreCase));

    private static long ResponseLength(HttpResponseMessage response, long offset, long end, out long responseEnd)
    {
        var headers = response.Content.Headers;
        if (headers.ContentEncoding.Count == 0)
        {
            if (response.StatusCode == HttpStatusCode.PartialContent
                && headers.ContentRange is { Unit: "bytes", From: { } from, To: { } to, Length: { } length }
                && from == offset && to >= from && to < end && to < length
                && (headers.ContentLength is not { } count || count == to - from + 1))
            {
                responseEnd = to + 1;
                return length;
            }

            if (response.StatusCode == HttpStatusCode.OK && offset == 0 && headers.ContentLength is > 0 and var total)
            {
                responseEnd = Math.Min(end, total);
                return total;
            }
        }

        throw new IOException($"The audio server did not return the requested range ({(int)response.StatusCode}).");
    }

    public void Dispose()
    {
        _pump.Wait();
        try
        {
            if (_disposed) return;
            _disposed = true;
            CancelFetch();
            _buffer = null;
            _buffered = 0;
            foreach (var request in _requests)
                request.Done.TrySetException(new ObjectDisposedException(nameof(StreamingLoader)));
            _requests.Clear();
        }
        finally
        {
            _pump.Release();
        }

        if (_http != SharedHttp) _http.Dispose();
    }
}
