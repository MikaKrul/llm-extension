// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.ClientModel;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using Microsoft.SemanticKernel.Connectors.AzureOpenAI;
using Microsoft.SemanticKernel.Connectors.Google;
using Microsoft.SemanticKernel.Connectors.MistralAI;
using Microsoft.SemanticKernel.Connectors.Ollama;
using OllamaSharp;

namespace LlmExtension;

/// <summary>
///     Supported services. Values are serialized to the config file by number,
///     so new services must be appended at the end.
/// </summary>
internal enum Service
{
    Ollama,
    OpenAI,
    AzureOpenAI,
    Google,
    Mistral,
    Groq,
}

/// <summary>
///     User and assistant chat conversation message.
/// </summary>
internal class ChatMessage
{
    public required string User { get; set; }
    public required string Assistant { get; set; }

    /// <summary>
    ///     Performance summary of the assistant response,
    ///     e.g. "42 tok/s · 128 tokens · 3.4 s".
    /// </summary>
    public string? Stats { get; set; }

    /// <summary>
    ///     Label of the tool(s) used to build the response,
    ///     e.g. "Exa web search (keyless)", or null when none were used.
    /// </summary>
    public string? Tools { get; set; }
}

/// <summary>
///     Result of a web search: the formatted context for the LLM plus a label
///     describing how the search was performed (for the "tools used" display).
/// </summary>
internal sealed record WebSearchResult(string Context, string Source);

/// <summary>
///     User configurations.
/// </summary>
internal class Config
{
    public Service Service { get; set; } = Service.Ollama;
    public string Url { get; set; } = "";
    public string? Model { get; set; }
    public bool Details { get; set; } = true;
    public string? ApiKey { get; set; }
    public string System { get; set; } =
        "You are an AI assistant, you should provide help to the user with their query. " +
        "Act as if you are human with emotions, use fun and relaxing tone when appropriate. " +
        "However, remember you are still an AI model. Respond in Markdown format if necessary.";

    public int History { get; set; } = 6;
    public bool Debug { get; set; }
    public int MaxTokens { get; set; } = 4096;
    public float Temperature { get; set; } = 0.8f;
    public float TopP { get; set; } = 1.0f;
    public string? ExaApiKey { get; set; }
    public bool WebSearch { get; set; }
}

/// <summary>
///     Client containing the configuration and the service.
/// </summary>
internal class Client
{
    public required Config Config;
    private IChatCompletionService? ChatService { get; set; }
    public IEnumerable<string> MissingConfigs { get; private set; } = new List<string>();

    private static readonly HttpClient ExaHttpClient = new() { Timeout = TimeSpan.FromSeconds(20) };

    public void ReinitializeService()
    {
        MissingConfigs = MissingConfigsForService();

        if (MissingConfigs.Any())
        {
            return;
        }

        ChatService = CreateService(Config);
    }

