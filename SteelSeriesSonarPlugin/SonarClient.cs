using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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

    public static string Normalize(string? channel)
    {
        if (string.IsNullOrWhiteSpace(channel))
            return Master;

        var lower = channel.Trim().ToLowerInvariant();
        return lower switch
        {
            "master" => Master,
            "game" => Game,
            "chat" or "chatrender" or "chat_render" or "chat (output)" or "chat(output)" => ChatRender,
            "mic" or "microphone" or "chatcapture" or "chat_capture" or "chat (input)" or "chat(input)" => ChatCapture,
            "media" => Media,
            "aux" => Aux,
            _ => channel
        };
    }

    public static string[] GetAliases(string? channel)
    {
        var norm = Normalize(channel);
        if (string.Equals(norm, ChatRender, StringComparison.OrdinalIgnoreCase))
            return [ChatRender, "chat", "Chat", "chatRender", "ChatRender"];
        if (string.Equals(norm, ChatCapture, StringComparison.OrdinalIgnoreCase))
            return [ChatCapture, "chatCapture", "ChatCapture", "microphone", "Microphone", "mic", "Mic"];
        if (string.Equals(norm, Master, StringComparison.OrdinalIgnoreCase))
            return [Master, "master", "Master"];
        if (string.Equals(norm, Game, StringComparison.OrdinalIgnoreCase))
            return [Game, "game", "Game"];
        if (string.Equals(norm, Media, StringComparison.OrdinalIgnoreCase))
            return [Media, "media", "Media"];
        if (string.Equals(norm, Aux, StringComparison.OrdinalIgnoreCase))
            return [Aux, "aux", "Aux"];
        return [norm, channel ?? ""];
    }
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
    private string? _cachedClassicJson;
    private DateTimeOffset _cachedClassicExpires = DateTimeOffset.MinValue;
    private readonly SemaphoreSlim _classicLock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public SonarClient(HttpClient http)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
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
        {
            throw new InvalidOperationException(
                $"SteelSeries GG does not appear to be installed or has never been launched. " +
                $"Expected coreProps.json at: {CorePropsPath}");
        }

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
        {
            throw new InvalidOperationException(
                "SteelSeries GG is running but Sonar is not available. " +
                "Please ensure the Sonar feature is enabled in GG.");
        }

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
    public void ResetCache()
    {
        _sonarBase = null;
        _cachedClassicJson = null;
        _cachedClassicExpires = DateTimeOffset.MinValue;
    }

    private async Task<string> GetVolumeSettingsJsonAsync(OutputType output, CancellationToken ct)
    {
        var sonarBase = await GetSonarBaseAsync(ct);
        if (output is OutputType.Streaming or OutputType.Monitoring)
        {
            var url = $"{sonarBase}/volumeSettings/streamer";
            return await _http.GetStringAsync(url, ct);
        }

        var now = DateTimeOffset.UtcNow;
        if (_cachedClassicJson is not null && now < _cachedClassicExpires)
            return _cachedClassicJson;

        await _classicLock.WaitAsync(ct);
        try
        {
            now = DateTimeOffset.UtcNow;
            if (_cachedClassicJson is not null && now < _cachedClassicExpires)
                return _cachedClassicJson;

            var url = $"{sonarBase}/volumeSettings/classic";
            _cachedClassicJson = await _http.GetStringAsync(url, ct);
            _cachedClassicExpires = DateTimeOffset.UtcNow.AddMilliseconds(200);
            return _cachedClassicJson;
        }
        finally
        {
            _classicLock.Release();
        }
    }

    // ── Volume ────────────────────────────────────────────────────────────────

    /// <summary>Returns the current volume (0.0–1.0) for the given channel.</summary>
    public async Task<double> GetVolumeAsync(
        string channel, OutputType output = OutputType.None, CancellationToken ct = default)
    {
        channel = SonarChannel.Normalize(channel);
        var json = await GetVolumeSettingsJsonAsync(output, ct);

        var streamType = output == OutputType.Streaming ? "streaming" : "monitoring";
        return ExtractChannelNumber(json, channel, streamType, "volume", "Volume")
            ?? throw LogAndBuildParseError(json, channel, "volume");
    }

    /// <summary>Sets the volume (0.0–1.0) for the given channel.</summary>
    public async Task SetVolumeAsync(
        string channel, double volume,
        OutputType output = OutputType.None, CancellationToken ct = default)
    {
        _cachedClassicExpires = DateTimeOffset.MinValue;
        channel = SonarChannel.Normalize(channel);
        volume = Math.Clamp(volume, 0.0, 1.0);
        var volStr = volume.ToString("0.####", CultureInfo.InvariantCulture);
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

        await PutEmptyAsync(url, ct);
    }

    // ── Mute ──────────────────────────────────────────────────────────────────

    /// <summary>Returns whether the given channel is muted.</summary>
    public async Task<bool> GetMuteAsync(
        string channel, OutputType output = OutputType.None, CancellationToken ct = default)
    {
        channel = SonarChannel.Normalize(channel);
        var json = await GetVolumeSettingsJsonAsync(output, ct);

        var streamType = output == OutputType.Streaming ? "streaming" : "monitoring";
        return ExtractChannelBool(json, channel, streamType, "muted", "Mute", "isMuted")
            ?? throw LogAndBuildParseError(json, channel, "mute");
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

        await PutEmptyAsync(url, ct);
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
        var chatMixStr = chatMix.ToString("0.##", CultureInfo.InvariantCulture);

        try
        {
            var sonarBase = await GetSonarBaseAsync(ct);
            var url = $"{sonarBase}/chatMix?balance={chatMixStr}";
            await PutEmptyAsync(url, ct);
        }
        catch
        {
            // Fallback for Sonar versions that require proportional Game & Chat volumes
            var gameVol = chatMix >= 0 ? 1.0 : (1.0 + chatMix);
            var chatVol = chatMix <= 0 ? 1.0 : (1.0 - chatMix);

            await SetVolumeAsync(SonarChannel.Game, gameVol, OutputType.None, ct);
            await SetVolumeAsync(SonarChannel.ChatRender, chatVol, OutputType.None, ct);
        }
    }

    /// <summary>Returns an approximate chat-mix value (−1.0 to +1.0).</summary>
    public async Task<double> GetChatMixAsync(CancellationToken ct = default)
    {
        try
        {
            var sonarBase = await GetSonarBaseAsync(ct);
            var url = $"{sonarBase}/chatMix";
            var json = await _http.GetStringAsync(url, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Number)
                return root.GetDouble();

            foreach (var name in new[] { "chatMix", "balance", "value" })
            {
                if (root.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Number)
                    return prop.GetDouble();
            }
        }
        catch
        {
            // Fallback: estimate from Game vs Chat volume
        }

        var gameVol = await GetVolumeAsync(SonarChannel.Game, OutputType.None, ct);
        var chatVol = await GetVolumeAsync(SonarChannel.ChatRender, OutputType.None, ct);

        if (gameVol < 0.99)
            return gameVol - 1.0;
        if (chatVol < 0.99)
            return 1.0 - chatVol;
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
        await PutEmptyAsync(url, ct);
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

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string BuildVolumeSettingsUrl(string sonarBase, OutputType output) =>
        output is OutputType.Streaming or OutputType.Monitoring
            ? $"{sonarBase}/volumeSettings/streamer"
            : $"{sonarBase}/volumeSettings/classic";

    private async Task PutEmptyAsync(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Put, url);
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
    }

    private static double? ExtractChannelNumber(string json, string channel, string streamType, params string[] propertyNames)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var aliases = SonarChannel.GetAliases(channel);

        foreach (var chName in aliases)
        {
            // Try direct hierarchical lookup first
            if (chName.Equals(SonarChannel.Master, StringComparison.OrdinalIgnoreCase))
            {
                if (root.TryGetProperty("masters", out var masters))
                {
                    if (masters.TryGetProperty("classic", out var classic) && classic.TryGetProperty("volume", out var v))
                        return v.GetDouble();
                    if (masters.TryGetProperty("stream", out var stream) &&
                        stream.TryGetProperty(streamType, out var st) &&
                        st.TryGetProperty("volume", out var sv))
                        return sv.GetDouble();
                }
            }
            else
            {
                if (root.TryGetProperty("devices", out var devices) && devices.TryGetProperty(chName, out var ch))
                {
                    if (ch.TryGetProperty("classic", out var classic) && classic.TryGetProperty("volume", out var v))
                        return v.GetDouble();
                    if (ch.TryGetProperty("stream", out var stream) &&
                        stream.TryGetProperty(streamType, out var st) &&
                        st.TryGetProperty("volume", out var sv))
                        return sv.GetDouble();
                }
            }

            // Fallback: search tree
            if (TryFindChannelObject(root, chName, out var channelElement))
            {
                foreach (var name in propertyNames)
                {
                    if (channelElement.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Number)
                        return prop.GetDouble();
                }
            }
        }

        return null;
    }

    private static bool? ExtractChannelBool(string json, string channel, string streamType, params string[] propertyNames)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var aliases = SonarChannel.GetAliases(channel);

        foreach (var chName in aliases)
        {
            // Try direct hierarchical lookup first
            if (chName.Equals(SonarChannel.Master, StringComparison.OrdinalIgnoreCase))
            {
                if (root.TryGetProperty("masters", out var masters))
                {
                    if (masters.TryGetProperty("classic", out var classic) && classic.TryGetProperty("muted", out var m))
                        return m.GetBoolean();
                    if (masters.TryGetProperty("stream", out var stream) &&
                        stream.TryGetProperty(streamType, out var st) &&
                        st.TryGetProperty("muted", out var sm))
                        return sm.GetBoolean();
                }
            }
            else
            {
                if (root.TryGetProperty("devices", out var devices) && devices.TryGetProperty(chName, out var ch))
                {
                    if (ch.TryGetProperty("classic", out var classic) && classic.TryGetProperty("muted", out var m))
                        return m.GetBoolean();
                    if (ch.TryGetProperty("stream", out var stream) &&
                        stream.TryGetProperty(streamType, out var st) &&
                        st.TryGetProperty("muted", out var sm))
                        return sm.GetBoolean();
                }
            }

            // Fallback: search tree
            if (TryFindChannelObject(root, chName, out var channelElement))
            {
                foreach (var name in propertyNames)
                {
                    if (channelElement.TryGetProperty(name, out var prop) &&
                        (prop.ValueKind == JsonValueKind.True || prop.ValueKind == JsonValueKind.False))
                        return prop.GetBoolean();
                }
            }
        }

        return null;
    }

    private static bool TryFindChannelObject(JsonElement element, string channel, out JsonElement found)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in element.EnumerateObject())
            {
                if (string.Equals(prop.Name, channel, StringComparison.OrdinalIgnoreCase) &&
                    prop.Value.ValueKind == JsonValueKind.Object)
                {
                    found = prop.Value;
                    return true;
                }
                if (TryFindChannelObject(prop.Value, channel, out found))
                    return true;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (TryFindChannelObject(item, channel, out found))
                    return true;
            }
        }

        found = default;
        return false;
    }

    private static InvalidOperationException LogAndBuildParseError(string json, string channel, string field)
    {
        return new InvalidOperationException(
            $"Could not parse '{field}' for channel '{channel}' from Sonar response.");
    }
}
