using MacroDeck.Sdk.Actions;
using Microsoft.Extensions.Logging;

namespace SteelSeriesSonarPlugin.Actions;

/// <summary>
/// Sets the Sonar Chat Mix balance slider. -100 = full chat audio, 0 = balanced,
/// +100 = full game audio.
/// </summary>
public sealed class SetChatMixAction : IActionDefinition, ISliderActionDefinition
{
    private readonly SonarClient _sonar;
    private readonly ILogger _logger;

    public string Id => "set-chat-mix";
    public string Name => "Set Chat Mix";
    public string Description => "Set the Sonar Chat Mix balance (-100 full chat, 0 balanced, +100 full game). Also works as a slider tile.";

    public IReadOnlyList<ActionParameter> Parameters { get; } =
    [
        ActionParameter.Slider(
            name: "chatMix",
            min: -100,
            max: 100,
            label: "Chat Mix",
            description: "-100 = full chat, 0 = balanced, +100 = full game.",
            step: 1,
            defaultValue: 0),
    ];

    public string SliderValueParameter => "chatMix";
    public bool CommitOnRelease => true;

    public SetChatMixAction(SonarClient sonar, ILogger logger)
    {
        _sonar = sonar;
        _logger = logger;
    }

    public async Task<SliderActionState?> GetSliderStateAsync(
        IReadOnlyDictionary<string, object?> parameters,
        CancellationToken cancellationToken)
    {
        try
        {
            var current = await _sonar.GetChatMixAsync(cancellationToken);
            return new SliderActionState(Min: -100, Max: 100, Step: 1, Value: Math.Round(current * 100.0));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read current chat mix for slider state");
            var defaultMix = SonarActionParameters.ReadDouble(parameters, "chatMix", 0.0);
            return new SliderActionState(Min: -100, Max: 100, Step: 1, Value: defaultMix);
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
            var chatMixValue = SonarActionParameters.ReadDouble(context.Parameters, "chatMix", 0.0);
            var chatMix = Math.Clamp(chatMixValue / 100.0, -1.0, 1.0);

            _logger.LogInformation("SetChatMix: value={Value}", chatMix);

            try
            {
                await _sonar.SetChatMixAsync(chatMix, context.CancellationToken);
                return ActionResult.Success();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to set chat mix");
                _sonar.ResetCache();
                return ActionResult.Failed("execution_failed", ex.Message);
            }
        }
    }
}
