namespace Niratan.Views.Dictionary;

internal enum DictionaryPopupRedirectMode
{
    InPlace,
    Nested,
}

internal static class DictionaryPopupRedirectRouter
{
    public static DictionaryPopupRedirectMode Resolve(DictionaryPopupRedirectRequest request) =>
        request.X is null
        && request.Y is null
        && string.IsNullOrWhiteSpace(request.Source)
            ? DictionaryPopupRedirectMode.InPlace
            : DictionaryPopupRedirectMode.Nested;
}
