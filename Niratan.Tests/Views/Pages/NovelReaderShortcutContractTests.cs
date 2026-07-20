using FluentAssertions;

namespace Niratan.Tests.Views.Pages;

public sealed class NovelReaderShortcutContractTests
{
    private static readonly string ProjectRoot = FindProjectRoot();

    [Fact]
    public void ReaderShortcutDispatch_PrioritizesTopmostPopupBeforeReader()
    {
        var readerCode = ReadProjectFile("Views", "Pages", "NovelReaderPage.xaml.cs");
        var readerBridge = ReadProjectFile("Web", "NovelReader", "reader-bridge.js");
        var overlayCode = ReadProjectFile("Views", "Dictionary", "DictionaryPopupOverlay.cs");
        var popupCode = ReadProjectFile("Views", "Dictionary", "DictionaryLookupPopup.cs");
        var popupScript = ReadProjectFile("Web", "DictionaryPopup", "popup.js");

        readerCode.Should().Contain("_keyboardAcceleratorBindings.Values.Any(existing => existing.Matches(binding))");
        readerCode.Should().Contain("await _popupOverlay.TryHandleShortcutAsync(binding)");
        readerCode.Should().Contain("args.Handled = await TryHandleReaderShortcutBindingAsync(binding);");
        readerCode.Should().Contain("var shortcutBinding = ParseReaderShortcutBinding(shortcutPayload);");
        readerCode.Should().Contain("await TryHandleReaderShortcutBindingAsync(shortcutBinding);");
        readerCode.Should().Contain("_popupOverlay.DismissStarted += OnPopupOverlayDismissStarted;");
        readerCode.Should().Contain("NovelWebView.Focus(FocusState.Programmatic)");
        readerBridge.Should().Contain("\"popup.dismiss\": { key: \"Escape\"");
        readerBridge.Should().Contain("postToHost(\"shortcut\", {");
        readerBridge.Should().NotContain("postToHost(\"shortcut\", { actionId: actionId });");

        popupScript.Should().Contain("window.__niratanPopupShortcutBindings");
        popupScript.Should().Contain("postPopupMessage('shortcut', {");
        popupCode.Should().Contain("private KeyboardShortcutBinding ParsePopupShortcutBinding");
        popupCode.Should().Contain("ShortcutInputMapper.TryGetVirtualKey(binding");
        overlayCode.Should().Contain("await TryHandleShortcutAsync(request.Binding);");

        var popupDispatch = overlayCode.IndexOf("DismissTopmost();", StringComparison.Ordinal);
        var topmostMethod = overlayCode.IndexOf("private void DismissTopmost()", StringComparison.Ordinal);
        var childDismiss = overlayCode.IndexOf("RemoveChild(child);", topmostMethod, StringComparison.Ordinal);
        var rootDismiss = overlayCode.IndexOf("Dismiss();", childDismiss, StringComparison.Ordinal);

        popupDispatch.Should().BeGreaterThanOrEqualTo(0);
        topmostMethod.Should().BeGreaterThan(popupDispatch);
        childDismiss.Should().BeGreaterThan(topmostMethod);
        rootDismiss.Should().BeGreaterThan(childDismiss);
    }

    private static string ReadProjectFile(params string[] parts) =>
        File.ReadAllText(Path.Combine([ProjectRoot, .. parts]));

    private static string FindProjectRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory != null)
            {
                var projectRoot = Path.Combine(directory.FullName, "Niratan");
                if (File.Exists(Path.Combine(projectRoot, "Niratan.csproj")))
                    return projectRoot;

                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the Niratan project root.");
    }
}
