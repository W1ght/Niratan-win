using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Hoshi.Models.Profiles;

namespace Hoshi.Services.Profiles;

public interface IProfileService
{
    IReadOnlyList<HoshiProfile> Profiles { get; }

    string ProfilesRoot { get; }

    Task LoadAsync();

    Task SaveAsync();

    Task<HoshiProfile> CreateProfileAsync(
        string name,
        string languageId,
        string? profileId = null,
        CancellationToken ct = default);

    Task SetPrimaryProfileForLanguageAsync(
        string languageId,
        string profileId,
        CancellationToken ct = default);

    Task SetGlobalActiveProfileAsync(string profileId, CancellationToken ct = default);

    string? GetPrimaryProfileIdForLanguage(string languageId);

    ProfileResolution Resolve(ProfileContext context);

    string GetProfileDirectory(string profileId);
}
