using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Niratan.Enums;

namespace Niratan.Views.Dictionary;

/// <summary>
/// Presents Anki mining feedback in the owning module layer, outside the dictionary popup.
/// </summary>
public sealed class AnkiMiningFeedbackPresenter : IDisposable
{
    private static readonly TimeSpan DisplayDuration = TimeSpan.FromMilliseconds(2200);

    private readonly InfoBar _infoBar;
    private CancellationTokenSource? _hideCts;

    public AnkiMiningFeedbackPresenter(InfoBar infoBar)
    {
        _infoBar = infoBar;
        _infoBar.IsOpen = false;
        _infoBar.IsClosable = false;
        AutomationProperties.SetAutomationId(_infoBar, "AnkiMiningToast");
        AutomationProperties.SetLiveSetting(_infoBar, AutomationLiveSetting.Polite);
    }

    public void Show(DictionaryPopupMiningFeedbackEventArgs feedback)
    {
        CancelHide();
        _hideCts = new CancellationTokenSource();
        _infoBar.Title = feedback.Title;
        _infoBar.Message = feedback.Result.Message;
        _infoBar.Severity = feedback.Severity switch
        {
            NotificationSeverity.Success => InfoBarSeverity.Success,
            NotificationSeverity.Warning => InfoBarSeverity.Warning,
            NotificationSeverity.Error => InfoBarSeverity.Error,
            _ => InfoBarSeverity.Informational,
        };
        _infoBar.IsOpen = true;
        _ = HideAsync(_hideCts.Token);
    }

    public void Clear()
    {
        CancelHide();
        _infoBar.IsOpen = false;
    }

    private async Task HideAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(DisplayDuration, cancellationToken).ConfigureAwait(false);
            _infoBar.DispatcherQueue.TryEnqueue(() =>
            {
                if (!cancellationToken.IsCancellationRequested)
                    _infoBar.IsOpen = false;
            });
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void CancelHide()
    {
        _hideCts?.Cancel();
        _hideCts?.Dispose();
        _hideCts = null;
    }

    public void Dispose()
    {
        Clear();
    }
}
