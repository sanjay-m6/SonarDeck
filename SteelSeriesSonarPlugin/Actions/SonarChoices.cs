using MacroDeck.Sdk.Actions;

namespace SteelSeriesSonarPlugin.Actions;

public static class SonarChoices
{
    public static readonly ActionParameterOption[] Channels =
    [
        new() { Value = "master", Label = "Master" },
        new() { Value = "game", Label = "Game" },
        new() { Value = "chatRender", Label = "Chat (Output)" },
        new() { Value = "chatCapture", Label = "Chat (Input)" },
        new() { Value = "media", Label = "Media" },
        new() { Value = "aux", Label = "Aux" },
        new() { Value = "microphone", Label = "Microphone" }
    ];

    public static readonly ActionParameterOption[] OutputTypes =
    [
        new() { Value = "Classic", Label = "Classic" },
        new() { Value = "Streaming", Label = "Streaming" },
        new() { Value = "Monitoring", Label = "Monitoring" }
    ];

    public static readonly ActionParameterOption[] Directions =
    [
        new() { Value = "Increase", Label = "Increase (+)" },
        new() { Value = "Decrease", Label = "Decrease (-)" }
    ];

    public static readonly ActionParameterOption[] MuteStates =
    [
        new() { Value = "Mute", Label = "Mute (Off)" },
        new() { Value = "Unmute", Label = "Unmute (On)" }
    ];

    public static readonly ActionParameterOption[] StreamerModeActions =
    [
        new() { Value = "Toggle", Label = "Toggle (Flip)" },
        new() { Value = "Enable", Label = "Enable" },
        new() { Value = "Disable", Label = "Disable" }
    ];
}
