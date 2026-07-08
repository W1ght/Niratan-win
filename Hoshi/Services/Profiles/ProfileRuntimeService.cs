using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hoshi.Models;
using Hoshi.Models.Profiles;
using Hoshi.Services.Dictionary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Hoshi.Services.Profiles;

public sealed class ProfileRuntimeService : IProfileRuntimeService, IDictionaryProfileContext
{
    private readonly IProfileService _profiles;
    private readonly ProfileSettingsStore _settingsStore;
    private readonly IServiceProvider _services;
    private readonly ILogger<ProfileRuntimeService> _logger;
    private readonly SemaphoreSlim _activationLock = new(1, 1);
    private ProfileResolution _activeResolution;

    public ProfileRuntimeService(
        IProfileService profiles,
        ProfileSettingsStore settingsStore,
        IServiceProvider services,
        ILogger<ProfileRuntimeService> logger)
    {
        _profiles = profiles;
        _settingsStore = settingsStore;
        _services = services;
        _logger = logger;
        _activeResolution = _profiles.Resolve(ProfileContext.Global());
    }

    public ProfileResolution ActiveResolution => _activeResolution;

    public string ActiveProfileId => _activeResolution.Profile.Id;

    public ContentLanguageProfile ActiveLanguage => _activeResolution.Language;

    public IReadOnlyList<string> ProfileIds => _profiles.Profiles
        .Select(profile => profile.Id)
        .ToList();

    public event EventHandler<ProfileResolution>? ProfileChanged;

    public Task InitializeAsync(CancellationToken ct = default) =>
        ActivateAsync(ProfileContext.Global(), ct);

    public Task ActivateGlobalAsync(CancellationToken ct = default) =>
        ActivateAsync(ProfileContext.Global(), ct);

    public async Task ActivateProfileAsync(
        string profileId,
        bool setGlobalActive = true,
        CancellationToken ct = default)
    {
        if (setGlobalActive)
            await _profiles.SetGlobalActiveProfileAsync(profileId, ct);

        await ActivateAsync(new ProfileContext(ProfileContextKind.Global, profileId), ct);
    }

    public Task ActivateForBookAsync(NovelBook book, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(book);
        return ActivateAsync(ProfileContext.Book(book.ProfileId, book.Language), ct);
    }

    public Task ActivateForVideoAsync(VideoItem video, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(video);
        return ActivateAsync(ProfileContext.Video(video.ProfileId), ct);
    }

    public Task SaveActiveSettingsAsync(CancellationToken ct = default) =>
        _settingsStore.SaveActiveAsync(ct);

    public string GetDictionaryConfigRoot(string profileId)
    {
        var root = Path.Combine(_profiles.GetProfileDirectory(profileId), "dictionaries");
        Directory.CreateDirectory(root);
        return root;
    }

    public bool EnableUnconfiguredDictionariesForProfile(string profileId) =>
        string.Equals(profileId, ProfileConstants.DefaultJapaneseProfileId, StringComparison.Ordinal);

    private async Task ActivateAsync(ProfileContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await _activationLock.WaitAsync(ct);
        try
        {
            var resolution = _profiles.Resolve(context);
            if (string.Equals(_activeResolution.Profile.Id, resolution.Profile.Id, StringComparison.Ordinal)
                && string.Equals(_settingsStore.ActiveProfileId, resolution.Profile.Id, StringComparison.Ordinal))
            {
                return;
            }

            _activeResolution = resolution;
            await _settingsStore.ActivateAsync(resolution.Profile.Id, ct);
            var lookup = _services.GetService<IDictionaryLookupService>();
            if (lookup is not null)
                await lookup.SetActiveLanguageAsync(resolution.Language.Id);

            _logger.LogInformation(
                "[Profiles] Activated profile {ProfileId} ({Language}) for {ContextKind}",
                resolution.Profile.Id,
                resolution.Language.Id,
                resolution.Context.Kind);
            ProfileChanged?.Invoke(this, resolution);
        }
        finally
        {
            _activationLock.Release();
        }
    }
}
