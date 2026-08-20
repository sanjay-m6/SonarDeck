using MacroDeck.Sdk.Actions;
using Microsoft.Extensions.Logging;

namespace SteelSeriesSonarPlugin.Actions;

/// <summary>
/// Explicitly mutes or unmutes a Sonar channel.
/// </summary>
public sealed class MuteAction : IActionDefinition
{
    private readonly SonarClient _sonar;
    private readonly ILogger _logger;

    public string Id => "mute";
    public string Name => "Mute / Unmute";
    public string Description => "Set the mute state of a Sonar channel to On or Off.";

    public IReadOnlyList<ActionParameter> Parameters { get; }

    public MuteAction(SonarClient sonar, ILogger logger)
    {
        _sonar = sonar;
        _logger = logger;

        Parameters =
        [
            ActionParameter.Choice("channel", SonarChoices.Channels, "Channel", "Audio channel to mute/unmute", defaultValue: "master", required: true),
            ActionParameter.Choice("outputType", SonarChoices.OutputTypes, "Output Type", "Classic = single slider; Streaming / Monitoring = Streamer Mode", defaultValue: "Classic", required: true),
            ActionParameter.Choice("state", SonarChoices.MuteStates, "State", "Mute (Off) or Unmute (On)", defaultValue: "Mute", required: true)
        ];
    }

    public IActionExecutor CreateExecutor() => new Executor(_sonar, _logger);

    private sealed class Executor : IActionExecutor
    {
        private readonly SonarClient _sonar;
        private readonly ILogger _logger;

        public Executor(SonarClient sonar, ILogger logger)
        {
            _sonar = sonar;
            _logger = logger;
        }

        public async Task<ActionResult> ExecuteAsync(ActionExecutionContext context)
        {
            var channel = context.Parameters.GetString("channel") ?? "master";
            var outputRaw = context.Parameters.GetString("outputType") ?? "Classic";
            var state = context.Parameters.GetString("state") ?? "Mute";

            var output = outputRaw switch
            {
                "Streaming" => OutputType.Streaming,
                "Monitoring" => OutputType.Monitoring,
                _ => OutputType.None
            };

            var mute = state == "Mute";

            try
            {
                await _sonar.SetMuteAsync(channel, mute, output, context.CancellationToken);
                return ActionResult.Success();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to set mute state on channel {Channel}", channel);
                _sonar.ResetCache();
                return ActionResult.Failed("execution_failed", ex.Message);
            }
        }
    }
}
