using FluentAssertions;

namespace Niratan.Tests.Services.Sync;

public sealed class TtuSyncSettingsAssetTests
{
    private static readonly string ProjectRoot = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..",
        "..",
        "..",
        "..",
        "..",
        "Niratan"));

    [Fact]
    public void SettingsNavigation_ExposesTtuSyncSettingsPage()
    {
        var settingsXaml = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "SettingsPage.xaml"));
        var advancedXaml = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "AdvancedSettingsPage.xaml"));
        var advancedCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "AdvancedSettingsPage.xaml.cs"));

        settingsXaml.Should().Contain("AutomationProperties.AutomationId=\"SettingsSyncNavItem\"");
        settingsXaml.Should().Contain("Tag=\"Niratan.Views.Pages.TtuSyncSettingsPage\"");
        advancedXaml.Should().Contain("AutomationProperties.AutomationId=\"SyncSettingsButton\"");
        advancedCode.Should().Contain("SyncSettings_Click");
        advancedCode.Should().Contain("NavigateSettingsSubpage(typeof(TtuSyncSettingsPage))");
    }

    [Fact]
    public void TtuSyncSettingsPage_UsesLocalizedSettingsControls()
    {
        var pagePath = Path.Combine(ProjectRoot, "Views", "Pages", "TtuSyncSettingsPage.xaml");
        var codePath = Path.Combine(ProjectRoot, "Views", "Pages", "TtuSyncSettingsPage.xaml.cs");
        var viewModelPath = Path.Combine(ProjectRoot, "ViewModels", "Pages", "TtuSyncSettingsPageViewModel.cs");
        var appCode = File.ReadAllText(Path.Combine(ProjectRoot, "App.xaml.cs"));
        var enResources = File.ReadAllText(Path.Combine(ProjectRoot, "Strings", "en-US", "Resources.resw"));
        var zhResources = File.ReadAllText(Path.Combine(ProjectRoot, "Strings", "zh-CN", "Resources.resw"));

        File.Exists(pagePath).Should().BeTrue();
        File.Exists(codePath).Should().BeTrue();
        File.Exists(viewModelPath).Should().BeTrue();

        var pageXaml = File.ReadAllText(pagePath);
        var pageCode = File.ReadAllText(codePath);
        var viewModel = File.ReadAllText(viewModelPath);

        pageXaml.Should().Contain("x:Class=\"Niratan.Views.Pages.TtuSyncSettingsPage\"");
        pageXaml.Should().Contain("AutomationProperties.AutomationId=\"TtuSyncEnableToggle\"");
        pageXaml.Should().Contain("AutomationProperties.AutomationId=\"TtuSyncGoogleClientIdTextBox\"");
        pageXaml.Should().Contain("x:Uid=\"TtuSyncGoogleClientSecretPasswordBox\"");
        pageXaml.Should().Contain("AutomationProperties.AutomationId=\"TtuSyncGoogleClientSecretPasswordBox\"");
        pageXaml.Should().Contain("Password=\"{x:Bind ViewModel.GoogleClientSecret, Mode=TwoWay}\"");
        pageXaml.Should().Contain("AutomationProperties.AutomationId=\"TtuSyncConnectGoogleDriveButton\"");
        pageXaml.Should().Contain("AutomationProperties.AutomationId=\"TtuSyncSignOutGoogleDriveButton\"");
        pageXaml.Should().Contain("AutomationProperties.AutomationId=\"TtuSyncClearGoogleDriveCacheButton\"");
        pageXaml.Should().Contain("AutomationProperties.AutomationId=\"TtuSyncModeComboBox\"");
        pageXaml.Should().Contain("AutomationProperties.AutomationId=\"TtuSyncAutoSyncToggle\"");
        pageXaml.Should().Contain("AutomationProperties.AutomationId=\"TtuSyncUploadBooksToggle\"");
        pageXaml.Should().Contain("x:Uid=\"TtuSyncExplanationText\"");
        pageXaml.Should().Contain("ViewModel.CanEditGoogleDriveCredentials");
        pageXaml.Should().Contain("ViewModel.IsGoogleDriveDisconnected");
        pageXaml.Should().Contain("ViewModel.IsGoogleDriveConnected");
        pageXaml.Should().Contain("AutomationProperties.AutomationId=\"TtuSyncStatisticsToggle\"");
        pageXaml.Should().Contain("AutomationProperties.AutomationId=\"TtuSyncSasayakiToggle\"");
        pageXaml.Should().Contain("ViewModel.ShowStatisticsSync");
        pageXaml.Should().Contain("ViewModel.ShowSasayakiSync");
        pageCode.Should().Contain("App.GetService<TtuSyncSettingsPageViewModel>()");
        pageCode.Should().Contain("await ViewModel.InitializeAsync()");
        viewModel.Should().Contain("TtuSyncSettings");
        appCode.Should().Contain("AddTransient<TtuSyncSettingsPageViewModel>");
        appCode.Should().Contain("AddSingleton<ITtuSyncService, TtuSyncService>");
        appCode.Should().Contain("AddSingleton<ITtuSyncRemoteStore, GoogleDriveTtuSyncRemoteStore>");
        appCode.Should().Contain("AddSingleton<IGoogleDriveAuthService, GoogleDriveAuthService>");

        foreach (var key in new[]
        {
            "TtuSyncSettingsPageTitle.Text",
            "TtuSyncGeneralSectionHeader.Text",
            "TtuSyncEnableToggle.Header",
            "TtuSyncEnableToggle.Description",
            "TtuSyncExplanationText.Text",
            "TtuSyncClientCredentialsSectionHeader.Text",
            "TtuSyncGoogleDriveSectionHeader.Text",
            "TtuSyncGoogleClientIdTextBox.Header",
            "TtuSyncGoogleClientIdTextBox.Description",
            "TtuSyncGoogleClientSecretPasswordBox.Header",
            "TtuSyncGoogleClientSecretPasswordBox.Description",
            "TtuSyncConnectionStatusCard.Header",
            "TtuSyncConnectionStatusText.Text",
            "TtuSyncConnectGoogleDriveButton.Content",
            "TtuSyncSignOutGoogleDriveButton.Content",
            "TtuSyncClearGoogleDriveCacheButton.Content",
            "TtuSyncBehaviorSectionHeader.Text",
            "TtuSyncModeComboBox.Header",
            "TtuSyncAutoSyncToggle.Header",
            "TtuSyncDataSectionHeader.Text",
            "TtuSyncUploadBooksToggle.Header",
            "TtuSyncStatisticsToggle.Header",
            "TtuSyncStatisticsToggle.Description",
            "TtuSyncSasayakiToggle.Header",
            "TtuSyncSasayakiToggle.Description",
            "TtuSyncModeAuto",
            "TtuSyncModeManual",
            "TtuSyncStatusConnected",
            "TtuSyncStatusNotConnected",
            "TtuSyncStatusConnecting",
            "TtuSyncClientIdRequiredStatus",
            "TtuSyncClientSecretRequiredStatus",
            "TtuSyncConnectionFailedFormat",
            "TtuSyncCredentialLoadFailedFormat",
            "TtuSyncClearCacheTitle",
            "TtuSyncClearCacheMessage",
            "TtuSyncCacheClearedStatus",
            "TtuSyncClearCacheFailedFormat",
            "TtuSyncSignOutTitle",
            "TtuSyncSignOutMessage",
            "TtuSyncSignOutFailedFormat",
        })
        {
            enResources.Should().Contain(key);
            zhResources.Should().Contain(key);
        }
    }

    [Fact]
    public void StatisticsSettingsPage_GatesGroupsWithoutResettingPreferences()
    {
        var pageXaml = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "StatisticsSettingsPage.xaml"));
        var viewModelCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "ViewModels", "Pages", "StatisticsSettingsPageViewModel.cs"));

        pageXaml.Should().Contain("ViewModel.ShowStatisticsOptions");
        pageXaml.Should().Contain("ViewModel.ShowStatisticsSyncOptions");
        viewModelCode.Should().NotContain("EnableSync = false");
        viewModelCode.Should().NotContain("SelectedSyncMode = StatisticsSyncMode.Merge;");
    }
}
