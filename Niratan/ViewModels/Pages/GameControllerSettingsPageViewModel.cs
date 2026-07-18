using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Niratan.Helpers;
using Niratan.Models.GameControllers;
using Niratan.Services.GameControllers;

namespace Niratan.ViewModels.Pages;

public partial class GameControllerSettingsPageViewModel : ObservableObject
{
    private readonly IGameControllerService _gameControllerService;
    private bool _isActive;

    public ObservableCollection<GameControllerSectionViewModel> Sections { get; } = [];

    [ObservableProperty]
    public partial bool IsConnected { get; set; }

    [ObservableProperty]
    public partial string ControllerStatusText { get; set; } = "";

    [ObservableProperty]
    public partial bool IsRecording { get; set; }

    [ObservableProperty]
    public partial string RecordingHintText { get; set; } = "";

    public GameControllerSettingsPageViewModel(IGameControllerService gameControllerService)
    {
        _gameControllerService = gameControllerService;
        BuildSections();
        Refresh();
    }

    public void OnNavigatedTo()
    {
        if (_isActive)
            return;

        _isActive = true;
        _gameControllerService.StateChanged += OnControllerStateChanged;
        _gameControllerService.BindingsChanged += OnControllerStateChanged;
        Refresh();
    }

    public void OnNavigatedFrom()
    {
        if (!_isActive)
            return;

        _isActive = false;
        _gameControllerService.StateChanged -= OnControllerStateChanged;
        _gameControllerService.BindingsChanged -= OnControllerStateChanged;
        _gameControllerService.CancelRecording();
    }

    [RelayCommand]
    private void CancelRecording() => _gameControllerService.CancelRecording();

    [RelayCommand]
    private void ResetDefaults() => _gameControllerService.ResetDefaults();

    private void BuildSections()
    {
        Sections.Clear();
        Sections.Add(CreateSection(
            "GameControllerSectionReading",
            "Reading",
            GameControllerAction.PreviousPage,
            GameControllerAction.NextPage));
        Sections.Add(CreateSection(
            "GameControllerSectionSasayaki",
            "Sasayaki",
            GameControllerAction.PreviousSasayakiCue,
            GameControllerAction.PlayPauseSasayaki,
            GameControllerAction.NextSasayakiCue,
            GameControllerAction.ReplaySasayakiCue,
            GameControllerAction.JumpSasayakiCue));
        Sections.Add(CreateSection(
            "GameControllerSectionStatistics",
            "Statistics",
            GameControllerAction.ToggleStatistics));
    }

    private GameControllerSectionViewModel CreateSection(
        string resourceKey,
        string fallbackTitle,
        params GameControllerAction[] actions)
    {
        var rows = new List<GameControllerRowViewModel>();
        foreach (var action in actions)
        {
            var definition = GameControllerActions.Definition(action);
            rows.Add(new GameControllerRowViewModel(
                action,
                ResourceStringHelper.GetString(
                    definition.TitleResourceKey,
                    definition.FallbackTitle),
                _gameControllerService.StartRecording));
        }

        return new GameControllerSectionViewModel(
            ResourceStringHelper.GetString(resourceKey, fallbackTitle),
            rows);
    }

    private void OnControllerStateChanged(object? sender, EventArgs e) => Refresh();

    private void Refresh()
    {
        IsConnected = _gameControllerService.IsConnected;
        ControllerStatusText = IsConnected
            ? _gameControllerService.ConnectedControllerName
                ?? ResourceStringHelper.GetString(
                    "GameControllerConnectedFallback",
                    "Compatible controller")
            : ResourceStringHelper.GetString(
                "GameControllerNotConnected",
                "Not Connected");

        var recordingAction = _gameControllerService.RecordingAction;
        IsRecording = recordingAction != null;
        RecordingHintText = recordingAction is { } action
            ? ResourceStringHelper.FormatString(
                "GameControllerWaitingForInputFormat",
                "Waiting for controller input for {0}...",
                ResourceStringHelper.GetString(
                    GameControllerActions.Definition(action).TitleResourceKey,
                    GameControllerActions.Definition(action).FallbackTitle))
            : "";

        foreach (var section in Sections)
        {
            foreach (var row in section.Rows)
            {
                var binding = _gameControllerService.GetBinding(row.Action);
                row.Update(
                    _gameControllerService.GetDisplayLabel(binding),
                    recordingAction == row.Action,
                    ResourceStringHelper.GetString(
                        "GameControllerPressInput",
                        "Press controller..."));
            }
        }
    }
}

public sealed record GameControllerSectionViewModel(
    string Title,
    IReadOnlyList<GameControllerRowViewModel> Rows);

public partial class GameControllerRowViewModel : ObservableObject
{
    private readonly Action<GameControllerAction> _startRecording;

    public GameControllerRowViewModel(
        GameControllerAction action,
        string title,
        Action<GameControllerAction> startRecording)
    {
        Action = action;
        Title = title;
        _startRecording = startRecording;
        AutomationId = $"GameController{action}RecordButton";
    }

    public GameControllerAction Action { get; }
    public string Title { get; }
    public string AutomationId { get; }

    [ObservableProperty]
    public partial string DisplayLabel { get; set; } = "";

    [ObservableProperty]
    public partial bool IsRecording { get; set; }

    [RelayCommand]
    private void Record() => _startRecording(Action);

    public void Update(string displayLabel, bool isRecording, string recordingLabel)
    {
        IsRecording = isRecording;
        DisplayLabel = isRecording ? recordingLabel : displayLabel;
    }
}
