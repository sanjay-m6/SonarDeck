using MacroDeck.Sdk;
using MacroDeck.Sdk.Actions;
using MacroDeck.Sdk.Issues;
using MacroDeck.Sdk.Variables;
using Microsoft.Extensions.Logging;
using SteelSeriesSonarPlugin.Actions;

namespace SteelSeriesSonarPlugin;

/// <summary>
/// Lifecycle integration for the SteelSeries GG Sonar plugin.
/// Registers actions, surfaces connection issues, and provides real-time variables to Macro Deck.
/// </summary>
public sealed class SonarIntegration : IPluginIntegration, IIntegrationIssueProvider, IVariableProvider
{
    private const string IssueIdSonarUnavailable = "sonar-unavailable";

    private readonly SonarClient _sonar;
    private readonly ILogger<SonarIntegration> _logger;

    public IReadOnlyList<IActionDefinition> Actions { get; }

    public IReadOnlyList<ProvidedVariable> ProvidedVariables => VariablesList;
    public IReadOnlyList<ProvidedVariable> DeclaredVariables => VariablesList;
    public bool VariablesDependOnConfiguration => false;

    private static readonly TimeSpan FastRefresh = TimeSpan.FromMilliseconds(200);

    private static readonly IReadOnlyList<ProvidedVariable> VariablesList =
    [
        new("sonar_master_volume", VariableType.Numeric, 0, FastRefresh),
        new("sonar_game_volume", VariableType.Numeric, 0, FastRefresh),
        new("sonar_chat_volume", VariableType.Numeric, 0, FastRefresh),
        new("sonar_media_volume", VariableType.Numeric, 0, FastRefresh),
        new("sonar_aux_volume", VariableType.Numeric, 0, FastRefresh),
        new("sonar_mic_volume", VariableType.Numeric, 0, FastRefresh),

        new("sonar_master_muted", VariableType.Boolean, null, FastRefresh),
        new("sonar_game_muted", VariableType.Boolean, null, FastRefresh),
        new("sonar_chat_muted", VariableType.Boolean, null, FastRefresh),
        new("sonar_media_muted", VariableType.Boolean, null, FastRefresh),
        new("sonar_aux_muted", VariableType.Boolean, null, FastRefresh),
        new("sonar_mic_muted", VariableType.Boolean, null, FastRefresh),

        new("sonar_chatmix", VariableType.Numeric, 0, FastRefresh),
    ];

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

    /// <inheritdoc />
    public async Task<object?> GetValueAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            return name.ToLowerInvariant() switch
            {
                "sonar_master_volume" => Math.Round((await _sonar.GetVolumeAsync("master", OutputType.None, cancellationToken)) * 100.0),
                "sonar_game_volume" => Math.Round((await _sonar.GetVolumeAsync("game", OutputType.None, cancellationToken)) * 100.0),
                "sonar_chat_volume" => Math.Round((await _sonar.GetVolumeAsync("chat", OutputType.None, cancellationToken)) * 100.0),
                "sonar_media_volume" => Math.Round((await _sonar.GetVolumeAsync("media", OutputType.None, cancellationToken)) * 100.0),
                "sonar_aux_volume" => Math.Round((await _sonar.GetVolumeAsync("aux", OutputType.None, cancellationToken)) * 100.0),
                "sonar_mic_volume" => Math.Round((await _sonar.GetVolumeAsync("mic", OutputType.None, cancellationToken)) * 100.0),

                "sonar_master_muted" => await _sonar.GetMuteAsync("master", OutputType.None, cancellationToken),
                "sonar_game_muted" => await _sonar.GetMuteAsync("game", OutputType.None, cancellationToken),
                "sonar_chat_muted" => await _sonar.GetMuteAsync("chat", OutputType.None, cancellationToken),
                "sonar_media_muted" => await _sonar.GetMuteAsync("media", OutputType.None, cancellationToken),
                "sonar_aux_muted" => await _sonar.GetMuteAsync("aux", OutputType.None, cancellationToken),
                "sonar_mic_muted" => await _sonar.GetMuteAsync("mic", OutputType.None, cancellationToken),

                "sonar_chatmix" => Math.Round((await _sonar.GetChatMixAsync(cancellationToken)) * 100.0),

                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<IntegrationIssue>> GetIssuesAsync(CancellationToken cancellationToken)
    {
        var available = await _sonar.IsAvailableAsync(cancellationToken);
        if (available)
        {
            return [];
        }

        return
        [
            new IntegrationIssue
            {
                Id = IssueIdSonarUnavailable,
                Title = "SteelSeries GG Sonar is not running",
                Description = "Start SteelSeries GG and ensure Sonar is enabled in settings.",
                Severity = IntegrationIssueSeverity.Warning,
            }
        ];
    }

    /// <inheritdoc />
    public Task<IssueResolution> ResolveIssueAsync(string issueId, CancellationToken cancellationToken) =>
        Task.FromResult(IssueResolution.Failed("This issue resolves automatically once SteelSeries GG Sonar is reachable."));
}
