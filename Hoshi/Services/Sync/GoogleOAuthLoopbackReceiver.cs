using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Hoshi.Services.Sync;

public sealed class GoogleOAuthLoopbackReceiver : IGoogleOAuthLoopbackReceiver
{
    public Task<GoogleOAuthLoopbackSession> StartAsync(
        string state,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(state);

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var redirectUri = new Uri($"http://127.0.0.1:{port}/");
        var callbackTask = ReceiveCallbackAsync(listener, ct);

        return Task.FromResult(new GoogleOAuthLoopbackSession(
            redirectUri,
            callbackTask,
            () =>
            {
                listener.Stop();
                return ValueTask.CompletedTask;
            }));
    }

    private static async Task<GoogleOAuthCallback> ReceiveCallbackAsync(
        TcpListener listener,
        CancellationToken ct)
    {
        try
        {
            using var registration = ct.Register(listener.Stop);
            using var client = await listener.AcceptTcpClientAsync(ct);
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);

            var requestLine = await reader.ReadLineAsync(ct) ?? "";
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync(ct)))
            {
            }

            var callback = ParseRequestLine(requestLine);
            await WriteResponseAsync(stream, callback, ct);
            return callback;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static GoogleOAuthCallback ParseRequestLine(string requestLine)
    {
        var parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return new GoogleOAuthCallback(null, null, "invalid_request");

        var uri = new Uri(new Uri("http://127.0.0.1"), parts[1]);
        var query = ParseQuery(uri.Query);
        query.TryGetValue("code", out var code);
        query.TryGetValue("state", out var state);
        query.TryGetValue("error", out var error);
        return new GoogleOAuthCallback(code, state, error);
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        query = query.TrimStart('?');
        if (query.Length == 0)
            return result;

        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            var key = separator >= 0 ? part[..separator] : part;
            var value = separator >= 0 ? part[(separator + 1)..] : "";
            result[Decode(key)] = Decode(value);
        }

        return result;
    }

    private static string Decode(string value) =>
        Uri.UnescapeDataString(value.Replace('+', ' '));

    private static async Task WriteResponseAsync(
        Stream stream,
        GoogleOAuthCallback callback,
        CancellationToken ct)
    {
        var title = string.IsNullOrWhiteSpace(callback.Error)
            ? "Google Drive connected"
            : "Google Drive authorization failed";
        var body = $"""
            <!doctype html>
            <html lang="en">
            <head><meta charset="utf-8"><title>{WebUtility.HtmlEncode(title)}</title></head>
            <body>{WebUtility.HtmlEncode(title)}. You can close this window.</body>
            </html>
            """;
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bodyBytes.Length}\r\nConnection: close\r\n\r\n");

        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(bodyBytes, ct);
    }
}
