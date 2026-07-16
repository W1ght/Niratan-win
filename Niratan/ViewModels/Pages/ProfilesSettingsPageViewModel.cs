using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Niratan.Helpers;
using Niratan.Models.Profiles;
using Niratan.Services.Profiles;

namespace Niratan.ViewModels.Pages;

public sealed record ProfileOption(
    string Id,
    string Name,
    string LanguageId,
    string LanguageDisplayName,
    bool IsDefault);

public partial class ProfilesSettingsPageViewModel : ObservableObject
{
    private readonly IProfileService _profileService;
    private readonly IProfileRuntimeService _profileRuntime;
    private bool _isLoading;

    public ObservableCollection<ProfileOption> Profiles { get; } = [];
    public ObservableCollection<ProfileOption> JapaneseProfiles { get; } = [];
    public ObservableCollection<ProfileOption> EnglishProfiles { get; } = [];
    public IReadOnlyList<ContentLanguageProfile> AvailableLanguages { get; } =
        ContentLanguageProfile.All
            .Select(language => new ContentLanguageProfile(language.Id, GetLanguageDisplayName(language.Id)))
            .ToArray();

    [ObservableProperty]
    public partial string NewProfileName { get; set; } = "";

    [ObservableProperty]
    public partial ContentLanguageProfile NewProfileLanguage { get; set; } = null!;

    [ObservableProperty]
    public partial string? GlobalActiveProfileId { get; set; }

    [ObservableProperty]
    public partial string? JapanesePrimaryProfileId { get; set; }

    [ObservableProperty]
    public partial string? EnglishPrimaryProfileId { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = "";

    [ObservableProperty]
    public partial bool IsOperationInProgress { get; set; }

    public IAsyncRelayCommand CreateProfileCommand { get; }

    public ProfilesSettingsPageViewModel(
        IProfileService profileService,
        IProfileRuntimeService profileRuntime)
    {
        _profileService = profileService;
        _profileRuntime = profileRuntime;
        NewProfileLanguage = AvailableLanguages.First(language => language.Id == ContentLanguageProfile.English.Id);
        CreateProfileCommand = new AsyncRelayCommand(CreateProfileAsync);
        RefreshProfiles();
    }

    partial void OnGlobalActiveProfileIdChanged(string? value)
    {
        if (_isLoading || string.IsNullOrWhiteSpace(value))
            return;

        _ = ActivateProfileIdAsync(value);
    }

    partial void OnJapanesePrimaryProfileIdChanged(string? value)
    {
        if (_isLoading || string.IsNullOrWhiteSpace(value))
            return;

        _ = SetPrimaryProfileAsync(ContentLanguageProfile.Japanese.Id, value);
    }

    partial void OnEnglishPrimaryProfileIdChanged(string? value)
    {
        if (_isLoading || string.IsNullOrWhiteSpace(value))
            return;

        _ = SetPrimaryProfileAsync(ContentLanguageProfile.English.Id, value);
    }

    private async Task CreateProfileAsync()
    {
        if (IsOperationInProgress)
            return;

        IsOperationInProgress = true;
        try
        {
            var language = NewProfileLanguage ?? ContentLanguageProfile.English;
            var name = string.IsNullOrWhiteSpace(NewProfileName)
                ? ResourceStringHelper.FormatString("ProfilesDefaultProfileName", "{0} Profile", language.DisplayName)
                : NewProfileName.Trim();
            await _profileRuntime.SaveActiveSettingsAsync();
            var profile = await _profileService.CreateProfileAsync(
                name,
                language.Id,
                ct: default,
                copyFromProfileId: _profileRuntime.ActiveProfileId);
            NewProfileName = "";
            await _profileRuntime.ActivateProfileAsync(profile.Id);
            StatusText = ResourceStringHelper.FormatString(
                "ProfilesCreatedStatus",
                "Created {0}.",
                profile.Name);
            RefreshProfiles();
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
        finally
        {
            IsOperationInProgress = false;
        }
    }

    private async Task ActivateProfileIdAsync(string profileId)
    {
        if (IsOperationInProgress)
            return;

        IsOperationInProgress = true;
        try
        {
            await _profileRuntime.ActivateProfileAsync(profileId);
            StatusText = ResourceStringHelper.FormatString(
                "ProfilesUsingStatus",
                "Using {0}.",
                GetProfileDisplayName(_profileRuntime.ActiveResolution.Profile));
            RefreshProfiles();
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
        finally
        {
            IsOperationInProgress = false;
        }
    }

    private async Task SetPrimaryProfileAsync(string languageId, string profileId)
    {
        try
        {
            await _profileService.SetPrimaryProfileForLanguageAsync(languageId, profileId);
            var language = ContentLanguageProfile.FromId(languageId);
            var profileName = Profiles.FirstOrDefault(profile => profile.Id == profileId)?.Name ?? profileId;
            StatusText = ResourceStringHelper.FormatString(
                "ProfilesBookDefaultStatus",
                "{0} books will use {1}.",
                GetLanguageDisplayName(language.Id),
                profileName);
            RefreshProfiles();
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
    }

    private void RefreshProfiles()
    {
        _isLoading = true;
        try
        {
            Profiles.Clear();
            JapaneseProfiles.Clear();
            EnglishProfiles.Clear();

            foreach (var option in _profileService.Profiles
                         .OrderBy(profile => profile.Language.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                         .ThenBy(profile => profile.Name, StringComparer.CurrentCultureIgnoreCase)
                         .Select(ToOption))
            {
                Profiles.Add(option);
                if (option.LanguageId == ContentLanguageProfile.Japanese.Id)
                    JapaneseProfiles.Add(option);
                if (option.LanguageId == ContentLanguageProfile.English.Id)
                    EnglishProfiles.Add(option);
            }

            GlobalActiveProfileId = _profileRuntime.ActiveProfileId;
            JapanesePrimaryProfileId = _profileService.GetPrimaryProfileIdForLanguage(ContentLanguageProfile.Japanese.Id);
            EnglishPrimaryProfileId = _profileService.GetPrimaryProfileIdForLanguage(ContentLanguageProfile.English.Id);
        }
        finally
        {
            _isLoading = false;
        }
    }

    private static ProfileOption ToOption(NiratanProfile profile) =>
        new(
            profile.Id,
            GetProfileDisplayName(profile),
            profile.Language.Id,
            GetLanguageDisplayName(profile.Language.Id),
            profile.IsDefault);

    private static string GetLanguageDisplayName(string languageId) =>
        string.Equals(languageId, ContentLanguageProfile.English.Id, StringComparison.OrdinalIgnoreCase)
            ? ResourceStringHelper.GetString("ProfilesLanguageEnglish", "English")
            : ResourceStringHelper.GetString("ProfilesLanguageJapanese", "Japanese");

    private static string GetProfileDisplayName(NiratanProfile profile)
    {
        if (!profile.IsDefault)
            return profile.Name;

        return profile.Id switch
        {
            ProfileConstants.DefaultJapaneseProfileId =>
                ResourceStringHelper.GetString("ProfilesDefaultJapaneseEpub", "Japanese EPUB"),
            ProfileConstants.DefaultJapaneseVideoProfileId =>
                ResourceStringHelper.GetString("ProfilesDefaultJapaneseVideo", "Japanese Video"),
            ProfileConstants.DefaultEnglishProfileId =>
                ResourceStringHelper.GetString("ProfilesDefaultEnglishEpub", "English EPUB"),
            ProfileConstants.DefaultEnglishVideoProfileId =>
                ResourceStringHelper.GetString("ProfilesDefaultEnglishVideo", "English Video"),
            _ => profile.Name,
        };
    }
}
