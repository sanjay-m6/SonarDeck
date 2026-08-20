using MacroDeck.Sdk;
using MacroDeck.Sdk.Actions;
using Microsoft.Extensions.Logging;
using SteelSeriesSonarPlugin.Actions;

namespace SteelSeriesSonarPlugin;

/// <summary>
/// Lifecycle integration for the SteelSeries GG Sonar plugin.
/// Registers actions and manages Sonar connection state.
/// </summary>
public sealed class SonarIntegration : IPluginIntegration
{
    private readonly SonarClient _sonar;
    private readonly ILogger<SonarIntegration> _logger;

    public IReadOnlyList<IActionDefinition> Actions { get; }

    public SonarIntegration(SonarClient sonar, ILogger<SonarIntegration> logger)
    {
        _sonar = sonar;
        _logger = logger;

        Actions =
        [
            new SetVolumeAction(_sonar, _logger),
            new AdjustVolumeAction(_sonar, _logger),
            new MuteAction(_sonar, _logger),
            new ToggleMuteAction(_sonar, _logger),
            new SetChatMixAction(_sonar, _logger),
            new SetStreamerModeAction(_sonar, _logger)
        ];
    }

    /// <inheritdoc />
    public async Task InitializeAsync(IIntegrationContext context)
    {
        _logger.LogInformation("SteelSeries Sonar integration initializing…");

        var available = await _sonar.IsAvailableAsync();
        if (available)
        {
            _logger.LogInformation("SteelSeries GG Sonar is reachable.");
        }
        else
        {
            _logger.LogWarning(
                "SteelSeries GG Sonar is not currently reachable. " +
                "Actions will connect dynamically once SteelSeries GG and Sonar are launched.");
        }
    }

    /// <inheritdoc />
    public Task ShutdownAsync()
    {
        _logger.LogInformation("SteelSeries Sonar integration shutting down.");
        return Task.CompletedTask;
    }
}
