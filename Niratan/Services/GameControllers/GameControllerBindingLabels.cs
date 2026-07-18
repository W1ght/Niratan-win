using Niratan.Models.GameControllers;

namespace Niratan.Services.GameControllers;

public static class GameControllerBindingLabels
{
    public static string For(GameControllerBinding binding, GameControllerFamily family) =>
        family switch
        {
            GameControllerFamily.Xbox => Xbox(binding.Input),
            GameControllerFamily.PlayStation => PlayStation(binding.Input),
            GameControllerFamily.Nintendo => Nintendo(binding.Input),
            _ => Generic(binding.Input),
        };

    public static GameControllerFamily DetectFamily(string? displayName, ushort vendorId = 0)
    {
        if (vendorId == 0x045e)
            return GameControllerFamily.Xbox;
        if (vendorId == 0x054c)
            return GameControllerFamily.PlayStation;
        if (vendorId == 0x057e)
            return GameControllerFamily.Nintendo;

        var name = displayName?.ToLowerInvariant() ?? "";
        if (name.Contains("xbox"))
            return GameControllerFamily.Xbox;
        if (name.Contains("playstation")
            || name.Contains("dualshock")
            || name.Contains("dualsense")
            || name.Contains("sony"))
        {
            return GameControllerFamily.PlayStation;
        }
        if (name.Contains("switch")
            || name.Contains("nintendo")
            || name.Contains("joy-con")
            || name.Contains("pro controller"))
        {
            return GameControllerFamily.Nintendo;
        }

        return GameControllerFamily.Generic;
    }

    private static string Xbox(string input) =>
        input switch
        {
            "buttonA" => "A",
            "buttonB" => "B",
            "buttonX" => "X",
            "buttonY" => "Y",
            "leftShoulder" => "LB",
            "rightShoulder" => "RB",
            "leftTrigger" => "LT",
            "rightTrigger" => "RT",
            "buttonOptions" => "View",
            "buttonMenu" => "Menu",
            "buttonHome" => "Xbox",
            "buttonShare" => "Share",
            "xboxPaddle1" => "Paddle 1",
            "xboxPaddle2" => "Paddle 2",
            "xboxPaddle3" => "Paddle 3",
            "xboxPaddle4" => "Paddle 4",
            _ => Generic(input),
        };

    private static string PlayStation(string input) =>
        input switch
        {
            "buttonA" => "Cross",
            "buttonB" => "Circle",
            "buttonX" => "Square",
            "buttonY" => "Triangle",
            "leftShoulder" => "L1",
            "rightShoulder" => "R1",
            "leftTrigger" => "L2",
            "rightTrigger" => "R2",
            "leftThumbstickButton" => "L3",
            "rightThumbstickButton" => "R3",
            "buttonOptions" => "Share/Create",
            "buttonMenu" => "Options",
            "buttonHome" => "PS",
            "buttonShare" => "Create",
            "playStationTouchpad" => "Touchpad",
            _ => Generic(input),
        };

    private static string Nintendo(string input) =>
        input switch
        {
            "buttonA" => "B",
            "buttonB" => "A",
            "buttonX" => "Y",
            "buttonY" => "X",
            "leftShoulder" => "L",
            "rightShoulder" => "R",
            "leftTrigger" => "ZL",
            "rightTrigger" => "ZR",
            "buttonOptions" => "-",
            "buttonMenu" => "+",
            "buttonHome" => "Home",
            "buttonShare" => "Capture",
            _ => Generic(input),
        };

    private static string Generic(string input) =>
        input switch
        {
            "buttonA" => "A / Cross / B",
            "buttonB" => "B / Circle / A",
            "buttonX" => "X / Square / Y",
            "buttonY" => "Y / Triangle / X",
            "dpadUp" => "D-Pad ↑",
            "dpadDown" => "D-Pad ↓",
            "dpadLeft" => "D-Pad ←",
            "dpadRight" => "D-Pad →",
            "leftShoulder" => "LB / L1 / L",
            "rightShoulder" => "RB / R1 / R",
            "leftTrigger" => "LT / L2 / ZL",
            "rightTrigger" => "RT / R2 / ZR",
            "leftThumbstickButton" => "L3",
            "rightThumbstickButton" => "R3",
            "buttonMenu" => "Menu / Options / +",
            "buttonOptions" => "View / Share / -",
            "buttonHome" => "Home / PS",
            "buttonShare" => "Share / Create / Capture",
            "playStationTouchpad" => "Touchpad",
            "xboxPaddle1" => "Paddle 1",
            "xboxPaddle2" => "Paddle 2",
            "xboxPaddle3" => "Paddle 3",
            "xboxPaddle4" => "Paddle 4",
            "leftThumbstickUp" => "Left Stick ↑",
            "leftThumbstickDown" => "Left Stick ↓",
            "leftThumbstickLeft" => "Left Stick ←",
            "leftThumbstickRight" => "Left Stick →",
            "rightThumbstickUp" => "Right Stick ↑",
            "rightThumbstickDown" => "Right Stick ↓",
            "rightThumbstickLeft" => "Right Stick ←",
            "rightThumbstickRight" => "Right Stick →",
            _ => input,
        };
}
