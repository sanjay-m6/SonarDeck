using MacroDeck.Sdk.Actions;
using Microsoft.Extensions.Logging;

namespace SteelSeriesSonarPlugin.Actions;

/// <summary>
/// Sets the Sonar Chat Mix balance slider.
/// −100 = full chat audio, 0 = balanced, +100 = full game audio.
/// </summary>
public sealed class SetChatMixAction : IActionDefinition
{
    private readonly SonarClient _sonar;
    private readonly ILogger _logger;

    public string Id => "set-chat-mix";
    public string Name => "Set Chat Mix";
    public string Description => "Set the Sonar Chat Mix balance (−100 full chat → 0 balanced → +100 full game).";

    public IReadOnlyList<ActionParameter> Parameters { get; }

    public SetChatMixAction(SonarClient sonar, ILogger logger)
    {
        _sonar = sonar;
        _logger = logger;

        Parameters =
        [
            ActionParameter.Slider("chatMix", -100, 100, "Chat Mix Balance (-100 to +100)", "−100 = full chat, 0 = balanced, +100 = full game", defaultValue: 0, step: 1)
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
            var chatMixValue = context.Parameters.GetInt32("chatMix") ?? 0;
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
