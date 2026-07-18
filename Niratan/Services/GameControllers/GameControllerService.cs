using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Dispatching;
using Niratan.Models.GameControllers;
using Niratan.Services.Settings;
using Windows.Gaming.Input;

namespace Niratan.Services.GameControllers;

internal sealed class GameControllerService : IGameControllerService
{
    private const double AxisPressThreshold = 0.6;
    private const double AxisReleaseThreshold = 0.4;
    private static readonly TimeSpan DebounceInterval = TimeSpan.FromMilliseconds(180);

    private readonly ISettingsService _settingsService;
    private readonly Dictionary<Gamepad, HashSet<string>> _activeInputsByGamepad =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, DateTimeOffset> _lastPressTimes = [];
    private DispatcherQueue? _dispatcherQueue;
    private DispatcherQueueTimer? _pollTimer;
    private IReadOnlyList<Gamepad> _gamepads = [];
    private bool _isStarted;

    public GameControllerService(ISettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    public bool IsConnected => _gamepads.Count > 0;
    public string? ConnectedControllerName { get; private set; }
    public GameControllerFamily ControllerFamily { get; private set; } =
        GameControllerFamily.Generic;
    public GameControllerAction? RecordingAction { get; private set; }

    public event EventHandler? StateChanged;
    public event EventHandler? BindingsChanged;
    public event EventHandler<GameControllerActionEventArgs>? ActionInvoked;

    public void Start()
    {
        if (_isStarted)
        {
            RefreshConnectedController();
            return;
        }

        _dispatcherQueue = DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("Game controller service must start on the UI thread.");
        _isStarted = true;
        Gamepad.GamepadAdded += OnGamepadChanged;
        Gamepad.GamepadRemoved += OnGamepadChanged;

        _pollTimer = _dispatcherQueue.CreateTimer();
        _pollTimer.Interval = TimeSpan.FromMilliseconds(20);
        _pollTimer.IsRepeating = true;
        _pollTimer.Tick += PollTimer_Tick;
        _pollTimer.Start();
        RefreshConnectedController();
    }

    public void StartRecording(GameControllerAction action)
    {
        RecordingAction = action;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void CancelRecording()
    {
        if (RecordingAction == null)
            return;

        RecordingAction = null;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ResetDefaults()
    {
        SaveConfiguration(GameControllerConfiguration.Defaults());
    }

    public GameControllerBinding GetBinding(GameControllerAction action) =>
        CurrentConfiguration.BindingFor(action);

    public string GetDisplayLabel(GameControllerBinding binding) =>
        GameControllerBindingLabels.For(binding, ControllerFamily);

    private GameControllerConfiguration CurrentConfiguration =>
        _settingsService.Current.GameControllerConfiguration
        ?? new GameControllerConfiguration();

    private void OnGamepadChanged(object? sender, Gamepad gamepad)
    {
        _dispatcherQueue?.TryEnqueue(RefreshConnectedController);
    }

    private void RefreshConnectedController()
    {
        var previousGamepads = _gamepads;
        var previousName = ConnectedControllerName;
        var previousFamily = ControllerFamily;
        _gamepads = Gamepad.Gamepads.ToList();
        var primaryGamepad = _gamepads.FirstOrDefault();

        ConnectedControllerName = null;
        ControllerFamily = GameControllerFamily.Generic;
        if (primaryGamepad != null)
        {
            try
            {
                var rawController = RawGameController.FromGameController(primaryGamepad);
                ConnectedControllerName = rawController?.DisplayName;
                ControllerFamily = GameControllerBindingLabels.DetectFamily(
                    ConnectedControllerName,
                    rawController?.HardwareVendorId ?? 0);
            }
            catch
            {
                // Some third-party drivers do not expose a RawGameController identity.
            }
        }

        foreach (var removedGamepad in _activeInputsByGamepad.Keys
            .Where(gamepad => !_gamepads.Contains(gamepad, ReferenceEqualityComparer.Instance))
            .ToList())
        {
            _activeInputsByGamepad.Remove(removedGamepad);
        }

        if (!previousGamepads.SequenceEqual(_gamepads, ReferenceEqualityComparer.Instance)
            || !string.Equals(previousName, ConnectedControllerName, StringComparison.Ordinal)
            || previousFamily != ControllerFamily)
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
            BindingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void PollTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        if (_gamepads.Count == 0)
            return;

        foreach (var gamepad in _gamepads)
        {
            GamepadReading reading;
            try
            {
                reading = gamepad.GetCurrentReading();
            }
            catch
            {
                RefreshConnectedController();
                return;
            }

            if (!_activeInputsByGamepad.TryGetValue(gamepad, out var activeInputs))
            {
                activeInputs = [];
                _activeInputsByGamepad[gamepad] = activeInputs;
            }

            var pressedInputs = ReadPressedInputs(reading, activeInputs);
            foreach (var input in pressedInputs.Where(input => !activeInputs.Contains(input)))
                HandlePressedInput(input);

            activeInputs.Clear();
            activeInputs.UnionWith(pressedInputs);
        }
    }

    private HashSet<string> ReadPressedInputs(
        GamepadReading reading,
        IReadOnlySet<string> activeInputs)
    {
        // Windows.Gaming.Input.Gamepad exposes the shared cross-platform controls,
        // but not vendor-only Home, Share/Capture, or PlayStation touchpad inputs.
        HashSet<string> inputs = [];
        AddButton(inputs, reading.Buttons, GamepadButtons.A, "buttonA");
        AddButton(inputs, reading.Buttons, GamepadButtons.B, "buttonB");
        AddButton(inputs, reading.Buttons, GamepadButtons.X, "buttonX");
        AddButton(inputs, reading.Buttons, GamepadButtons.Y, "buttonY");
        AddButton(inputs, reading.Buttons, GamepadButtons.LeftShoulder, "leftShoulder");
        AddButton(inputs, reading.Buttons, GamepadButtons.RightShoulder, "rightShoulder");
        AddAnalog(inputs, activeInputs, reading.LeftTrigger, "leftTrigger");
        AddAnalog(inputs, activeInputs, reading.RightTrigger, "rightTrigger");
        AddButton(inputs, reading.Buttons, GamepadButtons.Menu, "buttonMenu");
        AddButton(inputs, reading.Buttons, GamepadButtons.View, "buttonOptions");
        AddButton(inputs, reading.Buttons, GamepadButtons.LeftThumbstick, "leftThumbstickButton");
        AddButton(inputs, reading.Buttons, GamepadButtons.RightThumbstick, "rightThumbstickButton");
        AddButton(inputs, reading.Buttons, GamepadButtons.Paddle1, "xboxPaddle1");
        AddButton(inputs, reading.Buttons, GamepadButtons.Paddle2, "xboxPaddle2");
        AddButton(inputs, reading.Buttons, GamepadButtons.Paddle3, "xboxPaddle3");
        AddButton(inputs, reading.Buttons, GamepadButtons.Paddle4, "xboxPaddle4");
        AddButton(inputs, reading.Buttons, GamepadButtons.DPadUp, "dpadUp");
        AddButton(inputs, reading.Buttons, GamepadButtons.DPadDown, "dpadDown");
        AddButton(inputs, reading.Buttons, GamepadButtons.DPadLeft, "dpadLeft");
        AddButton(inputs, reading.Buttons, GamepadButtons.DPadRight, "dpadRight");
        AddAxis(inputs, activeInputs, reading.LeftThumbstickY, true, "leftThumbstickUp");
        AddAxis(inputs, activeInputs, reading.LeftThumbstickY, false, "leftThumbstickDown");
        AddAxis(inputs, activeInputs, reading.LeftThumbstickX, false, "leftThumbstickLeft");
        AddAxis(inputs, activeInputs, reading.LeftThumbstickX, true, "leftThumbstickRight");
        AddAxis(inputs, activeInputs, reading.RightThumbstickY, true, "rightThumbstickUp");
        AddAxis(inputs, activeInputs, reading.RightThumbstickY, false, "rightThumbstickDown");
        AddAxis(inputs, activeInputs, reading.RightThumbstickX, false, "rightThumbstickLeft");
        AddAxis(inputs, activeInputs, reading.RightThumbstickX, true, "rightThumbstickRight");
        return inputs;
    }

    private static void AddButton(
        ISet<string> inputs,
        GamepadButtons buttons,
        GamepadButtons button,
        string input)
    {
        if (buttons.HasFlag(button))
            inputs.Add(input);
    }

    private static void AddAnalog(
        ISet<string> inputs,
        IReadOnlySet<string> activeInputs,
        double value,
        string input)
    {
        var threshold = activeInputs.Contains(input)
            ? AxisReleaseThreshold
            : AxisPressThreshold;
        if (value >= threshold)
            inputs.Add(input);
    }

    private static void AddAxis(
        ISet<string> inputs,
        IReadOnlySet<string> activeInputs,
        double value,
        bool positive,
        string input)
    {
        var threshold = activeInputs.Contains(input)
            ? AxisReleaseThreshold
            : AxisPressThreshold;
        if (positive ? value >= threshold : value <= -threshold)
            inputs.Add(input);
    }

    private void HandlePressedInput(string input)
    {
        var now = DateTimeOffset.UtcNow;
        if (_lastPressTimes.TryGetValue(input, out var lastPress)
            && now - lastPress < DebounceInterval)
        {
            return;
        }
        _lastPressTimes[input] = now;

        if (RecordingAction is { } recordingAction)
        {
            var next = CurrentConfiguration.Clone();
            next.SetBinding(recordingAction, new GameControllerBinding(input));
            RecordingAction = null;
            SaveConfiguration(next);
            StateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        var resolved = GameControllerActions.All.FirstOrDefault(
            definition => string.Equals(
                GetBinding(definition.Action).Input,
                input,
                StringComparison.Ordinal));
        if (resolved != null)
            ActionInvoked?.Invoke(this, new GameControllerActionEventArgs(resolved.Action));
    }

    private void SaveConfiguration(GameControllerConfiguration configuration)
    {
        _settingsService.Set(settings => settings.GameControllerConfiguration, configuration);
        BindingsChanged?.Invoke(this, EventArgs.Empty);
        _ = _settingsService.SaveAsync();
    }
}
