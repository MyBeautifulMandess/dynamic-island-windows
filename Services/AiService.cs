using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DynamicIsland.Models;

namespace DynamicIsland.Services;

public sealed class AiService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(120) };

    public async Task<IReadOnlyList<string>> GetModelsAsync(string baseUrl, string apiKey,
        CancellationToken token = default)
    {
        using var request = CreateRequest(HttpMethod.Get, baseUrl, "models", apiKey);
        using var response = await Http.SendAsync(request, token);
        await EnsureSuccess(response, token);
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(token),
            cancellationToken: token);
        var models = new List<string>();
        if (json.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
            {
                if (item.TryGetProperty("id", out var id) && !string.IsNullOrWhiteSpace(id.GetString()))
                    models.Add(id.GetString()!);
                else if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                    models.Add(item.GetString()!);
            }
        }
        else if (json.RootElement.TryGetProperty("models", out var legacy) && legacy.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in legacy.EnumerateArray())
            {
                if (item.TryGetProperty("name", out var name) && !string.IsNullOrWhiteSpace(name.GetString()))
                    models.Add(name.GetString()!.Replace("models/", "", StringComparison.OrdinalIgnoreCase));
            }
        }
        return models.Distinct(StringComparer.OrdinalIgnoreCase).Order().ToArray();
    }

    public async Task<string> SendAsync(string baseUrl, string apiKey, string model, AgentPreset agent,
        IEnumerable<ChatMessage> history, string prompt, CancellationToken token = default)
    {
        var messages = new List<object> { new { role = "system", content = agent.Prompt } };
        messages.AddRange(history.TakeLast(12).Select(x => (object)new { role = x.Role, content = x.Content }));
        messages.Add(new { role = "user", content = prompt });
        using var request = CreateRequest(HttpMethod.Post, baseUrl, "chat/completions", apiKey);
        request.Content = JsonContent.Create(new { model, messages, temperature = 0.4, max_tokens = 1600 });
        using var response = await Http.SendAsync(request, token);
        await EnsureSuccess(response, token);
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(token),
            cancellationToken: token);
        var content = json.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
        return string.IsNullOrWhiteSpace(content) ? "Модель вернула пустой ответ." : content.Trim();
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string baseUrl, string path, string apiKey)
    {
        var root = string.IsNullOrWhiteSpace(baseUrl) ? "https://routerai.ru/api/v1" : baseUrl.Trim().TrimEnd('/');
        var url = root.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? root + "/" + path : root + "/v1/" + path;
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private static async Task EnsureSuccess(HttpResponseMessage response, CancellationToken token)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(token);
        var message = body;
        try
        {
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.TryGetProperty("error", out var error))
                message = error.ValueKind == JsonValueKind.String
                    ? error.GetString() ?? body
                    : error.TryGetProperty("message", out var text) ? text.GetString() ?? body : error.GetRawText();
        }
        catch { }
        throw new InvalidOperationException($"API ({(int)response.StatusCode}): {message}");
    }
}
