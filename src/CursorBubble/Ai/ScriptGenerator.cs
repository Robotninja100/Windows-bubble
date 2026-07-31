using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace CursorBubble.Ai;

/// <summary>Result of an AI script generation.</summary>
public sealed record GeneratedScript(string Script, string Explanation, string Warnings);

/// <summary>
/// Generates a Windows PowerShell script from a natural-language description by
/// calling the Anthropic Messages API directly (no SDK dependency). The caller
/// always reviews the result before it is saved or run.
/// </summary>
public static class ScriptGenerator
{
    private const string Endpoint = "https://api.anthropic.com/v1/messages";
    private const string AnthropicVersion = "2023-06-01";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(120) };

    private const string SystemPrompt =
        "You write small, safe Windows PowerShell scripts from a user's plain-language request. " +
        "Target Windows 10/11 with the built-in PowerShell (Desktop 5.1). " +
        "Prefer simple, readable commands and add short comments. " +
        "Do NOT include anything destructive or irreversible unless the user clearly asked for it, " +
        "and never include commands that exfiltrate data or weaken security. " +
        "Respond with ONLY a JSON object (no markdown, no prose outside it) with exactly these keys: " +
        "\"script\" (the PowerShell script as a string), " +
        "\"explanation\" (one or two sentences, in the user's language, describing what it does), " +
        "\"warnings\" (a string with any risks or an empty string if none).";

    /// <summary>
    /// Generate a script for <paramref name="description"/>. Throws
    /// <see cref="InvalidOperationException"/> with a user-friendly message on failure.
    /// </summary>
    public static async Task<GeneratedScript> GenerateAsync(
        string apiKey, string model, string description, CancellationToken cancel = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("No API key has been set yet (Settings → AI).");
        if (string.IsNullOrWhiteSpace(description))
            throw new InvalidOperationException("Describe what the script should do first.");

        var payload = new
        {
            model,
            max_tokens = 4096,
            system = SystemPrompt,
            messages = new[]
            {
                new { role = "user", content = description }
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        request.Headers.Add("x-api-key", apiKey);
        request.Headers.Add("anthropic-version", AnthropicVersion);
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(request, cancel);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Could not reach the Claude API: " + ex.Message, ex);
        }

        string body = await response.Content.ReadAsStringAsync(cancel);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(DescribeApiError(response, body));

        return ParseResponse(body);
    }

    private static GeneratedScript ParseResponse(string body)
    {
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        if (root.TryGetProperty("stop_reason", out JsonElement stop) &&
            stop.GetString() == "refusal")
        {
            throw new InvalidOperationException(
                "The model declined this request. Rephrase it or adjust what you are asking for.");
        }

        // Find the first text content block (thinking blocks may precede it).
        string? text = null;
        if (root.TryGetProperty("content", out JsonElement content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement block in content.EnumerateArray())
            {
                if (block.TryGetProperty("type", out JsonElement t) && t.GetString() == "text" &&
                    block.TryGetProperty("text", out JsonElement txt))
                {
                    text = txt.GetString();
                    break;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("Unexpected response from the API (no text found).");

        return ParseGeneratedJson(text!);
    }

    /// <summary>Extract the JSON object the model returned, tolerating stray fences/prose.</summary>
    private static GeneratedScript ParseGeneratedJson(string text)
    {
        int start = text.IndexOf('{');
        int end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
            throw new InvalidOperationException("Could not read the generated script (no JSON found).");

        string json = text.Substring(start, end - start + 1);
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement r = doc.RootElement;
            string script = r.TryGetProperty("script", out var s) ? s.GetString() ?? "" : "";
            string explanation = r.TryGetProperty("explanation", out var e) ? e.GetString() ?? "" : "";
            string warnings = r.TryGetProperty("warnings", out var w) ? w.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(script))
                throw new InvalidOperationException("The model returned no script.");
            return new GeneratedScript(script.Trim(), explanation.Trim(), warnings.Trim());
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("Could not parse the generated script.");
        }
    }

    private static string DescribeApiError(HttpResponseMessage response, string body)
    {
        string message = body;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out JsonElement err) &&
                err.TryGetProperty("message", out JsonElement m))
                message = m.GetString() ?? body;
        }
        catch
        {
            // keep raw body
        }

        return (int)response.StatusCode switch
        {
            401 => "Invalid API key. Check the key under Settings → AI.",
            429 => "Too many requests, or out of credit. Try again later.",
            _ => $"API error ({(int)response.StatusCode}): {message}"
        };
    }
}
