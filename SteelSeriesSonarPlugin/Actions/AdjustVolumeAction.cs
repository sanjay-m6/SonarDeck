using MacroDeck.Sdk.Actions;
using Microsoft.Extensions.Logging;

namespace SteelSeriesSonarPlugin.Actions;

/// <summary>
/// Nudges a Sonar channel's volume up or down by a configurable step.
/// Supports Classic mode and Streamer Mode (Streaming / Monitoring outputs).
/// </summary>
public sealed class AdjustVolumeAction : IActionDefinition
{
    private readonly SonarClient _sonar;
    private readonly ILogger _logger;

    public string Id => "adjust-volume";
    public string Name => "Adjust Volume";
    public string Description => "Increase or decrease a Sonar channel's volume by a step.";

    public IReadOnlyList<ActionParameter> Parameters { get; }

    public AdjustVolumeAction(SonarClient sonar, ILogger logger)
    {
        _sonar = sonar;
        _logger = logger;

        Parameters =
        [
            ActionParameter.Choice("channel", SonarChoices.Channels, "Channel", "Audio channel to adjust", defaultValue: "game", required: true),
            ActionParameter.Choice("outputType", SonarChoices.OutputTypes, "Output Type", "Classic = single slider; Streaming / Monitoring = Streamer Mode", defaultValue: "Classic", required: true),
            ActionParameter.Choice("direction", SonarChoices.Directions, "Direction", "Increase (+) or Decrease (-) volume", defaultValue: "Increase", required: true),
            ActionParameter.Number("step", "Step (%)", "Volume change per trigger (1–25%)", min: 1, max: 25, step: 1, defaultValue: 5, required: true)
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
            var direction = context.Parameters.GetString("direction") ?? "Increase";
            var stepPercent = context.Parameters.GetInt32("step") ?? 5;

            var output = outputRaw switch
            {
                "Streaming" => OutputType.Streaming,
                "Monitoring" => OutputType.Monitoring,
                _ => OutputType.None
            };

            var step = stepPercent / 100.0;
            if (direction == "Decrease")
            {
                step = -step;
            }

            try
            {
                var current = await _sonar.GetVolumeAsync(channel, output, context.CancellationToken);
                var next = Math.Clamp(current + step, 0.0, 1.0);
                await _sonar.SetVolumeAsync(channel, next, output, context.CancellationToken);
                return ActionResult.Success();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to adjust volume on channel {Channel}", channel);
                _sonar.ResetCache();
                return ActionResult.Failed("execution_failed", ex.Message);
            }
        }
    }
}
