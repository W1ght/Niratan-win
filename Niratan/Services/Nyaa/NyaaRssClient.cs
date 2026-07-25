using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Niratan.Models.Common;
using Niratan.Models.Nyaa;

namespace Niratan.Services.Nyaa;

public sealed class NyaaRssClient : INyaaClient
{
    private static readonly Uri BaseUri = new("https://nyaa.si/");
    private const long MaximumRssBytes = 2 * 1024 * 1024;
    private readonly HttpClient _httpClient;
    private readonly NyaaRssParser _parser;

    public NyaaRssClient(HttpClient httpClient, NyaaRssParser parser)
    {
        _httpClient = httpClient;
        _parser = parser;
    }

    public async Task<Result<IReadOnlyList<NyaaTorrentItem>>> SearchAsync(
        NyaaSearchRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Query))
            return Result<IReadOnlyList<NyaaTorrentItem>>.Failure("Enter a search query.", "Nyaa search");

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var category = IsValidCategory(request.CategoryCode) ? request.CategoryCode : "0_0";
            var relative = $"?page=rss&q={Uri.EscapeDataString(request.Query.Trim())}"
                + $"&c={Uri.EscapeDataString(category)}&f=0&p={Math.Max(1, request.Page)}";
            using var response = await _httpClient.GetAsync(
                new Uri(BaseUri, relative),
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
            response.EnsureSuccessStatusCode();
            EnsureSameOrigin(response.RequestMessage?.RequestUri);
            var xml = await ReadRssAsync(response.Content, timeout.Token);
            return Result<IReadOnlyList<NyaaTorrentItem>>.Success(_parser.Parse(xml, BaseUri));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Result<IReadOnlyList<NyaaTorrentItem>>.Cancelled();
        }
        catch (OperationCanceledException)
        {
            return Result<IReadOnlyList<NyaaTorrentItem>>.Failure(
                "Nyaa did not respond within 30 seconds.",
                "Nyaa search timed out");
        }
        catch (Exception ex)
        {
            return Result<IReadOnlyList<NyaaTorrentItem>>.Failure(ex.Message, "Nyaa search failed");
        }
    }

    private static bool IsValidCategory(string value)
    {
        var parts = value.Split('_');
        return parts.Length == 2
            && int.TryParse(parts[0], out var main)
            && int.TryParse(parts[1], out var sub)
            && main is >= 0 and <= 6
            && sub is >= 0 and <= 4;
    }

    private static void EnsureSameOrigin(Uri? uri)
    {
        if (uri is null
            || !uri.Scheme.Equals(BaseUri.Scheme, StringComparison.OrdinalIgnoreCase)
            || !uri.Host.Equals(BaseUri.Host, StringComparison.OrdinalIgnoreCase)
            || uri.Port != BaseUri.Port
            || uri.UserInfo.Length != 0)
        {
            throw new InvalidDataException("Nyaa redirected the RSS request outside its allowed origin.");
        }
    }

    private static async Task<string> ReadRssAsync(HttpContent content, CancellationToken ct)
    {
        if (content.Headers.ContentLength > MaximumRssBytes)
            throw new InvalidDataException("Nyaa RSS response exceeded the 2 MiB safety limit.");

        await using var input = await content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            long total = 0;
            int read;
            while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
            {
                total += read;
                if (total > MaximumRssBytes)
                    throw new InvalidDataException("Nyaa RSS response exceeded the 2 MiB safety limit.");
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
            }

            return Encoding.UTF8.GetString(output.GetBuffer(), 0, checked((int)output.Length));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
