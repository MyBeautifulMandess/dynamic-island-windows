using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DynamicIsland.Models;

namespace DynamicIsland.Services;

public sealed class AiService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(120) };
    private const string BaseUrl = "https://routerai.ru/api/v1/";

    public async Task<IReadOnlyList<string>> GetModelsAsync(string apiKey, CancellationToken token = default)
    {
        using var request = CreateRequest(HttpMethod.Get, "models", apiKey);
        using var response = await Http.SendAsync(request, token);
        await EnsureSuccess(response, token);
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(token), cancellationToken: token);
        return json.RootElement.GetProperty("data").EnumerateArray()
            .Select(x => x.TryGetProperty("id", out var id) ? id.GetString() : null)
            .Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>().Order().ToArray();
    }

    public async Task<string> SendAsync(string apiKey, string model, AgentPreset agent,
        IEnumerable<ChatMessage> history, string prompt, CancellationToken token = default)
    {
        var messages = new List<object> { new { role = "system", content = agent.Prompt } };
        messages.AddRange(history.TakeLast(12).Select(x => (object)new { role = x.Role, content = x.Content }));
        messages.Add(new { role = "user", content = prompt });
        using var request = CreateRequest(HttpMethod.Post, "chat/completions", apiKey);
        request.Content = JsonContent.Create(new { model, messages, temperature = 0.4, max_tokens = 1600 });
        using var response = await Http.SendAsync(request, token);
        await EnsureSuccess(response, token);
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(token), cancellationToken: token);
        var content = json.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
        return string.IsNullOrWhiteSpace(content) ? "Модель вернула пустой ответ." : content.Trim();
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string path, string apiKey)
    {
        var request = new HttpRequestMessage(method, BaseUrl + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private static async Task EnsureSuccess(HttpResponseMessage response, CancellationToken token)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(token);
        string message = body;
        try
        {
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.TryGetProperty("error", out var error))
                message = error.TryGetProperty("message", out var text) ? text.GetString() ?? body : error.GetRawText();
        }
        catch { }
        throw new InvalidOperationException($"RouterAI ({(int)response.StatusCode}): {message}");
    }
}
