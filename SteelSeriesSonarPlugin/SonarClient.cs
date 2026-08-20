using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SteelSeriesSonarPlugin;

// ── Sonar channel identifiers ─────────────────────────────────────────────────

/// <summary>Audio channels exposed by SteelSeries GG Sonar.</summary>
public static class SonarChannel
{
    public const string Master      = "master";
    public const string Game        = "game";
    public const string ChatRender  = "chatRender";
    public const string ChatCapture = "chatCapture";
    public const string Media       = "media";
    public const string Aux         = "aux";
    public const string Microphone  = "microphone";

    public static readonly string[] All =
    [
        Master, Game, ChatRender, ChatCapture, Media, Aux, Microphone
    ];

    public static string Normalize(string channel) =>
        channel.Equals("microphone", StringComparison.OrdinalIgnoreCase)
            ? ChatCapture
            : channel;
}

/// <summary>
/// In streamer mode each channel has two independent sliders.
/// Use <c>None</c> (default) when streamer mode is off / classic.
/// </summary>
public enum OutputType
{
    /// <summary>Classic (single-slider) mode.</summary>
    None,
    /// <summary>The mix sent to the streaming encoder (OBS, etc.).</summary>
    Streaming,
    /// <summary>What the streamer themselves hears in their headphones.</summary>
    Monitoring,
}

// ── Internal JSON models ───────────────────────────────────────────────────────

internal sealed record CoreProps(
    [property: JsonPropertyName("ggEncryptedAddress")] string? GgEncryptedAddress,
    [property: JsonPropertyName("encryptedAddress")] string? EncryptedAddress,
    [property: JsonPropertyName("address")] string? Address
);

internal sealed record SubAppsResponse(
    [property: JsonPropertyName("subApps")] Dictionary<string, SubApp> SubApps
);

internal sealed record SubApp(
    [property: JsonPropertyName("metadata")] SubAppMetadata Metadata
);

internal sealed record SubAppMetadata(
    [property: JsonPropertyName("webServerAddress")] string? WebServerAddress,
    [property: JsonPropertyName("encryptedWebServerAddress")] string? EncryptedWebServerAddress
);

// ── SonarClient ───────────────────────────────────────────────────────────────

/// <summary>
/// Thin async wrapper around the SteelSeries GG Sonar local HTTP REST API.
/// Sonar runs as an internal ASP.NET Core service with dynamic ports discovered
/// from <c>coreProps.json</c> and GG's encrypted <c>/subApps</c> endpoint.
/// </summary>
public sealed class SonarClient
{
    private static readonly string CorePropsPath =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SteelSeries", "GG", "coreProps.json");

    private readonly HttpClient _http;
    private string? _sonarBase; // e.g. "http://127.0.0.1:54241"

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public SonarClient()
        : this(CreateDefaultHttpClient())
    {
    }

