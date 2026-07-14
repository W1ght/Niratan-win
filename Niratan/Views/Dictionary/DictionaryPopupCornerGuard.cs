using System;

namespace Niratan.Views.Dictionary;

internal static class DictionaryPopupCornerGuard
{
    public static double CalculateInset(double radius)
    {
        if (radius <= 0)
            return 0;

        return Math.Ceiling(radius * (1 - 1 / Math.Sqrt(2)));
    }
}