    async public IAsyncEnumerable<string> Chat(IEnumerable<ChatMessage> messages, string? webContext = null)
    {
        if (ChatService == null) yield break;

        // Build history
        ChatHistory history = [];
        history.AddSystemMessage(Config.System);
        var relevantMessages = messages.Take(Config.History).Reverse().ToList();
        foreach (var (message, isLast) in relevantMessages.Select((m, i) => (m, i == relevantMessages.Count - 1)))
        {
            if (!string.IsNullOrEmpty(message.User))
            {
                var userText = message.User;
                if (isLast && webContext != null)
                {
                    // Fresh web results are only attached to the newest question.
                    userText = $"{webContext}\n\n{userText}";
                }

                history.AddUserMessage(userText);
            }

            if (!string.IsNullOrEmpty(message.Assistant))
            {
                history.AddAssistantMessage(message.Assistant);
            }
        }

        // Build execution settings
#pragma warning disable SKEXP0070
        PromptExecutionSettings settings = Config.Service switch
        {
            Service.Ollama => new OllamaPromptExecutionSettings()
            {
                Temperature = Config.Temperature,
                TopP = Config.TopP,
            },
            Service.OpenAI => new OpenAIPromptExecutionSettings()
            {
                MaxTokens = Config.MaxTokens,
                Temperature = Config.Temperature,
                TopP = Config.TopP == 1.0f ? null : Config.TopP,
            },
            Service.AzureOpenAI => new AzureOpenAIPromptExecutionSettings()
            {
                MaxTokens = Config.MaxTokens,
                Temperature = Config.Temperature,
                TopP = Config.TopP == 1.0f ? null : Config.TopP,
            },
            Service.Google => new GeminiPromptExecutionSettings()
            {
                MaxTokens = Config.MaxTokens,
                Temperature = Config.Temperature,
                TopP = Config.TopP,
            },
            Service.Mistral => new MistralAIPromptExecutionSettings()
            {
                MaxTokens = Config.MaxTokens,
                Temperature = Config.Temperature,
                TopP = Config.TopP,
            },
            Service.Groq => new OpenAIPromptExecutionSettings()
            {
                MaxTokens = Config.MaxTokens,
                Temperature = Config.Temperature,
                TopP = Config.TopP == 1.0f ? null : Config.TopP,
            },
            _ => new OpenAIPromptExecutionSettings()
            {
                MaxTokens = Config.MaxTokens,
                Temperature = Config.Temperature,
            },
        };

        await foreach (var content in ChatService.GetStreamingChatMessageContentsAsync(history, settings))
        {
            if (string.IsNullOrEmpty(content.Content))
            {
                continue;
            }

            yield return content.Content;
        }
    }

    public IEnumerable<string> MissingConfigsForService()
    {
        var missingFields = new List<string>();

        if (string.IsNullOrEmpty(Config.Model)) missingFields.Add("model");

        if (Config.Service == Service.Ollama)
        {
            if (string.IsNullOrEmpty(Config.Url)) missingFields.Add("url");
        }
        else if (Config.Service == Service.AzureOpenAI)
        {
            if (string.IsNullOrEmpty(Config.ApiKey)) missingFields.Add("apikey");
        }
        else if (Config.Service == Service.Google)
        {
            if (string.IsNullOrEmpty(Config.ApiKey)) missingFields.Add("apikey");
        }
        else if (Config.Service == Service.Mistral)
        {
            if (string.IsNullOrEmpty(Config.ApiKey)) missingFields.Add("apikey");
        }
        else if (Config.Service == Service.Groq)
        {
            if (string.IsNullOrEmpty(Config.ApiKey)) missingFields.Add("apikey");
        }

        return missingFields;
    }

    /// <summary>
    ///     Searches the web with Exa. With an API key configured, the official
    ///     REST API is used; without one, Exa's public keyless MCP endpoint is
    ///     called instead (free, but rate-limited).
    ///     Returns null when nothing useful was found.
    /// </summary>
    public async Task<WebSearchResult?> WebSearchAsync(string query)
    {
        return string.IsNullOrEmpty(Config.ExaApiKey)
            ? await WebSearchKeylessAsync(query)
            : await WebSearchWithKeyAsync(query);
    }

    private async Task<WebSearchResult?> WebSearchWithKeyAsync(string query)
    {
        var body = new JsonObject
        {
            ["query"] = query,
            ["numResults"] = 3,
            ["contents"] = new JsonObject
            {
                ["text"] = new JsonObject
                {
                    ["maxCharacters"] = 1000,
                },
            },
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.exa.ai/search");
        request.Headers.Add("x-api-key", Config.ExaApiKey ?? "");
        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        using var response = await ExaHttpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            var detail = ExtractJsonMessage(errorBody);
            throw new InvalidOperationException(
                $"Exa search failed with HTTP {(int)response.StatusCode}" +
                (string.IsNullOrEmpty(detail) ? string.Empty : $": {detail}"));
        }

        var json = await response.Content.ReadAsStringAsync();
        var results = JsonNode.Parse(json)?["results"]?.AsArray();
        if (results is not { Count: > 0 })
        {
            return null;
        }

        return new WebSearchResult(FormatResults(query, results), "Exa web search (API key)");
    }

