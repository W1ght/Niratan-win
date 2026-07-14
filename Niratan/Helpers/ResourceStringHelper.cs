using System;
using System.Globalization;
using Microsoft.Windows.ApplicationModel.Resources;

namespace Niratan.Helpers;

public static class ResourceStringHelper
{
    private static readonly Lazy<ResourceLoader> Loader = new(() => new ResourceLoader());

    public static string GetString(string resourceId, string fallback)
    {
        try
        {
            var value = Loader.Value.GetString(resourceId);
            return string.IsNullOrEmpty(value) ? fallback : value;
        }
        catch
        {
            return fallback;
        }
    }

    public static string FormatString(string resourceId, string fallbackFormat, params object[] args)
    {
        var format = GetString(resourceId, fallbackFormat);
        return string.Format(CultureInfo.CurrentCulture, format, args);
    }
}
