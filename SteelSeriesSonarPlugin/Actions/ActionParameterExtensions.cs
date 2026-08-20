using System.Text.Json;

namespace SteelSeriesSonarPlugin.Actions;

public static class ActionParameterExtensions
{
    public static string? GetString(this IReadOnlyDictionary<string, object> parameters, string key)
    {
        if (parameters.TryGetValue(key, out var val) && val is not null)
        {
            if (val is string s) return s;
            if (val is JsonElement elem && elem.ValueKind == JsonValueKind.String)
            {
                return elem.GetString();
            }
            return val.ToString();
        }
        return null;
    }

    public static int? GetInt32(this IReadOnlyDictionary<string, object> parameters, string key)
    {
        if (parameters.TryGetValue(key, out var val) && val is not null)
        {
            if (val is int i) return i;
            if (val is long l) return (int)l;
            if (val is double d) return (int)d;
            if (val is JsonElement elem)
            {
                if (elem.ValueKind == JsonValueKind.Number && elem.TryGetInt32(out var jsonInt))
                    return jsonInt;
                if (elem.ValueKind == JsonValueKind.String && int.TryParse(elem.GetString(), out var parsedStr))
                    return parsedStr;
            }
            if (int.TryParse(val.ToString(), out var parsed))
            {
                return parsed;
            }
        }
        return null;
    }
}