    public SonarClient(HttpClient http)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    private static HttpClient CreateDefaultHttpClient()
    {
        var handler = new HttpClientHandler
        {
            // SteelSeries GG uses self-signed certificates for localhost IPC
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
    }

    // ── Address discovery ─────────────────────────────────────────────────────

    /// <summary>
    /// Reads <c>coreProps.json</c> and asks the GG base address for the Sonar
    /// sub-app web-server URL. Caches the result; call <see cref="ResetCache"/>
    /// to force re-discovery after a GG restart.
    /// </summary>
    public async Task<string> GetSonarBaseAsync(CancellationToken ct = default)
    {
        if (_sonarBase is not null)
            return _sonarBase;

        if (!File.Exists(CorePropsPath))
            throw new InvalidOperationException(
                $"SteelSeries GG does not appear to be installed or has never been launched. " +
                $"Expected coreProps.json at: {CorePropsPath}");

        var raw = await File.ReadAllTextAsync(CorePropsPath, ct);
        var coreProps = JsonSerializer.Deserialize<CoreProps>(raw, JsonOpts)
            ?? throw new InvalidOperationException("Failed to parse coreProps.json.");

        var ggAddress = coreProps.GgEncryptedAddress ?? coreProps.EncryptedAddress;
        if (string.IsNullOrWhiteSpace(ggAddress))
            throw new InvalidOperationException("No valid GG address found in coreProps.json.");

        if (!ggAddress.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !ggAddress.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            ggAddress = "https://" + ggAddress;
        }

        var subAppsUrl = $"{ggAddress.TrimEnd('/')}/subApps";
        var subAppsJson = await _http.GetStringAsync(subAppsUrl, ct);
        var subApps = JsonSerializer.Deserialize<SubAppsResponse>(subAppsJson, JsonOpts)
            ?? throw new InvalidOperationException("Failed to parse /subApps response from GG.");

        if (!subApps.SubApps.TryGetValue("sonar", out var sonar))
            throw new InvalidOperationException(
                "SteelSeries GG is running but Sonar is not available. " +
                "Please ensure the Sonar feature is enabled in GG.");

        var sonarAddress = sonar.Metadata.WebServerAddress;
        if (string.IsNullOrWhiteSpace(sonarAddress))
            sonarAddress = sonar.Metadata.EncryptedWebServerAddress;

        if (string.IsNullOrWhiteSpace(sonarAddress))
            throw new InvalidOperationException("Sonar webServerAddress was not reported by GG.");

        if (!sonarAddress.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !sonarAddress.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            sonarAddress = "http://" + sonarAddress;
        }

        _sonarBase = sonarAddress.TrimEnd('/');
        return _sonarBase;
    }

    /// <summary>Clears the cached Sonar address so it is re-discovered next call.</summary>
    public void ResetCache() => _sonarBase = null;

    // ── Volume ────────────────────────────────────────────────────────────────

    /// <summary>Returns the current volume (0.0–1.0) for the given channel.</summary>
    public async Task<double> GetVolumeAsync(
        string channel, OutputType output = OutputType.None, CancellationToken ct = default)
    {
        channel = SonarChannel.Normalize(channel);
        var sonarBase = await GetSonarBaseAsync(ct);

        if (output is OutputType.Streaming or OutputType.Monitoring)
        {
            var url = $"{sonarBase}/volumeSettings/streamer";
            var json = await _http.GetStringAsync(url, ct);
            using var doc = JsonDocument.Parse(json);
            var streamType = output == OutputType.Streaming ? "streaming" : "monitoring";

            if (channel.Equals(SonarChannel.Master, StringComparison.OrdinalIgnoreCase))
            {
                return doc.RootElement
                    .GetProperty("masters")
                    .GetProperty("stream")
                    .GetProperty(streamType)
                    .GetProperty("volume")
                    .GetDouble();
            }

            return doc.RootElement
                .GetProperty("devices")
                .GetProperty(channel)
                .GetProperty("stream")
                .GetProperty(streamType)
                .GetProperty("volume")
                .GetDouble();
        }
        else
        {
            var url = $"{sonarBase}/volumeSettings/classic";
            var json = await _http.GetStringAsync(url, ct);
            using var doc = JsonDocument.Parse(json);

            if (channel.Equals(SonarChannel.Master, StringComparison.OrdinalIgnoreCase))
            {
                return doc.RootElement
                    .GetProperty("masters")
                    .GetProperty("classic")
                    .GetProperty("volume")
                    .GetDouble();
            }

            return doc.RootElement
                .GetProperty("devices")
                .GetProperty(channel)
                .GetProperty("classic")
                .GetProperty("volume")
                .GetDouble();
        }
    }

    /// <summary>Sets the volume (0.0–1.0) for the given channel.</summary>
    public async Task SetVolumeAsync(
        string channel, double volume,
        OutputType output = OutputType.None, CancellationToken ct = default)
    {
        channel = SonarChannel.Normalize(channel);
        volume = Math.Clamp(volume, 0.0, 1.0);
        var volStr = volume.ToString("0.##", CultureInfo.InvariantCulture);
        var sonarBase = await GetSonarBaseAsync(ct);

        string url;
        if (output is OutputType.Streaming or OutputType.Monitoring)
        {
            var streamType = output == OutputType.Streaming ? "streaming" : "monitoring";
            url = channel.Equals(SonarChannel.Master, StringComparison.OrdinalIgnoreCase)
                ? $"{sonarBase}/volumeSettings/streamer/{streamType}/master/volume/{volStr}"
                : $"{sonarBase}/volumeSettings/streamer/{streamType}/{channel}/volume/{volStr}";
        }
        else
        {
            url = channel.Equals(SonarChannel.Master, StringComparison.OrdinalIgnoreCase)
                ? $"{sonarBase}/volumeSettings/classic/master/Volume/{volStr}"
                : $"{sonarBase}/volumeSettings/classic/{channel}/Volume/{volStr}";
        }

        using var req = new HttpRequestMessage(HttpMethod.Put, url);
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
    }

    // ── Mute ──────────────────────────────────────────────────────────────────

    /// <summary>Returns whether the given channel is muted.</summary>
    public async Task<bool> GetMuteAsync(
        string channel, OutputType output = OutputType.None, CancellationToken ct = default)
    {
        channel = SonarChannel.Normalize(channel);
        var sonarBase = await GetSonarBaseAsync(ct);

        if (output is OutputType.Streaming or OutputType.Monitoring)
        {
            var url = $"{sonarBase}/volumeSettings/streamer";
            var json = await _http.GetStringAsync(url, ct);
            using var doc = JsonDocument.Parse(json);
            var streamType = output == OutputType.Streaming ? "streaming" : "monitoring";

            if (channel.Equals(SonarChannel.Master, StringComparison.OrdinalIgnoreCase))
            {
                return doc.RootElement
                    .GetProperty("masters")
                    .GetProperty("stream")
                    .GetProperty(streamType)
                    .GetProperty("muted")
                    .GetBoolean();
            }

            return doc.RootElement
                .GetProperty("devices")
                .GetProperty(channel)
                .GetProperty("stream")
                .GetProperty(streamType)
                .GetProperty("muted")
                .GetBoolean();
        }
        else
        {
            var url = $"{sonarBase}/volumeSettings/classic";
            var json = await _http.GetStringAsync(url, ct);
            using var doc = JsonDocument.Parse(json);

            if (channel.Equals(SonarChannel.Master, StringComparison.OrdinalIgnoreCase))
            {
                return doc.RootElement
                    .GetProperty("masters")
                    .GetProperty("classic")
                    .GetProperty("muted")
                    .GetBoolean();
            }

            return doc.RootElement
                .GetProperty("devices")
                .GetProperty(channel)
                .GetProperty("classic")
                .GetProperty("muted")
                .GetBoolean();
        }
    }

    /// <summary>Sets the mute state for the given channel.</summary>
    public async Task SetMuteAsync(
        string channel, bool mute,
        OutputType output = OutputType.None, CancellationToken ct = default)
    {
        channel = SonarChannel.Normalize(channel);
        var muteStr = mute ? "true" : "false";
        var sonarBase = await GetSonarBaseAsync(ct);

        string url;
        if (output is OutputType.Streaming or OutputType.Monitoring)
        {
            var streamType = output == OutputType.Streaming ? "streaming" : "monitoring";
            url = channel.Equals(SonarChannel.Master, StringComparison.OrdinalIgnoreCase)
                ? $"{sonarBase}/volumeSettings/streamer/{streamType}/master/isMuted/{muteStr}"
                : $"{sonarBase}/volumeSettings/streamer/{streamType}/{channel}/isMuted/{muteStr}";
        }
        else
        {
            url = channel.Equals(SonarChannel.Master, StringComparison.OrdinalIgnoreCase)
                ? $"{sonarBase}/volumeSettings/classic/master/Mute/{muteStr}"
                : $"{sonarBase}/volumeSettings/classic/{channel}/Mute/{muteStr}";
        }

        using var req = new HttpRequestMessage(HttpMethod.Put, url);
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>Toggles the mute state for the given channel and returns the new state.</summary>
    public async Task<bool> ToggleMuteAsync(
        string channel, OutputType output = OutputType.None, CancellationToken ct = default)
    {
        var current = await GetMuteAsync(channel, output, ct);
        await SetMuteAsync(channel, !current, output, ct);
        return !current;
    }

    // ── Chat Mix ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Sets the chat-mix balance between Game and Chat channels.
    /// Value range: −1.0 (full chat) to +1.0 (full game).
    /// </summary>
    public async Task SetChatMixAsync(double chatMix, CancellationToken ct = default)
    {
        chatMix = Math.Clamp(chatMix, -1.0, 1.0);
        // Balance game vs chat volume proportionally
        var gameVol = chatMix >= 0 ? 1.0 : (1.0 + chatMix);
        var chatVol = chatMix <= 0 ? 1.0 : (1.0 - chatMix);

        await SetVolumeAsync(SonarChannel.Game, gameVol, OutputType.None, ct);
        await SetVolumeAsync(SonarChannel.ChatRender, chatVol, OutputType.None, ct);
    }

    /// <summary>Returns an approximate chat-mix value based on Game and Chat levels.</summary>
    public async Task<double> GetChatMixAsync(CancellationToken ct = default)
    {
        var gameVol = await GetVolumeAsync(SonarChannel.Game, OutputType.None, ct);
        var chatVol = await GetVolumeAsync(SonarChannel.ChatRender, OutputType.None, ct);

        if (gameVol < 0.99)
            return gameVol - 1.0; // Negative (more chat)
        if (chatVol < 0.99)
            return 1.0 - chatVol; // Positive (more game)
        return 0.0;
    }

    // ── Streamer Mode ─────────────────────────────────────────────────────────

    /// <summary>Returns whether Streamer Mode is currently active.</summary>
    public async Task<bool> GetStreamerModeAsync(CancellationToken ct = default)
    {
        var sonarBase = await GetSonarBaseAsync(ct);
        var url = $"{sonarBase}/mode";
        var raw = await _http.GetStringAsync(url, ct);
        return raw.Trim('"', ' ', '\r', '\n').Equals("stream", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Enables or disables Streamer Mode.
    /// When enabled, each channel supports independent Streaming and Monitoring levels.
    /// </summary>
    public async Task SetStreamerModeAsync(bool enabled, CancellationToken ct = default)
    {
        var sonarBase = await GetSonarBaseAsync(ct);
        var modeName = enabled ? "stream" : "classic";
        var url = $"{sonarBase}/mode/{modeName}";
        using var req = new HttpRequestMessage(HttpMethod.Put, url);
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>Toggles Streamer Mode and returns the new state.</summary>
    public async Task<bool> ToggleStreamerModeAsync(CancellationToken ct = default)
    {
        var current = await GetStreamerModeAsync(ct);
        await SetStreamerModeAsync(!current, ct);
        return !current;
    }

    // ── Connectivity test ─────────────────────────────────────────────────────

    /// <summary>
    /// Returns <c>true</c> if Sonar is reachable right now.
    /// On failure it resets the address cache so the next call re-discovers it.
    /// </summary>
    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            var sonarBase = await GetSonarBaseAsync(ct);
            var url = $"{sonarBase}/volumeSettings/classic";
            using var resp = await _http.GetAsync(url, ct);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            ResetCache();
            return false;
        }
    }
}
