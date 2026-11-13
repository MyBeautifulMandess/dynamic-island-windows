using System.Text.Json;
using System.Text.Json.Nodes;

namespace DynamicIsland.Services;

/// <summary>Конверт протокола DI Link. Один формат на все сообщения.</summary>
public sealed record PhoneEnvelope(string Type, JsonObject Payload, string Id)
{
    public const int Version = 1;

    public T? Get<T>(string name) => Payload[name] is { } node ? node.Deserialize<T>() : default;

    public string GetString(string name) => Payload[name]?.GetValue<string>() ?? "";

    public string GetString(string name, string fallback) =>
        Payload[name]?.GetValue<string>() is { Length: > 0 } value ? value : fallback;

    public long GetLong(string name, long fallback = 0)
    {
        try { return Payload[name]?.GetValue<long>() ?? fallback; }
        catch { return fallback; }
    }

    public bool GetBool(string name, bool fallback = false)
    {
        try { return Payload[name]?.GetValue<bool>() ?? fallback; }
        catch { return fallback; }
    }
}

public static class PhoneProtocol
{
    /// <summary>
    /// Имена полей в протоколе — camelCase, разбор — без учёта регистра. Без этого
    /// вложенные записи планера приходили с пустыми полями.
    /// </summary>
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public static string Build(string type, object? payload = null, string? id = null)
    {
        var node = payload is null ? new JsonObject() : JsonSerializer.SerializeToNode(payload, Json) as JsonObject ?? new JsonObject();
        var envelope = new JsonObject
        {
            ["v"] = PhoneEnvelope.Version,
            ["id"] = id ?? Guid.NewGuid().ToString("N"),
            ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["type"] = type,
            ["payload"] = node
        };
        return envelope.ToJsonString(Json);
    }

    public static PhoneEnvelope? Parse(string text)
    {
        try
        {
            var root = JsonNode.Parse(text) as JsonObject;
            if (root?["type"]?.GetValue<string>() is not { Length: > 0 } type) return null;
            var payload = root["payload"] as JsonObject ?? new JsonObject();
            return new PhoneEnvelope(type, payload, root["id"]?.GetValue<string>() ?? "");
        }
        catch { return null; }
    }

    /// <summary>Ограничивает размер входящего текста, чтобы телефон не выел память.</summary>
    public static string Clamp(string? value, int limit) =>
        string.IsNullOrEmpty(value) ? "" : value.Length <= limit ? value : value[..limit];
}
