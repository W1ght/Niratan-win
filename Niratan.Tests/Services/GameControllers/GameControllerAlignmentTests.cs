using FluentAssertions;
using Niratan.Models.GameControllers;
using Niratan.Models.Shortcuts;
using Niratan.Services.GameControllers;

namespace Niratan.Tests.Services.GameControllers;

public sealed class GameControllerAlignmentTests
{
    private static readonly string ProjectRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    [Fact]
    public void Defaults_MatchNiratanControllerBindings()
    {
        var configuration = GameControllerConfiguration.Defaults();

        configuration.BindingFor(GameControllerAction.PreviousPage).Input.Should().Be("dpadLeft");
        configuration.BindingFor(GameControllerAction.NextPage).Input.Should().Be("dpadRight");
        configuration.BindingFor(GameControllerAction.PreviousSasayakiCue).Input.Should().Be("leftShoulder");
        configuration.BindingFor(GameControllerAction.PlayPauseSasayaki).Input.Should().Be("buttonA");
        configuration.BindingFor(GameControllerAction.NextSasayakiCue).Input.Should().Be("rightShoulder");
        configuration.BindingFor(GameControllerAction.ReplaySasayakiCue).Input.Should().Be("buttonX");
        configuration.BindingFor(GameControllerAction.JumpSasayakiCue).Input.Should().Be("buttonB");
        configuration.BindingFor(GameControllerAction.ToggleStatistics).Input.Should().Be("buttonY");
    }

    [Fact]
    public void Actions_ReuseReaderAndSasayakiShortcutActionIds()
    {
        GameControllerActions.ShortcutActionId(GameControllerAction.PreviousPage)
            .Should().Be(ReaderShortcutActions.PreviousPage.Id);
        GameControllerActions.ShortcutActionId(GameControllerAction.NextPage)
            .Should().Be(ReaderShortcutActions.NextPage.Id);
        GameControllerActions.ShortcutActionId(GameControllerAction.PreviousSasayakiCue)
            .Should().Be(SasayakiShortcutActions.PreviousCue.Id);
        GameControllerActions.ShortcutActionId(GameControllerAction.PlayPauseSasayaki)
            .Should().Be(SasayakiShortcutActions.PlayPause.Id);
        GameControllerActions.ShortcutActionId(GameControllerAction.NextSasayakiCue)
            .Should().Be(SasayakiShortcutActions.NextCue.Id);
        GameControllerActions.ShortcutActionId(GameControllerAction.ReplaySasayakiCue)
            .Should().Be(SasayakiShortcutActions.ReplayCue.Id);
        GameControllerActions.ShortcutActionId(GameControllerAction.JumpSasayakiCue)
            .Should().Be(SasayakiShortcutActions.JumpCue.Id);
        GameControllerActions.ShortcutActionId(GameControllerAction.ToggleStatistics)
            .Should().Be(ReaderShortcutActions.ToggleStatistics.Id);
    }

    [Theory]
    [InlineData(GameControllerFamily.Xbox, "buttonA", "A")]
    [InlineData(GameControllerFamily.PlayStation, "buttonA", "Cross")]
    [InlineData(GameControllerFamily.Nintendo, "buttonA", "B")]
    [InlineData(GameControllerFamily.Generic, "buttonA", "A / Cross / B")]
    [InlineData(GameControllerFamily.Xbox, "leftShoulder", "LB")]
    [InlineData(GameControllerFamily.PlayStation, "leftShoulder", "L1")]
    [InlineData(GameControllerFamily.Nintendo, "leftShoulder", "L")]
    [InlineData(GameControllerFamily.Generic, "leftThumbstickUp", "Left Stick ↑")]
    public void Labels_UseConnectedControllerFamily(
        GameControllerFamily family,
        string input,
        string expected)
    {
        GameControllerBindingLabels.For(new GameControllerBinding(input), family)
            .Should().Be(expected);
    }

    [Theory]
    [InlineData("Xbox Wireless Controller", 0, GameControllerFamily.Xbox)]
    [InlineData("DualSense Wireless Controller", 0, GameControllerFamily.PlayStation)]
    [InlineData("Nintendo Switch Pro Controller", 0, GameControllerFamily.Nintendo)]
    [InlineData("Unknown USB Controller", 0x045e, GameControllerFamily.Xbox)]
    [InlineData("Unknown USB Controller", 0x054c, GameControllerFamily.PlayStation)]
    [InlineData("Unknown USB Controller", 0x057e, GameControllerFamily.Nintendo)]
    [InlineData("Unknown USB Controller", 0, GameControllerFamily.Generic)]
    public void FamilyDetection_UsesVendorIdentityAndDisplayName(
        string displayName,
        ushort vendorId,
        GameControllerFamily expected)
    {
        GameControllerBindingLabels.DetectFamily(displayName, vendorId)
            .Should().Be(expected);
    }

    [Fact]
    public void WindowsUiAndReader_AreWiredToControllerService()
    {
        var settingsXaml = File.ReadAllText(
            Path.Combine(ProjectRoot, "Niratan", "Views", "Pages", "SettingsPage.xaml"));
        var controllerXaml = File.ReadAllText(
            Path.Combine(ProjectRoot, "Niratan", "Views", "Pages", "GameControllerSettingsPage.xaml"));
        var appCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Niratan", "App.xaml.cs"));
        var readerCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Niratan", "Views", "Pages", "NovelReaderPage.xaml.cs"));
        var navigationCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Niratan", "Services", "UI", "NavigationService.cs"));

        settingsXaml.Should().Contain("Tag=\"Niratan.Views.Pages.GameControllerSettingsPage\"");
        controllerXaml.Should().Contain("AutomationProperties.AutomationId=\"GameControllerConnectionStatus\"");
        controllerXaml.Should().Contain("AutomationProperties.AutomationId=\"GameControllerResetDefaultsButton\"");
        controllerXaml.Should().Contain("ItemsSource=\"{x:Bind ViewModel.Sections, Mode=OneWay}\"");
        appCode.Should().Contain("AddSingleton<IGameControllerService, GameControllerService>()");
        appCode.Should().Contain("GetService<IGameControllerService>().Start()");
        readerCode.Should().Contain("_gameControllerService.ActionInvoked += OnGameControllerActionInvoked");
        readerCode.Should().Contain("GameControllerActions.ShortcutActionId(e.Action)");
        navigationCode.Should().Contain("typeof(GameControllerSettingsPage) => AppPage.GameControllerSettingsPage");
    }
}
