using MacroDeck.Sdk.Actions;
using Microsoft.Extensions.Logging;

namespace SteelSeriesSonarPlugin.Actions;

/// <summary>
/// Toggles the mute state of a Sonar channel.
/// </summary>
public sealed class ToggleMuteAction : IActionDefinition
{
    private readonly SonarClient _sonar;
    private readonly ILogger _logger;

    public string Id => "toggle-mute";
    public string Name => "Toggle Mute";
    public string Description => "Flip the mute state of a Sonar channel.";

    public IReadOnlyList<ActionParameter> Parameters { get; }

    public ToggleMuteAction(SonarClient sonar, ILogger logger)
    {
        _sonar = sonar;
        _logger = logger;

        Parameters =
        [
            ActionParameter.Choice("channel", SonarChoices.Channels, "Channel", "Audio channel to toggle mute for", defaultValue: "master", required: true),
            ActionParameter.Choice("outputType", SonarChoices.OutputTypes, "Output Type", "Classic = single slider; Streaming / Monitoring = Streamer Mode", defaultValue: "Classic", required: true)
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

            var output = outputRaw switch
            {
                "Streaming" => OutputType.Streaming,
                "Monitoring" => OutputType.Monitoring,
                _ => OutputType.None
            };

            try
            {
                var isMuted = await _sonar.ToggleMuteAsync(channel, output, context.CancellationToken);
                _logger.LogInformation("ToggleMute: channel={Channel} output={Output} isMuted={IsMuted}",
                    channel, output, isMuted);
                return ActionResult.Success();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to toggle mute on channel {Channel}", channel);
                _sonar.ResetCache();
                return ActionResult.Failed("execution_failed", ex.Message);
            }
        }
    }
}
