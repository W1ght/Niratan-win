using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Threading;
using Niratan.Models;

namespace Niratan.Services.Video;

/// <summary>
/// Bridges signed Google Video URLs through libmpv's read-only stream callback.
/// The CDN rejects FFmpeg's open-ended Range request, so each upstream request
/// is emitted as a bounded byte range while libmpv keeps normal read/seek semantics.
/// </summary>
internal sealed class MpvHttpRangeStreamBridge : IDisposable
{
    internal const string Protocol = "niratanhttps";
    private const int DefaultChunkSize = 1024 * 1024;

    private static readonly MpvNative.MpvStreamOpenCallback OpenCallback = Open;
    private static readonly MpvNative.MpvStreamReadCallback ReadCallback = Read;
    private static readonly MpvNative.MpvStreamSeekCallback SeekCallback = Seek;
    private static readonly MpvNative.MpvStreamSizeCallback SizeCallback = Size;
    private static readonly MpvNative.MpvStreamCloseCallback CloseCallback = Close;
    private static readonly MpvNative.MpvStreamCancelCallback CancelCallback = Cancel;
    private static readonly IntPtr ReadCallbackPointer = Marshal.GetFunctionPointerForDelegate(ReadCallback);
    private static readonly IntPtr SeekCallbackPointer = Marshal.GetFunctionPointerForDelegate(SeekCallback);
    private static readonly IntPtr SizeCallbackPointer = Marshal.GetFunctionPointerForDelegate(SizeCallback);
    private static readonly IntPtr CloseCallbackPointer = Marshal.GetFunctionPointerForDelegate(CloseCallback);
    private static readonly IntPtr CancelCallbackPointer = Marshal.GetFunctionPointerForDelegate(CancelCallback);

    private readonly ConcurrentDictionary<string, MpvHttpRangeSource> _sources =
        new(StringComparer.Ordinal);
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private GCHandle _selfHandle;
    private bool _registered;
    private bool _disposed;

    public MpvHttpRangeStreamBridge()
        : this(CreateHttpClient(), ownsHttpClient: true)
    {
    }

