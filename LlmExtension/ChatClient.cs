// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
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
}

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
    ///     Searches the web with the Exa API and formats the results as context for the LLM.
    ///     Returns null when nothing useful was found.
    /// </summary>
    public async Task<string?> WebSearchAsync(string query)
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
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        var results = JsonNode.Parse(json)?["results"]?.AsArray();
        if (results is not { Count: > 0 })
        {
            return null;
        }

        var builder = new StringBuilder();
        builder.AppendLine($"Web search results for \"{query}\":");
        builder.AppendLine("Use these results to answer the user's question if they are relevant. Cite the sources you use.");

        for (var i = 0; i < results.Count; i++)
        {
            var result = results[i];
            var title = result?["title"]?.ToString() ?? string.Empty;
            var url = result?["url"]?.ToString() ?? string.Empty;
            var text = result?["text"]?.ToString() ?? string.Empty;

            builder.AppendLine();
            builder.AppendLine($"[{i + 1}] {title} ({url})");
            builder.AppendLine(string.IsNullOrEmpty(text) ? "(no content extracted)" : text);
        }

        return builder.ToString();
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
///     Loads and saves the configuration file in %USERPROFILE%\.config\LlmExtensionForCmdPal.
/// </summary>
internal static class ConfigStore
{
    public static readonly string ConfigPath = "%USERPROFILE%\\.config\\LlmExtensionForCmdPal\\config.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static void Save(Config config)
    {
        var path = Environment.ExpandEnvironmentVariables(ConfigPath);
        var dir = Path.GetDirectoryName(path)!;

        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(config, JsonOptions);
        File.WriteAllText(path, json);
    }

    public static Config Read()
    {
        var path = Environment.ExpandEnvironmentVariables(ConfigPath);
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
