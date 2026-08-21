using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Niratan.Models.Anki;
using Niratan.Models.Dictionary;
using Niratan.Models.Games;
using Niratan.Services.Dictionary;
using Niratan.Services.Profiles;
using Niratan.Services.Settings;
using Serilog;

namespace Niratan.Services.Games;

/// <summary>
/// Bridges the Fushi v15 lookup area to Niratan's existing dictionary and Anki
/// pipeline.  It owns no dictionary rules: query selection, popup rendering
/// and deferred game media all remain shared with the other modules.
/// </summary>
public sealed class GalGameIngameLookupController : IDisposable
{
    private readonly IGalGameSessionService _session;
    private readonly IDictionaryPopupRequestService _requestService;
    private readonly GalGameLookupCardRenderer _renderer;
    private readonly ISettingsService _settingsService;
    private readonly IProfileRuntimeService _profileRuntime;
    private CancellationTokenSource? _renderCts;
    private int? _enabledProcessId;
    private ulong _lastHitSequence;
    private ulong _lastInputSequence;
    private GalGameLookupCardFrame? _activeFrame;
    private bool _disposed;

    public GalGameIngameLookupController(
        IGalGameSessionService session,
        IDictionaryPopupRequestService requestService,
        GalGameLookupCardRenderer renderer,
        ISettingsService settingsService,
        IProfileRuntimeService profileRuntime)
    {
        _session = session;
        _requestService = requestService;
        _renderer = renderer;
        _settingsService = settingsService;
        _profileRuntime = profileRuntime;
    }

    public async Task PollAsync(
        Func<GalGameTextLine, AnkiMiningContext?> miningContextFactory,
        CancellationToken cancellationToken = default)
    {
        if (_disposed)
            return;

        var state = _session.State;
        var processId = state.GamePid;
        if (processId is not > 0 || !state.IsActive || state.Ipc?.HasIngameLookup != true)
        {
            DisableIfNeeded();
            return;
        }

        if (_enabledProcessId != processId)
        {
            CancelActiveRender();
            _enabledProcessId = processId;
            _lastHitSequence = 0;
            _lastInputSequence = 0;
            _activeFrame = null;
            if (!_session.SetIngameLookupEnabled(true))
            {
                _enabledProcessId = null;
                Log.Warning("[GalLookup] lookup region exists but could not be enabled pid={Pid}", processId);
                return;
            }
            Log.Information("[GalLookup] enabled Fushi lookup bridge pid={Pid}", processId);
        }

        var hit = _session.PollIngameLookupHit(_lastHitSequence, out var hitCount);
        if (hitCount > _lastHitSequence)
            _lastHitSequence = hitCount;
        if (hit is not null)
        {
            _lastHitSequence = Math.Max(_lastHitSequence, hit.Sequence);
            await RenderHitAsync(hit, miningContextFactory, cancellationToken);
        }

        if (_activeFrame is null)
            return;

        var inputs = _session.PollIngameLookupInputs(
            _lastInputSequence,
            out var inputCount);
        if (inputCount > _lastInputSequence)
            _lastInputSequence = inputCount;

        foreach (var input in CoalesceMoveInputs(inputs))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var refreshed = await _renderer.InjectAndCaptureAsync(input, cancellationToken);
            if (refreshed is null || refreshed.HitSequence != _activeFrame.HitSequence)
                continue;

            _activeFrame = refreshed;
            _session.PublishIngameLookupFrame(refreshed);
        }
    }

    private async Task RenderHitAsync(
        GalGameLookupHit hit,
        Func<GalGameTextLine, AnkiMiningContext?> miningContextFactory,
        CancellationToken cancellationToken)
    {
        CancelActiveRender();
        _renderCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ct = _renderCts.Token;
        _activeFrame = null;

        try
        {
            var settings = _settingsService.Current.DictionaryDisplaySettings;
            var candidate = TextSelectionResolver.LookupCandidate(
                hit.Line,
                checked((int)hit.CharacterIndex),
                settings.ScanLength,
                _profileRuntime.ActiveLanguage);
            if (candidate is null)
            {
                _session.DismissIngameLookup(hit.Sequence);
                return;
            }

            var line = new GalGameTextLine
            {
                ProcessId = hit.ProcessId,
                Sequence = hit.Sequence,
                Text = hit.Line,
                HookName = "Fushi LookupHit",
            };
            var context = miningContextFactory(line)
                ?? new AnkiMiningContext { Sentence = hit.Line };
            context.Sentence = hit.Line;
            context.SentenceOffset = candidate.Utf16Start;

            foreach (var query in DictionaryLookupService.EnumerateLookupCandidates(
                         candidate.Text,
                         settings.ScanLength))
            {
                ct.ThrowIfCancellationRequested();
                var request = await _requestService.CreateAsync(
                    query,
                    context,
                    $"galgame-ingame-{hit.ProcessId}-{hit.Sequence:x}",
                    ct);
                if (request is null)
                    continue;

                var frame = await _renderer.RenderAsync(request, hit.Sequence, ct);
                if (frame is null)
                    continue;

                var anchorX = Math.Clamp(
                    hit.GlyphX + Math.Max(1, hit.GlyphWidth) + 16,
                    0,
                    Math.Max(0, hit.ViewWidth - frame.Width));
                var anchorY = Math.Clamp(
                    hit.GlyphY,
                    0,
                    Math.Max(0, hit.ViewHeight - frame.Height));
                var positioned = frame with
                {
                    AnchorX = anchorX,
                    AnchorY = anchorY,
                    HighlightStart = candidate.Utf16Start,
                    HighlightLength = Math.Max(1, checked((int)hit.CharacterCount)),
                };
                ct.ThrowIfCancellationRequested();
                _activeFrame = positioned;
                if (!_session.PublishIngameLookupFrame(positioned))
                    Log.Warning("[GalLookup] lookup frame publish failed pid={Pid} hit={Hit}", hit.ProcessId, hit.Sequence);
                return;
            }

            _session.DismissIngameLookup(hit.Sequence);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[GalLookup] lookup hit processing failed pid={Pid} hit={Hit}", hit.ProcessId, hit.Sequence);
            _session.DismissIngameLookup(hit.Sequence);
        }
    }

    private static IEnumerable<GalGameLookupInput> CoalesceMoveInputs(
        IReadOnlyList<GalGameLookupInput> inputs)
    {
        GalGameLookupInput? pendingMove = null;
        foreach (var input in inputs)
        {
            if (input.Kind == 0)
            {
                pendingMove = input;
                continue;
            }

            if (pendingMove is not null)
            {
                yield return pendingMove;
                pendingMove = null;
            }
            yield return input;
        }

        if (pendingMove is not null)
            yield return pendingMove;
    }

    public async Task StopAsync()
    {
        if (_disposed)
            return;

        CancelActiveRender();
        _activeFrame = null;
        if (_enabledProcessId is not null)
            _session.SetIngameLookupEnabled(false);
        _enabledProcessId = null;
        _lastHitSequence = 0;
        _lastInputSequence = 0;
        await Task.CompletedTask;
    }

    private void DisableIfNeeded()
    {
        if (_enabledProcessId is null)
            return;
        CancelActiveRender();
        _session.SetIngameLookupEnabled(false);
        _enabledProcessId = null;
        _activeFrame = null;
        _lastHitSequence = 0;
        _lastInputSequence = 0;
    }

    private void CancelActiveRender()
    {
        _renderCts?.Cancel();
        _renderCts?.Dispose();
        _renderCts = null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        DisableIfNeeded();
        _renderer.Dispose();
    }
}
