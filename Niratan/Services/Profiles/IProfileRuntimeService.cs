using System;
using System.Threading;
using System.Threading.Tasks;
using Niratan.Models;
using Niratan.Models.Profiles;

namespace Niratan.Services.Profiles;

public interface IProfileRuntimeService
{
    ProfileResolution ActiveResolution { get; }

    string ActiveProfileId { get; }

    ContentLanguageProfile ActiveLanguage { get; }

    event EventHandler<ProfileResolution>? ProfileChanged;

    Task InitializeAsync(CancellationToken ct = default);

    Task ActivateGlobalAsync(CancellationToken ct = default);

    Task ActivateProfileAsync(
        string profileId,
        bool setGlobalActive = true,
        CancellationToken ct = default);

    Task ActivateForBookAsync(NovelBook book, CancellationToken ct = default);

    Task ActivateForVideoAsync(VideoItem video, CancellationToken ct = default);

    Task SaveActiveSettingsAsync(CancellationToken ct = default);
}
