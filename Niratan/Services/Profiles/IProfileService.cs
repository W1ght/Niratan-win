using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Niratan.Models.Profiles;

namespace Niratan.Services.Profiles;

public interface IProfileService
{
    IReadOnlyList<NiratanProfile> Profiles { get; }

    string ProfilesRoot { get; }

    Task LoadAsync();

    Task SaveAsync();

    Task<NiratanProfile> CreateProfileAsync(
        string name,
        string languageId,
        string? profileId = null,
        CancellationToken ct = default,
        string? copyFromProfileId = null);

    Task RenameProfileAsync(
        string profileId,
        string name,
        CancellationToken ct = default);

    Task DeleteProfileAsync(string profileId, CancellationToken ct = default);

    Task SetPrimaryProfileForLanguageAsync(
        string languageId,
        string profileId,
        CancellationToken ct = default);

    Task SetGlobalActiveProfileAsync(string profileId, CancellationToken ct = default);

    string? GetPrimaryProfileIdForLanguage(string languageId);

    ProfileResolution Resolve(ProfileContext context);

    string GetProfileDirectory(string profileId);
}
