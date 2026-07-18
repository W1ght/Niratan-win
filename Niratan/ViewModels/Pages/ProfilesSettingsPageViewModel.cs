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
    bool IsDefault,
    bool IsActive)
{
    public bool CanDelete => !IsDefault;
}

public partial class ProfilesSettingsPageViewModel : ObservableObject
{
    private readonly IProfileService _profileService;
    private readonly IProfileRuntimeService _profileRuntime;

    public ObservableCollection<ProfileOption> Profiles { get; } = [];
    public IReadOnlyList<ContentLanguageProfile> AvailableLanguages { get; } =
        ContentLanguageProfile.All
            .Select(language => new ContentLanguageProfile(language.Id, GetLanguageDisplayName(language.Id)))
            .ToArray();

    [ObservableProperty]
    public partial string NewProfileName { get; set; } = "";

    [ObservableProperty]
    public partial ContentLanguageProfile NewProfileLanguage { get; set; } = null!;

    [ObservableProperty]
    public partial bool IsCreateEditorVisible { get; set; }

    [ObservableProperty]
    public partial bool IsRenameEditorVisible { get; set; }

    [ObservableProperty]
    public partial string RenameProfileName { get; set; } = "";

    [ObservableProperty]
    public partial string StatusText { get; set; } = "";

    [ObservableProperty]
    public partial bool IsOperationInProgress { get; set; }

    public ProfileOption? EditingProfile { get; private set; }

    public IAsyncRelayCommand<ProfileOption> ActivateProfileCommand { get; }
    public IRelayCommand BeginCreateProfileCommand { get; }
    public IRelayCommand CancelCreateProfileCommand { get; }
    public IAsyncRelayCommand CreateProfileCommand { get; }
    public IRelayCommand<ProfileOption> BeginRenameProfileCommand { get; }
    public IRelayCommand CancelRenameProfileCommand { get; }
    public IAsyncRelayCommand RenameProfileCommand { get; }
    public IAsyncRelayCommand<ProfileOption> DeleteProfileCommand { get; }

    public ProfilesSettingsPageViewModel(
        IProfileService profileService,
        IProfileRuntimeService profileRuntime)
    {
        _profileService = profileService;
        _profileRuntime = profileRuntime;
        NewProfileLanguage = AvailableLanguages[0];
        ActivateProfileCommand = new AsyncRelayCommand<ProfileOption>(ActivateProfileAsync);
        BeginCreateProfileCommand = new RelayCommand(BeginCreateProfile);
        CancelCreateProfileCommand = new RelayCommand(CancelCreateProfile);
        CreateProfileCommand = new AsyncRelayCommand(CreateProfileAsync);
        BeginRenameProfileCommand = new RelayCommand<ProfileOption>(BeginRenameProfile);
        CancelRenameProfileCommand = new RelayCommand(CancelRenameProfile);
        RenameProfileCommand = new AsyncRelayCommand(RenameProfileAsync);
        DeleteProfileCommand = new AsyncRelayCommand<ProfileOption>(DeleteProfileAsync);
        RefreshProfiles();
    }

    private void BeginCreateProfile()
    {
        NewProfileName = "";
        NewProfileLanguage = AvailableLanguages.First(language =>
            language.Id == _profileRuntime.ActiveLanguage.Id);
        IsRenameEditorVisible = false;
        IsCreateEditorVisible = true;
        StatusText = "";
    }

    private void CancelCreateProfile() => IsCreateEditorVisible = false;

    private async Task CreateProfileAsync()
    {
        if (IsOperationInProgress)
            return;

        var name = NewProfileName.Trim();
        if (name.Length == 0)
        {
            StatusText = ResourceStringHelper.GetString(
                "ProfilesBlankNameError",
                "Profile name cannot be empty.");
            return;
        }

        IsOperationInProgress = true;
        try
        {
            await _profileRuntime.SaveActiveSettingsAsync();
            var profile = await _profileService.CreateProfileAsync(
                name,
                NewProfileLanguage.Id,
                copyFromProfileId: _profileRuntime.ActiveProfileId);
            await _profileRuntime.ActivateProfileAsync(profile.Id);
            IsCreateEditorVisible = false;
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

    private async Task ActivateProfileAsync(ProfileOption? option)
    {
        if (option is null || option.IsActive || IsOperationInProgress)
            return;

        IsOperationInProgress = true;
        try
        {
            await _profileRuntime.ActivateProfileAsync(option.Id);
            StatusText = ResourceStringHelper.FormatString(
                "ProfilesUsingStatus",
                "Using {0}.",
                option.Name);
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

    private void BeginRenameProfile(ProfileOption? option)
    {
        if (option is null)
            return;

        EditingProfile = option;
        RenameProfileName = option.Name;
        IsCreateEditorVisible = false;
        IsRenameEditorVisible = true;
        OnPropertyChanged(nameof(EditingProfile));
    }

    private void CancelRenameProfile()
    {
        EditingProfile = null;
        IsRenameEditorVisible = false;
        OnPropertyChanged(nameof(EditingProfile));
    }

    private async Task RenameProfileAsync()
    {
        if (EditingProfile is null || IsOperationInProgress)
            return;

        var name = RenameProfileName.Trim();
        if (name.Length == 0)
        {
            StatusText = ResourceStringHelper.GetString(
                "ProfilesBlankNameError",
                "Profile name cannot be empty.");
            return;
        }

        IsOperationInProgress = true;
        try
        {
            await _profileService.RenameProfileAsync(EditingProfile.Id, name);
            CancelRenameProfile();
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

    private async Task DeleteProfileAsync(ProfileOption? option)
    {
        if (option is null || !option.CanDelete || IsOperationInProgress)
            return;

        IsOperationInProgress = true;
        try
        {
            await _profileService.DeleteProfileAsync(option.Id);
            if (option.IsActive)
                await _profileRuntime.ActivateGlobalAsync();
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

    private void RefreshProfiles()
    {
        Profiles.Clear();
        foreach (var option in _profileService.Profiles.Select(ToOption))
            Profiles.Add(option);
    }

    private ProfileOption ToOption(NiratanProfile profile) =>
        new(
            profile.Id,
            GetProfileDisplayName(profile),
            profile.Language.Id,
            GetLanguageDisplayName(profile.Language.Id),
            profile.IsDefault,
            string.Equals(profile.Id, _profileRuntime.ActiveProfileId, StringComparison.Ordinal));

    private static string GetLanguageDisplayName(string languageId) =>
        string.Equals(languageId, ContentLanguageProfile.English.Id, StringComparison.OrdinalIgnoreCase)
            ? ResourceStringHelper.GetString("ProfilesLanguageEnglish", "English")
            : ResourceStringHelper.GetString("ProfilesLanguageJapanese", "Japanese");

    private static string GetProfileDisplayName(NiratanProfile profile) =>
        profile.Id == ProfileConstants.DefaultJapaneseProfileId
            && profile.Name is "Japanese" or "Japanese EPUB"
                ? ResourceStringHelper.GetString("ProfilesDefaultJapanese", "Japanese")
                : profile.Name;
}
