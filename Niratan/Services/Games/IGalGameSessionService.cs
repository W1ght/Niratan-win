using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Niratan.Models.Games;

namespace Niratan.Services.Games;

public interface IGalGameSessionService : IDisposable
{
    GalHookSessionState State { get; }
    event EventHandler<GalHookSessionState>? StateChanged;

    Task<GalHookOperationResult> LaunchAsync(
        GalGameEntry game,
        CancellationToken ct = default);

    Task<GalHookOperationResult> AttachAsync(
        int processId,
        CancellationToken ct = default);

    IReadOnlyList<GalGameTextLine> PollText();
    IReadOnlyList<GalGameThreadPreview> ReadThreadPreviews();
    bool SelectTextThread(ulong threadId);
    GalGameAudioCapture? CaptureAudio(GalGameTextLine line);
    bool SetIngameLookupEnabled(bool enabled);
    GalGameLookupHit? PollIngameLookupHit(ulong afterSequence, out ulong hitCount);
    IReadOnlyList<GalGameLookupInput> PollIngameLookupInputs(
        ulong afterSequence,
        out ulong inputCount);
    bool PublishIngameLookupFrame(GalGameLookupCardFrame frame);
    bool DismissIngameLookup(ulong hitSequence);

    Task StopAsync();
}
