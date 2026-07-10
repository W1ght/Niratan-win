using System;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace Hoshi.Helpers;

public static class WebView2EnvironmentHelper
{
    private static readonly object SyncRoot = new();
    private static Task<CoreWebView2Environment>? _environmentTask;

    public static Task<CoreWebView2Environment> GetOrCreateAsync()
    {
        lock (SyncRoot)
        {
            return _environmentTask ??= CreateEnvironmentAsync();
        }
    }

    private static async Task<CoreWebView2Environment> CreateEnvironmentAsync()
    {
        return await CoreWebView2Environment.CreateWithOptionsAsync(
            null,
            AppDataHelper.GetWebView2UserDataPath(),
            null);
    }
}
