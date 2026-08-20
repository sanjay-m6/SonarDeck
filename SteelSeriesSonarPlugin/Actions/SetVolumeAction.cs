using MacroDeck.Sdk.Actions;
using Microsoft.Extensions.Logging;

namespace SteelSeriesSonarPlugin.Actions;

/// <summary>
/// Sets the volume of a Sonar channel to an absolute percentage (0–100%).
/// </summary>
public sealed class SetVolumeAction : IActionDefinition
{
    private readonly SonarClient _sonar;
    private readonly ILogger _logger;

    public string Id => "set-volume";
    public string Name => "Set Volume";
    public string Description => "Set a Sonar channel's volume to an exact level (0–100%).";

    public IReadOnlyList<ActionParameter> Parameters { get; }

    public SetVolumeAction(SonarClient sonar, ILogger logger)
    {
        _sonar = sonar;
        _logger = logger;

        Parameters =
        [
            ActionParameter.Choice("channel", SonarChoices.Channels, "Channel", "Audio channel to set volume for", defaultValue: "game", required: true),
            ActionParameter.Choice("outputType", SonarChoices.OutputTypes, "Output Type", "Classic = single slider; Streaming / Monitoring = Streamer Mode", defaultValue: "Classic", required: true),
            ActionParameter.Number("volume", "Volume (%)", "Target volume level (0–100%)", min: 0, max: 100, step: 1, defaultValue: 50, required: true)
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
            var channel = context.Parameters.GetString("channel") ?? "game";
            var outputRaw = context.Parameters.GetString("outputType") ?? "Classic";
            var volumePercent = context.Parameters.GetInt32("volume") ?? 50;

            var output = outputRaw switch
            {
                "Streaming" => OutputType.Streaming,
                "Monitoring" => OutputType.Monitoring,
                _ => OutputType.None
            };

            var volume = Math.Clamp(volumePercent / 100.0, 0.0, 1.0);

            try
            {
                await _sonar.SetVolumeAsync(channel, volume, output, context.CancellationToken);
                return ActionResult.Success();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to set volume on channel {Channel}", channel);
                _sonar.ResetCache();
                return ActionResult.Failed("execution_failed", ex.Message);
            }
        }
    }
}