    /// <summary>
    ///     Keyless search via Exa's public MCP endpoint. The URL whitelists the
    ///     advanced search tool; the response is an SSE stream whose first
    ///     message carries the tool result as JSON-RPC.
    /// </summary>
    private async Task<WebSearchResult?> WebSearchKeylessAsync(string query)
    {
        var url = "https://mcp.exa.ai/mcp?tools=web_search_exa,web_search_advanced_exa";
        var rpc = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "web_search_advanced_exa",
                ["arguments"] = new JsonObject
                {
                    ["query"] = query,
                    ["numResults"] = 3,
                },
            },
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        request.Content = new StringContent(rpc.ToJsonString(), Encoding.UTF8, "application/json");

        using var response = await ExaHttpClient.SendAsync(request);
        var raw = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Exa keyless search returned HTTP {(int)response.StatusCode}" +
                (string.IsNullOrEmpty(raw) ? string.Empty : $": {TextUtils.OneLine(raw, 200)}"));
        }

        var payload = ExtractMcpPayload(raw);
        if (payload?["error"] is JsonObject error)
        {
            throw new InvalidOperationException(
                $"Exa keyless search failed: {error["message"]?.ToString() ?? "unknown error"}. " +
                "You can set an Exa API key via /settings for reliable search.");
        }

        var text = payload?["result"]?["content"]?[0]?["text"]?.ToString();
        if (string.IsNullOrEmpty(text))
        {
            throw new InvalidOperationException("Exa keyless search returned an empty response — try again, or set an Exa API key via /settings.");
        }

        var results = JsonNode.Parse(text)?["results"]?.AsArray();
        if (results is not { Count: > 0 })
        {
            return null;
        }

        return new WebSearchResult(FormatResults(query, results), "Exa web search (keyless)");
    }

    private static string FormatResults(string query, JsonArray results)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Web search results for \"{query}\":");
        builder.AppendLine("Use these results to answer the user's question if they are relevant. Cite the sources you use.");

        for (var i = 0; i < results.Count; i++)
        {
            var result = results[i];
            var title = result?["title"]?.ToString() ?? result?["id"]?.ToString() ?? string.Empty;
            var url = result?["url"]?.ToString() ?? string.Empty;
            var text = result?["text"]?.ToString() ?? string.Empty;

            builder.AppendLine();
            builder.AppendLine($"[{i + 1}] {title} ({url})");
            builder.AppendLine(string.IsNullOrEmpty(text) ? "(no content extracted)" : text);
        }

        return builder.ToString();
    }

    /// <summary>
    ///     Parses a JSON-RPC payload that arrives either as plain JSON or as
    ///     an SSE stream ("event: ... / data: {...}") from the MCP endpoint.
    ///     Prefers the message carrying a result or error.
    /// </summary>
    private static JsonNode? ExtractMcpPayload(string raw)
    {
        raw = raw.Trim();
        if (raw.StartsWith("{", StringComparison.Ordinal))
        {
            return JsonNode.Parse(raw);
        }

        JsonNode? fallback = null;
        foreach (var line in raw.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var json = trimmed["data:".Length..].Trim();
            JsonNode? node;
            try
            {
                node = JsonNode.Parse(json);
            }
            catch (JsonException)
            {
                continue;
            }

            if (node == null)
            {
                continue;
            }

            if (node["result"] != null || node["error"] != null)
            {
                return node;
            }

            fallback = node;
        }

        return fallback;
    }

    /// <summary>
    ///     Pulls a human-readable message out of a JSON error body
    ///     ("error.message", "message", or "detail").
    /// </summary>
    internal static string ExtractJsonMessage(string? body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return string.Empty;
        }

        try
        {
            var node = JsonNode.Parse(body);
            var message = node?["error"]?["message"]?.ToString()
                ?? (node?["error"] is JsonValue v && v.TryGetValue<string>(out var err) ? err : null)
                ?? node?["message"]?.ToString()
                ?? node?["detail"]?.ToString();
            if (string.IsNullOrEmpty(message))
            {
                return string.Empty;
            }

            return TextUtils.OneLine(message, 180);
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    ///     Fetches the list of available model names from the configured
    ///     provider, using the server URL and API key from the settings.
    ///     Throws an InvalidOperationException with a friendly message.
    /// </summary>
    public async Task<List<string>> FetchModelsAsync()
    {
        List<string> models;
        try
        {
            var raw = Config.Service switch
            {
                Service.Ollama => await FetchOllamaModelsAsync(),
                Service.Google => await FetchGoogleModelsAsync(),
                Service.AzureOpenAI => await FetchAzureModelsAsync(),
                _ => await FetchOpenAiCompatibleModelsAsync(),
            };

            models = raw
                .Where(m => !string.IsNullOrEmpty(m))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(m => m, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException(Errors.DescribeHttpException(ex, Config), ex);
        }

        if (models.Count == 0)
        {
            throw new InvalidOperationException($"The {Config.Service} API returned an empty model list.");
        }

        return models;
    }

    private static readonly HttpClient ModelsHttpClient = new() { Timeout = TimeSpan.FromSeconds(25) };

    private static async Task<string> GetStringAsync(string url, string? bearer = null, string? headerName = null, string? headerValue = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (bearer != null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        if (headerName != null && headerValue != null)
        {
            request.Headers.Add(headerName, headerValue);
        }

        using var response = await ModelsHttpClient.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            var detail = ExtractJsonMessage(body);
            var hint = (int)response.StatusCode switch
            {
                401 or 403 => "the API key was rejected \u2014 check it with /settings",
                404 => "the endpoint was not found \u2014 check the server URL with /settings",
                429 => "rate limit reached \u2014 try again in a minute",
                >= 500 => "the provider is having trouble right now \u2014 try again later",
                _ => string.Empty,
            };
            throw new InvalidOperationException(
                $"HTTP {(int)response.StatusCode} from {url}" +
                (string.IsNullOrEmpty(hint) ? string.Empty : $" \u2014 {hint}") +
                (string.IsNullOrEmpty(detail) ? string.Empty : $" ({detail})"));
        }

        return body;
    }

    private Task<List<string>> FetchOllamaModelsAsync()
    {
        var baseUrl = (string.IsNullOrEmpty(Config.Url) ? "http://localhost:11434" : Config.Url).TrimEnd('/');
        return ParseModels($"{baseUrl}/api/tags", null, "models", "name");
    }

    private Task<List<string>> FetchGoogleModelsAsync()
    {
        var url = $"https://generativelanguage.googleapis.com/v1beta/models?key={Config.ApiKey}";
        return ParseModels(url, null, "models", "name", stripPrefix: "models/");
    }

    private Task<List<string>> FetchAzureModelsAsync()
    {
        var baseUrl = (string.IsNullOrEmpty(Config.Url) ? throw new InvalidOperationException("Azure OpenAI needs a server URL \u2014 set it with /settings.") : Config.Url).TrimEnd('/');
        var url = $"{baseUrl}/openai/deployments?api-version=2022-12-01";
        return ParseModels(url, null, "data", "id", headerName: "api-key", headerValue: Config.ApiKey);
    }

    private Task<List<string>> FetchOpenAiCompatibleModelsAsync()
    {
        var defaultUrl = Config.Service switch
        {
            Service.Groq => "https://api.groq.com/openai/v1",
            Service.Mistral => "https://api.mistral.ai/v1",
            _ => "https://api.openai.com/v1",
        };
        var baseUrl = (string.IsNullOrEmpty(Config.Url) ? defaultUrl : Config.Url).TrimEnd('/');
        return ParseModels($"{baseUrl}/models", Config.ApiKey, "data", "id");
    }

    private static async Task<List<string>> ParseModels(
        string url,
        string? bearer,
        string arrayName,
        string idName,
        string? stripPrefix = null,
        string? headerName = null,
        string? headerValue = null)
    {
        var body = await GetStringAsync(url, bearer, headerName, headerValue);
        var array = JsonNode.Parse(body)?[arrayName]?.AsArray();
        if (array == null)
        {
            throw new InvalidOperationException($"Unexpected response from {url}: no '{arrayName}' array.");
        }

        var models = new List<string>();
        foreach (var entry in array)
        {
            var name = entry?[idName]?.ToString();
            if (!string.IsNullOrEmpty(name))
            {
                if (stripPrefix != null && name.StartsWith(stripPrefix, StringComparison.Ordinal))
                {
                    name = name[stripPrefix.Length..];
                }

                models.Add(name);
            }
        }

        return models;
    }

#pragma warning disable SKEXP0001
#pragma warning disable SKEXP0070
    public static IChatCompletionService CreateService(Config config)
    {
        return config.Service switch
        {
            Service.Ollama => CreateOllamaService(config),
            Service.OpenAI => CreateOpenAIService(config),
            Service.AzureOpenAI => CreateAzureOpenAIService(config),
            Service.Google => CreateGoogleService(config),
            Service.Mistral => CreateMistralService(config),
            Service.Groq => CreateGroqService(config),
            _ => throw new ArgumentException("Invalid service name")
        };
    }

    private static IChatCompletionService CreateOllamaService(Config config) => new OllamaApiClient(
            uriString: config.Url,
            defaultModel: config.Model ?? ""
    ).AsChatCompletionService();

    private static OpenAIChatCompletionService CreateOpenAIService(Config config) => new(
        config.Model ?? " ",
        new OpenAI.OpenAIClient(
            new ApiKeyCredential(config.ApiKey ?? " "),
            new OpenAI.OpenAIClientOptions() { Endpoint = string.IsNullOrEmpty(config.Url) ? null : new Uri(config.Url) }));

    private static AzureOpenAIChatCompletionService CreateAzureOpenAIService(Config config) => new(
        config.Model ?? " ",
        new Azure.AI.OpenAI.AzureOpenAIClient(new Uri(config.Url), new ApiKeyCredential(config.ApiKey ?? " ")));

    private static GoogleAIGeminiChatCompletionService CreateGoogleService(Config config) => new(
        config.Model ?? " ",
        config.ApiKey ?? " ");

    private static MistralAIChatCompletionService CreateMistralService(Config config) => new(
        config.Model ?? " ",
        config.ApiKey ?? " ",
        string.IsNullOrEmpty(config.Url) ? null : new Uri(config.Url));

    /// <summary>
    ///     Groq exposes an OpenAI-compatible API, so it rides on the OpenAI
    ///     client with the Groq endpoint as the default URL.
    /// </summary>
    private static OpenAIChatCompletionService CreateGroqService(Config config) => new(
        config.Model ?? " ",
        new OpenAI.OpenAIClient(
            new ApiKeyCredential(config.ApiKey ?? " "),
            new OpenAI.OpenAIClientOptions()
            {
                Endpoint = new Uri(string.IsNullOrEmpty(config.Url) ? "https://api.groq.com/openai/v1" : config.Url),
            }));
}

/// <summary>
///     Maps exceptions from provider/HTTP calls to clear, actionable messages.
/// </summary>
internal static class Errors
{
    public static string DescribeHttpException(Exception ex, Config config)
    {
        switch (ex)
        {
            case HttpOperationException http when http.StatusCode is HttpStatusCode code:
                {
                    var status = (int)code;
                    var detail = Client.ExtractJsonMessage(http.ResponseContent);
                    return status switch
                    {
                        401 or 403 => $"The {config.Service} API key was rejected (HTTP {status}) \u2014 check your API key with /settings.{Suffix(detail)}",
                        404 => $"The model or endpoint was not found (HTTP 404) \u2014 check the model name '{config.Model}' and the server URL with /settings. Note: for Azure OpenAI use the deployment name as the model.{Suffix(detail)}",
                        429 => $"Rate limit reached at {config.Service} (HTTP 429) \u2014 wait a moment and try again.{Suffix(detail)}",
                        >= 500 => $"The {config.Service} API had a server error (HTTP {status}) \u2014 try again later.{Suffix(detail)}",
                        _ => $"HTTP {status} from the {config.Service} API.{Suffix(detail)}",
                    };
                }

            case HttpOperationException http:
                return $"HTTP error from the {config.Service} API: {Trim(http.Message)}";

            case ClientResultException clientResult:
                {
                    var status = clientResult.Status;
                    var detail = Client.ExtractJsonMessage(clientResult.Message);
                    return status switch
                    {
                        401 or 403 => $"The {config.Service} API key was rejected (HTTP {status}) \u2014 check your API key with /settings.{Suffix(detail)}",
                        404 => $"The model or endpoint was not found (HTTP 404) \u2014 check the model name '{config.Model}' and the server URL with /settings.{Suffix(detail)}",
                        429 => $"Rate limit reached at {config.Service} (HTTP 429) \u2014 wait a moment and try again.{Suffix(detail)}",
                        >= 500 => $"The {config.Service} API had a server error (HTTP {status}) \u2014 try again later.{Suffix(detail)}",
                        _ => $"HTTP {status} from the {config.Service} API.{Suffix(detail)}",
                    };
                }

            case HttpRequestException:
                return config.Service == Service.Ollama
                    ? $"Could not reach the Ollama server at '{config.Url}' \u2014 is it running? Start it with 'ollama serve' and check the URL with /settings."
                    : $"Could not reach {config.Service} at '{EffectiveEndpoint(config)}' \u2014 check the server URL and your internet connection.";

            default:
                return $"Could not chat with {config.Service}: {Trim(ex.Message)}";
        }
    }

    private static string EffectiveEndpoint(Config config) =>
        string.IsNullOrEmpty(config.Url) ? "(the provider's default endpoint)" : config.Url;

    private static string Suffix(string detail) =>
        string.IsNullOrEmpty(detail) ? string.Empty : $" ({detail})";

    private static string Trim(string message) =>
        string.IsNullOrEmpty(message) ? "no details" : TextUtils.OneLine(message, 180);
}

/// <summary>
///     Loads and saves the configuration file in %USERPROFILE%\.config\LlmExtensionForCmdPal.
/// </summary>
internal static class ConfigStore
{
    public static readonly string ConfigPath = "%USERPROFILE%\\.config\\LlmExtensionForCmdPal\\config.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>
    ///     Expanded path to the config file, for direct access (e.g. /config).
    /// </summary>
    public static string ConfigFilePath => Environment.ExpandEnvironmentVariables(ConfigPath);

    public static void Save(Config config)
    {
        var path = ConfigFilePath;
        var dir = Path.GetDirectoryName(path)!;

        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(config, JsonOptions);
        File.WriteAllText(path, json);
    }

    public static Config Read()
    {
        var path = ConfigFilePath;
        var dir = Path.GetDirectoryName(path)!;

        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        if (!File.Exists(path))
        {
            var defaultConfig = new Config();
            var json = JsonSerializer.Serialize(defaultConfig, JsonOptions);
            File.WriteAllText(path, json);
            return defaultConfig;
        }

        try
        {
            var fileContent = File.ReadAllText(path);
            var config = JsonSerializer.Deserialize<Config>(fileContent) ?? new Config();
            return config;
        }
        catch (JsonException)
        {
            var defaultConfig = new Config();
            var json = JsonSerializer.Serialize(defaultConfig, JsonOptions);
            File.WriteAllText(path, json);
            return defaultConfig;
        }
    }
}
