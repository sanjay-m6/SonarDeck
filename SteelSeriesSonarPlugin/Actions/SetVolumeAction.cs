using MacroDeck.Sdk.Actions;
using Microsoft.Extensions.Logging;

namespace SteelSeriesSonarPlugin.Actions;

/// <summary>
/// Sets a Sonar channel's volume to an exact percentage (0-100). Supports Classic mode
/// (single slider) and Streamer Mode (Streaming / Monitoring outputs).
/// </summary>
public sealed class SetVolumeAction : IActionDefinition, ISliderActionDefinition
{
    private readonly SonarClient _sonar;
    private readonly ILogger _logger;

    public string Id => "set-volume";
    public string Name => "Set Volume";
    public string Description => "Set a Sonar channel's volume to an exact level (0-100 %). Also works as a slider tile.";

    public IReadOnlyList<ActionParameter> Parameters { get; } =
    [
        SonarActionParameters.Channel(defaultValue: SonarChannel.Game),
        SonarActionParameters.OutputTypeParameter(),
        ActionParameter.Slider(
            name: "volume",
            min: 0,
            max: 100,
            label: "Volume (%)",
            description: "Target volume from 0 to 100.",
            step: 1,
            defaultValue: 50),
    ];

    public string SliderValueParameter => "volume";
    public bool CommitOnRelease => true;

    public SetVolumeAction(SonarClient sonar, ILogger logger)
    {
        _sonar = sonar;
        _logger = logger;
    }

    public async Task<SliderActionState?> GetSliderStateAsync(
        IReadOnlyDictionary<string, object?> parameters,
        CancellationToken cancellationToken)
    {
        var channel = SonarActionParameters.ReadChannel(parameters, fallback: SonarChannel.Game);
        var output = SonarActionParameters.ReadOutputType(parameters);
        try
        {
            var current = await _sonar.GetVolumeAsync(channel, output, cancellationToken);
            return new SliderActionState(Min: 0, Max: 100, Step: 1, Value: Math.Round(current * 100.0));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read current volume for slider state on channel {Channel}", channel);
            var defaultVol = SonarActionParameters.ReadDouble(parameters, "volume", 50.0);
            return new SliderActionState(Min: 0, Max: 100, Step: 1, Value: defaultVol);
        }
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
            var channel = SonarActionParameters.ReadChannel(context.Parameters, fallback: SonarChannel.Game);
            var output = SonarActionParameters.ReadOutputType(context.Parameters);
            var volumePercent = SonarActionParameters.ReadDouble(context.Parameters, "volume", 50.0);
            var volume = Math.Clamp(volumePercent / 100.0, 0.0, 1.0);

            _logger.LogInformation("SetVolume: channel={Channel} output={Output} volume={Volume}", channel, output, volume);

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
