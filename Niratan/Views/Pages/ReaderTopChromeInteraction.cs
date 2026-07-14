namespace Niratan.Views.Pages;

internal enum ReaderTopChromeClickAction
{
    None,
    Open,
    Close,
}

internal static class ReaderTopChromeInteraction
{
    public const double ActivationHeight = 64;

    public static ReaderTopChromeClickAction ResolveBlankClick(
        bool isOpen,
        bool isFocusMode,
        bool isLookupPopupActive,
        double y)
    {
        if (isOpen)
            return ReaderTopChromeClickAction.Close;

        if (isFocusMode || isLookupPopupActive || y > ActivationHeight)
            return ReaderTopChromeClickAction.None;

        return ReaderTopChromeClickAction.Open;
    }
}
