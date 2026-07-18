using System;
using Niratan.Models.GameControllers;

namespace Niratan.Services.GameControllers;

public interface IGameControllerService
{
    bool IsConnected { get; }
    string? ConnectedControllerName { get; }
    GameControllerFamily ControllerFamily { get; }
    GameControllerAction? RecordingAction { get; }

    event EventHandler? StateChanged;
    event EventHandler? BindingsChanged;
    event EventHandler<GameControllerActionEventArgs>? ActionInvoked;

    void Start();
    void StartRecording(GameControllerAction action);
    void CancelRecording();
    void ResetDefaults();
    GameControllerBinding GetBinding(GameControllerAction action);
    string GetDisplayLabel(GameControllerBinding binding);
}
