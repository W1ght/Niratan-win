using System.Threading;
using System.Threading.Tasks;

namespace Niratan.Models.Anki;

public sealed record SasayakiMiningAudioRequest(
    bool CaptureAudioClip,
    string? DirectMediaDirectory);

public sealed record SasayakiMiningAudioResult(
    string? AudioClipPath = null,
    string? AudioClipTag = null,
    string? ErrorMessage = null);

public delegate Task<SasayakiMiningAudioResult> SasayakiMiningAudioProvider(
    SasayakiMiningAudioRequest request,
    CancellationToken ct);
