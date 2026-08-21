using System.Collections.Concurrent;
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

    // ── Authoritative variable declarations ──────────────────────────────────

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

    // ── Authoritative channel ↔ variable mapping (single source of truth) ────

    /// <summary>Volume variable name → Sonar API channel identifier.</summary>
    private static readonly Dictionary<string, string> VolumeVarToChannel = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sonar_master_volume"] = "master",
        ["sonar_game_volume"]   = "game",
        ["sonar_chat_volume"]   = "chatRender",
        ["sonar_media_volume"]  = "media",
        ["sonar_aux_volume"]    = "aux",
        ["sonar_mic_volume"]    = "chatCapture",
    };

    /// <summary>Mute variable name → Sonar API channel identifier.</summary>
    private static readonly Dictionary<string, string> MuteVarToChannel = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sonar_master_muted"] = "master",
        ["sonar_game_muted"]   = "game",
        ["sonar_chat_muted"]   = "chatRender",
        ["sonar_media_muted"]  = "media",
        ["sonar_aux_muted"]    = "aux",
        ["sonar_mic_muted"]    = "chatCapture",
    };

    // ── Last-known-good value cache ──────────────────────────────────────────
    // Prevents transient API failures from resetting the UI to 0/null.

    private readonly ConcurrentDictionary<string, object?> _lastKnownValues = new(StringComparer.OrdinalIgnoreCase);

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

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task InitializeAsync(IIntegrationContext context)
    {
        _logger.LogInformation("[Sonar] Integration initializing…");

        var available = await _sonar.IsAvailableAsync();
        if (available)
        {
            _logger.LogInformation("[Sonar] Sonar is reachable — reading initial state.");
            await RefreshAllValuesAsync(CancellationToken.None);
        }
        else
        {
            _logger.LogWarning(
                "[Sonar] Sonar is not currently reachable. " +
                "Variables will populate once SteelSeries GG and Sonar are launched.");
        }
    }

    /// <inheritdoc />
    public Task ShutdownAsync()
    {
        _logger.LogInformation("[Sonar] Integration shutting down.");
        return Task.CompletedTask;
    }

    // ── Variable provider ─────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<object?> GetValueAsync(string name, CancellationToken cancellationToken)
    {
        var lowerName = name.ToLowerInvariant();

        try
        {
            object? freshValue = null;

            if (VolumeVarToChannel.TryGetValue(lowerName, out var volChannel))
            {
                var raw = await _sonar.GetVolumeAsync(volChannel, OutputType.None, cancellationToken);
                freshValue = ClampVolume(raw);
                _logger.LogDebug("[Sonar] {Channel} volume read: {Value}", volChannel, freshValue);
            }
            else if (MuteVarToChannel.TryGetValue(lowerName, out var muteChannel))
            {
                freshValue = await _sonar.GetMuteAsync(muteChannel, OutputType.None, cancellationToken);
                _logger.LogDebug("[Sonar] {Channel} muted read: {Value}", muteChannel, freshValue);
            }
            else if (lowerName == "sonar_chatmix")
            {
                var raw = await _sonar.GetChatMixAsync(cancellationToken);
                freshValue = ClampChatMix(raw);
                _logger.LogDebug("[Sonar] ChatMix read: {Value}", freshValue);
            }

            if (freshValue is not null)
            {
                _lastKnownValues[lowerName] = freshValue;
                return freshValue;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Sonar] Failed to read variable '{Name}' — returning last-known value", lowerName);

            // On failure after a successful connection loss, reset cache so
            // re-discovery happens on the next attempt.
            _sonar.ResetCache();
        }

        // Return last-known-good value (never silently returns null/0 for a stale reason)
        if (_lastKnownValues.TryGetValue(lowerName, out var cached))
            return cached;

        // No cached value yet — return null to indicate "not yet available"
        return null;
    }

    // ── Batch refresh (used during init & reconnect) ──────────────────────────

    private async Task RefreshAllValuesAsync(CancellationToken ct)
    {
        try
        {
            // Use the batch method to fetch all channels from one cached API call
            var states = await _sonar.GetAllActiveStatesAsync(ct);

            foreach (var (varName, apiChannel) in VolumeVarToChannel)
            {
                if (states.TryGetValue(apiChannel, out var state))
                {
                    var vol = ClampVolume(state.Volume);
                    _lastKnownValues[varName] = vol;
                    _logger.LogInformation("[Sonar] Init: {Channel} volume = {Value}", apiChannel, vol);
                }
                else
                {
                    _logger.LogWarning("[Sonar] Init: Channel '{Channel}' not found in Sonar response", apiChannel);
                }
            }

            foreach (var (varName, apiChannel) in MuteVarToChannel)
            {
                if (states.TryGetValue(apiChannel, out var state))
                {
                    _lastKnownValues[varName] = state.Muted;
                    _logger.LogInformation("[Sonar] Init: {Channel} muted = {Value}", apiChannel, state.Muted);
                }
            }

            // ChatMix is a separate endpoint
            try
            {
                var chatMix = await _sonar.GetChatMixAsync(ct);
                var clamped = ClampChatMix(chatMix);
                _lastKnownValues["sonar_chatmix"] = clamped;
                _logger.LogInformation("[Sonar] Init: ChatMix = {Value}", clamped);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Sonar] Init: Failed to read ChatMix");
            }

            _logger.LogInformation("[Sonar] Initial state loaded successfully.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Sonar] Failed to load initial state from Sonar.");
        }
    }

    // ── Safe value converters ─────────────────────────────────────────────────

    /// <summary>
    /// Converts a raw Sonar volume (0.0–1.0) to a clamped percentage (0–100).
    /// Safely handles NaN, Infinity, and out-of-range values.
    /// </summary>
    private static double ClampVolume(double rawApiValue)
    {
        var percent = Math.Round(rawApiValue * 100.0);
        if (double.IsNaN(percent) || double.IsInfinity(percent))
            return 0;
        return Math.Clamp(percent, 0, 100);
    }

    /// <summary>
    /// Converts a raw Sonar chat-mix value (−1.0 to +1.0) to a clamped percentage (−100 to +100).
    /// </summary>
    private static double ClampChatMix(double rawApiValue)
    {
        var percent = Math.Round(rawApiValue * 100.0);
        if (double.IsNaN(percent) || double.IsInfinity(percent))
            return 0;
        return Math.Clamp(percent, -100, 100);
    }

    // ── Issue provider ────────────────────────────────────────────────────────

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
