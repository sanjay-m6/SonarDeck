using MacroDeck.Sdk.Actions;
using Microsoft.Extensions.Logging;

namespace SteelSeriesSonarPlugin.Actions;

/// <summary>
/// Enables, disables, or toggles SteelSeries GG Sonar Streamer Mode.
/// </summary>
public sealed class SetStreamerModeAction : IActionDefinition
{
    private readonly SonarClient _sonar;
    private readonly ILogger _logger;

    public string Id => "set-streamer-mode";
    public string Name => "Set Streamer Mode";
    public string Description => "Enable, disable, or toggle Sonar Streamer Mode (dual Streaming/Monitoring sliders).";

    public IReadOnlyList<ActionParameter> Parameters { get; }

    public SetStreamerModeAction(SonarClient sonar, ILogger logger)
    {
        _sonar = sonar;
        _logger = logger;

        Parameters =
        [
            ActionParameter.Choice("action", SonarChoices.StreamerModeActions, "Action", "Enable / Disable Streamer Mode, or Toggle state", defaultValue: "Toggle", required: true)
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
            var action = context.Parameters.GetString("action") ?? "Toggle";
            _logger.LogInformation("SetStreamerMode: action={Action}", action);

            try
            {
                bool newState = action switch
                {
                    "Enable" => await EnableAsync(context.CancellationToken),
                    "Disable" => await DisableAsync(context.CancellationToken),
                    _ => await _sonar.ToggleStreamerModeAsync(context.CancellationToken)
                };

                _logger.LogInformation("Streamer Mode is now {State}.", newState ? "enabled" : "disabled");
                return ActionResult.Success();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to change Streamer Mode");
                _sonar.ResetCache();
                return ActionResult.Failed("execution_failed", ex.Message);
            }
        }

        private async Task<bool> EnableAsync(CancellationToken ct)
        {
            await _sonar.SetStreamerModeAsync(true, ct);
            return true;
        }

        private async Task<bool> DisableAsync(CancellationToken ct)
        {
            await _sonar.SetStreamerModeAsync(false, ct);
            return false;
        }
    }
}
