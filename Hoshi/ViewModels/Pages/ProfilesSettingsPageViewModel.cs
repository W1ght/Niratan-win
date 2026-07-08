using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hoshi.Models.Profiles;
using Hoshi.Services.Profiles;

namespace Hoshi.ViewModels.Pages;

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
    public IReadOnlyList<ContentLanguageProfile> AvailableLanguages { get; } = ContentLanguageProfile.All;

    [ObservableProperty]
    public partial string NewProfileName { get; set; } = "";

    [ObservableProperty]
    public partial ContentLanguageProfile NewProfileLanguage { get; set; } = ContentLanguageProfile.English;

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
    public IAsyncRelayCommand<ProfileOption?> ActivateProfileCommand { get; }

    public ProfilesSettingsPageViewModel(
        IProfileService profileService,
        IProfileRuntimeService profileRuntime)
    {
        _profileService = profileService;
        _profileRuntime = profileRuntime;
        CreateProfileCommand = new AsyncRelayCommand(CreateProfileAsync);
        ActivateProfileCommand = new AsyncRelayCommand<ProfileOption?>(ActivateProfileAsync);
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
                ? $"{language.DisplayName} Profile"
                : NewProfileName.Trim();
            var profile = await _profileService.CreateProfileAsync(name, language.Id);
            NewProfileName = "";
            await _profileRuntime.ActivateProfileAsync(profile.Id);
            StatusText = $"Created {profile.Name}.";
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

    private async Task ActivateProfileAsync(ProfileOption? profile)
    {
        if (profile is null)
            return;

        await ActivateProfileIdAsync(profile.Id);
    }

    private async Task ActivateProfileIdAsync(string profileId)
    {
        if (IsOperationInProgress)
            return;

        IsOperationInProgress = true;
        try
        {
            await _profileRuntime.ActivateProfileAsync(profileId);
            StatusText = $"Using {_profileRuntime.ActiveResolution.Profile.Name}.";
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
            StatusText = $"{language.DisplayName} books will use {profileName}.";
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

    private static ProfileOption ToOption(HoshiProfile profile) =>
        new(
            profile.Id,
            profile.Name,
            profile.Language.Id,
            profile.Language.DisplayName,
            profile.IsDefault);
}