    internal MpvHttpRangeStreamBridge(HttpClient httpClient, bool ownsHttpClient = false)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _ownsHttpClient = ownsHttpClient;
    }

    public void Register(IntPtr mpvHandle)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (mpvHandle == IntPtr.Zero)
            throw new ArgumentException("A valid libmpv handle is required.", nameof(mpvHandle));
        if (_registered)
            return;

        _selfHandle = GCHandle.Alloc(this);
        var status = MpvNative.StreamCallbackAddReadOnly(
            mpvHandle,
            Protocol,
            GCHandle.ToIntPtr(_selfHandle),
            OpenCallback);
        if (status < 0)
        {
            _selfHandle.Free();
            throw new InvalidOperationException(
                $"Unable to register the remote video stream bridge: {MpvNative.ErrorString(status)}");
        }

        _registered = true;
    }

    public VideoPlaybackRequest Prepare(VideoPlaybackRequest request)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        _sources.Clear();

        var primary = PrepareSource(request.PrimarySource, request.HttpHeaders);
        var audio = string.IsNullOrWhiteSpace(request.ExternalAudioSource)
            ? null
            : PrepareSource(request.ExternalAudioSource, request.HttpHeaders);

        return primary == request.PrimarySource && audio == request.ExternalAudioSource
            ? request
            : request with
            {
                PrimarySource = primary,
                ExternalAudioSource = audio,
            };
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _sources.Clear();
        ReleaseRegistration();
        if (_ownsHttpClient)
            _httpClient.Dispose();
    }

    internal void ReleaseRegistration()
    {
        if (!_registered)
            return;

        if (_selfHandle.IsAllocated)
            _selfHandle.Free();
        _registered = false;
    }

    internal static bool TryCreateSource(
        string source,
        IReadOnlyDictionary<string, string> headers,
        out MpvHttpRangeSource? result)
    {
        result = null;
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri)
            || !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !IsGoogleVideoHost(uri.Host)
            || !TryGetContentLength(uri, out var contentLength))
        {
            return false;
        }

        result = new MpvHttpRangeSource(
            uri,
            contentLength,
            new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase));
        return true;
    }

    private string PrepareSource(string source, IReadOnlyDictionary<string, string> headers)
    {
        if (!TryCreateSource(source, headers, out var descriptor))
            return source;

        var token = Guid.NewGuid().ToString("N");
        _sources[token] = descriptor!;
        return $"{Protocol}://{token}";
    }

    private static int Open(IntPtr userData, IntPtr uriPointer, IntPtr infoPointer)
    {
        GCHandle streamHandle = default;
        try
        {
            var bridge = GCHandle.FromIntPtr(userData).Target as MpvHttpRangeStreamBridge;
            var uri = Marshal.PtrToStringUTF8(uriPointer);
            if (bridge == null || uri == null || !TryReadToken(uri, out var token)
                || !bridge._sources.TryGetValue(token, out var source))
            {
                return MpvNative.MpvErrorLoadingFailed;
            }

            var stream = new MpvHttpRangeStream(bridge._httpClient, source, DefaultChunkSize);
            streamHandle = GCHandle.Alloc(stream);
            var info = new MpvNative.MpvStreamCallbackInfo
            {
                Cookie = GCHandle.ToIntPtr(streamHandle),
                ReadCallback = ReadCallbackPointer,
                SeekCallback = SeekCallbackPointer,
                SizeCallback = SizeCallbackPointer,
                CloseCallback = CloseCallbackPointer,
                CancelCallback = CancelCallbackPointer,
            };
            Marshal.StructureToPtr(info, infoPointer, fDeleteOld: false);
            return 0;
        }
        catch
        {
            if (streamHandle.IsAllocated)
            {
                if (streamHandle.Target is IDisposable disposable)
                    disposable.Dispose();
                streamHandle.Free();
            }
            return MpvNative.MpvErrorLoadingFailed;
        }
    }

    private static long Read(IntPtr cookie, IntPtr buffer, ulong byteCount)
    {
        try
        {
            if (GCHandle.FromIntPtr(cookie).Target is not MpvHttpRangeStream stream)
                return -1;

            var bytes = stream.Read(byteCount);
            if (bytes.Length > 0)
                Marshal.Copy(bytes, 0, buffer, bytes.Length);
            return bytes.Length;
        }
        catch
        {
            return -1;
        }
    }

    private static long Seek(IntPtr cookie, long offset)
    {
        try
        {
            return GCHandle.FromIntPtr(cookie).Target is MpvHttpRangeStream stream
                ? stream.Seek(offset)
                : -1;
        }
        catch
        {
            return -1;
        }
    }

    private static long Size(IntPtr cookie)
    {
        try
        {
            return GCHandle.FromIntPtr(cookie).Target is MpvHttpRangeStream stream
                ? stream.Length
                : -1;
        }
        catch
        {
            return -1;
        }
    }

    private static void Cancel(IntPtr cookie)
    {
        try
        {
            if (GCHandle.FromIntPtr(cookie).Target is MpvHttpRangeStream stream)
                stream.Cancel();
        }
        catch
        {
        }
    }

    private static void Close(IntPtr cookie)
    {
        try
        {
            var handle = GCHandle.FromIntPtr(cookie);
            if (handle.Target is IDisposable disposable)
                disposable.Dispose();
            handle.Free();
        }
        catch
        {
        }
    }

    private static bool TryReadToken(string uri, out string token)
    {
        var prefix = $"{Protocol}://";
        if (!uri.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            token = "";
            return false;
        }

        token = uri[prefix.Length..].Trim('/');
        return token.Length == 32 && token.All(Uri.IsHexDigit);
    }

    private static bool IsGoogleVideoHost(string host) =>
        host.Equals("googlevideo.com", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".googlevideo.com", StringComparison.OrdinalIgnoreCase);

    private static bool TryGetContentLength(Uri uri, out long length)
    {
        length = 0;
        foreach (var item in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = item.Split('=', 2);
            if (!Uri.UnescapeDataString(parts[0]).Equals("clen", StringComparison.Ordinal))
                continue;

            return parts.Length == 2
                   && long.TryParse(Uri.UnescapeDataString(parts[1]), out length)
                   && length > 0;
        }
        return false;
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            UseCookies = false,
        };
        return new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }
}

internal sealed record MpvHttpRangeSource(
    Uri Uri,
    long ContentLength,
    IReadOnlyDictionary<string, string> Headers);

internal sealed class MpvHttpRangeStream : IDisposable
{
    private const int MaximumReadSize = 4 * 1024 * 1024;

    private readonly HttpClient _httpClient;
    private readonly MpvHttpRangeSource _source;
    private readonly int _chunkSize;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private byte[] _cache = [];
    private long _cacheOffset;
    private long _position;
    private bool _disposed;

    internal MpvHttpRangeStream(HttpClient httpClient, MpvHttpRangeSource source, int chunkSize)
    {
        _httpClient = httpClient;
        _source = source;
        _chunkSize = Math.Max(1, chunkSize);
    }

    internal long Length => _source.ContentLength;

    internal byte[] Read(ulong requestedByteCount)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _cts.Token.ThrowIfCancellationRequested();
            if (_position >= Length || requestedByteCount == 0)
                return [];

            var requested = (int)Math.Min(requestedByteCount, MaximumReadSize);
            if (!IsPositionCached())
                FetchChunk();

            var cacheIndex = checked((int)(_position - _cacheOffset));
            var available = _cache.Length - cacheIndex;
            var count = Math.Min(requested, available);
            if (count <= 0)
                return [];

            var result = new byte[count];
            Buffer.BlockCopy(_cache, cacheIndex, result, 0, count);
            _position += count;
            return result;
        }
    }

    internal long Seek(long offset)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (offset < 0 || offset > Length)
                return -1;
            _position = offset;
            return offset;
        }
    }

    internal void Cancel() => _cts.Cancel();

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _cts.Cancel();
        _cts.Dispose();
        _cache = [];
    }

    private bool IsPositionCached() =>
        _position >= _cacheOffset && _position < _cacheOffset + _cache.Length;

    private void FetchChunk()
    {
        var start = _position;
        var end = start > Length - _chunkSize
            ? Length - 1
            : start + _chunkSize - 1;
        using var request = new HttpRequestMessage(HttpMethod.Get, _source.Uri);
        request.Headers.Range = new RangeHeaderValue(start, end);
        foreach (var header in _source.Headers)
        {
            if (header.Key.Equals("Range", StringComparison.OrdinalIgnoreCase)
                || header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase)
                || header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        using var response = _httpClient.Send(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            _cts.Token);
        if (response.StatusCode != HttpStatusCode.PartialContent)
            throw new HttpRequestException("The remote media server rejected a bounded byte-range request.");

        var range = response.Content.Headers.ContentRange;
        if (range?.From != start || range.To is null || range.To > end || range.To < start)
            throw new InvalidDataException("The remote media server returned an invalid content range.");
        if (range.Length.HasValue && range.Length.Value != Length)
            throw new InvalidDataException("The remote media length changed while it was open.");

        var expectedLength = checked((int)(range.To.Value - start + 1));
        using var content = response.Content.ReadAsStream(_cts.Token);
        var bytes = new byte[expectedLength];
        var total = 0;
        while (total < bytes.Length)
        {
            var read = content.Read(bytes, total, bytes.Length - total);
            if (read == 0)
                break;
            total += read;
        }

        if (total == 0)
            throw new EndOfStreamException("The remote media server returned an empty byte range.");
        if (total != bytes.Length)
            Array.Resize(ref bytes, total);
        _cacheOffset = start;
        _cache = bytes;
    }
}
