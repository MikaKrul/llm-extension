// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Text.Json.Nodes;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace LlmExtension;

/// <summary>
///     Small helpers to build Adaptive Card JSON for the settings form.
/// </summary>
internal static class AdaptiveCards
{
    public static JsonObject TextBlock(string text, bool subtle = false, bool wrap = true)
    {
        var node = new JsonObject
        {
            ["type"] = "TextBlock",
            ["text"] = text,
            ["wrap"] = wrap,
        };

        if (subtle)
        {
            node["isSubtle"] = true;
        }

        return node;
    }

    public static JsonObject TextField(string id, string label, string placeholder, string value, bool required)
    {
        var node = new JsonObject
        {
            ["type"] = "Input.Text",
            ["id"] = id,
            ["label"] = label,
            ["placeholder"] = placeholder,
            ["value"] = value,
        };

        if (required)
        {
            node["isRequired"] = true;
            node["errorMessage"] = $"{label} is required";
        }

        return node;
    }

    public static JsonObject ChoiceSet(string id, string label, (string Title, string Value)[] choices, string value)
    {
        var options = new JsonArray();
        foreach (var (title, choiceValue) in choices)
        {
            options.Add(new JsonObject
            {
                ["title"] = title,
                ["value"] = choiceValue,
            });
        }

        return new JsonObject
        {
            ["type"] = "Input.ChoiceSet",
            ["id"] = id,
            ["label"] = label,
            ["style"] = "compact",
            ["value"] = value,
            ["isRequired"] = true,
            ["choices"] = options,
        };
    }

    public static JsonObject Toggle(string id, string label, bool value)
    {
        return new JsonObject
        {
            ["type"] = "Input.Toggle",
            ["id"] = id,
            ["label"] = label,
            ["value"] = value ? "true" : "false",
        };
    }

    public static string Form(string submitTitle, params JsonNode[] body)
    {
        var bodyArray = new JsonArray();
        foreach (var element in body)
        {
            bodyArray.Add(element);
        }

        var card = new JsonObject
        {
            ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
            ["type"] = "AdaptiveCard",
            ["version"] = "1.6",
            ["body"] = bodyArray,
            ["actions"] = new JsonArray(new JsonObject
            {
                ["type"] = "Action.Submit",
                ["title"] = submitTitle,
            }),
        };

        return card.ToJsonString();
    }
}

/// <summary>
///     The settings page: a single form with all configuration fields,
///     prefilled from the current config. Reached via /settings or the
///     incomplete-configuration warning on the main page.
///
///     The Command Palette host has no working way to navigate on a command
///     result (GoToPage is not implemented), but it does navigate when a list
///     item's command IS a page — so this page is used directly as the command.
/// </summary>
internal sealed partial class SettingsPage : ContentPage
{
    private readonly SettingsForm _form;

    public SettingsPage(Client client)
    {
        Title = "LLM chat settings";
        Name = "Settings";
        Icon = new IconInfo("\uE713");

        _form = new SettingsForm(client);
    }

    public override IContent[] GetContent() => [_form];
}

/// <summary>
///     One Adaptive Card form with every setting, validated per service when
///     submitted. On success the config is applied, persisted and the palette
///     goes back to the chat page.
/// </summary>
internal sealed partial class SettingsForm : FormContent
{
    private readonly Client _client;

    private static readonly (string Title, string Value)[] ServiceChoices =
    [
        ("Ollama — run models on your own machine", "ollama"),
        ("OpenAI — or any OpenAI-compatible API", "openai"),
        ("Azure OpenAI", "azureopenai"),
        ("Google — Gemini", "google"),
        ("Groq — fast open models", "groq"),
        ("Mistral AI", "mistral"),
    ];

    public SettingsForm(Client client)
    {
        _client = client;
        var config = client.Config;

        TemplateJson = AdaptiveCards.Form("Save settings",
            AdaptiveCards.TextBlock("LLM chat settings"),
            AdaptiveCards.TextBlock(
                "Pick a service and fill in the fields it needs, then press Save settings. " +
                "Only the fields the picked service needs are validated.",
                subtle: true),
            AdaptiveCards.ChoiceSet("service", "Service", ServiceChoices, ServiceChoiceFor(config.Service)),
            AdaptiveCards.TextField("model", "Model — or run /models in the chat to pick from the provider's live list", ModelPlaceholder(config.Service), config.Model ?? "", required: true),
            AdaptiveCards.TextField(
                "url",
                "Server URL — required for Ollama and Azure OpenAI, optional for the rest",
                "http://localhost:11434/ (Ollama) or https://<resource>.openai.azure.com/ (Azure)",
                config.Url,
                required: false),
            AdaptiveCards.TextField(
                "apikey",
                "API key — not needed for Ollama",
                "Paste your API key here",
                config.ApiKey ?? "",
                required: false),
            AdaptiveCards.Toggle("websearch", "Let the LLM search the web (Exa)", config.WebSearch),
            AdaptiveCards.TextField(
                "exakey",
                "Exa API key — optional; without a key, free keyless search is used",
                "Leave empty to keep the current key (or to use keyless search)",
                config.ExaApiKey ?? "",
                required: false));
    }

    private static string ServiceChoiceFor(Service service) => service switch
    {
        Service.Ollama => "ollama",
        Service.OpenAI => "openai",
        Service.AzureOpenAI => "azureopenai",
        Service.Google => "google",
        Service.Groq => "groq",
        Service.Mistral => "mistral",
        _ => "ollama",
    };

    private static string ModelPlaceholder(Service service) => service switch
    {
        Service.Ollama => "llama3.1",
        Service.OpenAI => "gpt-4o-mini",
        Service.AzureOpenAI => "<deployment-name>",
        Service.Google => "gemini-2.0-flash",
        Service.Groq => "llama-3.3-70b-versatile",
        Service.Mistral => "mistral-large-latest",
        _ => "model name",
    };

    private static bool UrlRequired(Service service) =>
        service is Service.Ollama or Service.AzureOpenAI;

    private static bool ApiKeyRequired(Service service) => service != Service.Ollama;

    public override CommandResult SubmitForm(string payload)
    {
        var data = JsonNode.Parse(payload)?.AsObject();
        if (data == null)
        {
            return CommandResult.GoBack();
        }

        Service? service = data["service"]?.ToString() switch
        {
            "ollama" => Service.Ollama,
            "openai" => Service.OpenAI,
            "azureopenai" => Service.AzureOpenAI,
            "google" => Service.Google,
            "groq" => Service.Groq,
            "mistral" => Service.Mistral,
            _ => null,
        };

        if (service == null)
        {
            LlmExtensionPage.ErrorToast("Please pick a service from the list");
            return CommandResult.KeepOpen();
        }

        var model = data["model"]?.ToString()?.Trim() ?? "";
        var url = data["url"]?.ToString()?.Trim() ?? "";
        var apiKey = data["apikey"]?.ToString()?.Trim() ?? "";
        var webSearch = data["websearch"]?.ToString() == "true";
        var exaKey = data["exakey"]?.ToString()?.Trim() ?? "";

        if (string.IsNullOrEmpty(model))
        {
            LlmExtensionPage.ErrorToast("Please fill in the model name (for Azure OpenAI, the deployment name)");
            return CommandResult.KeepOpen();
        }

        if (UrlRequired(service.Value) && string.IsNullOrEmpty(url))
        {
            LlmExtensionPage.ErrorToast("This service needs a server URL. For Ollama the default is http://localhost:11434/.");
            return CommandResult.KeepOpen();
        }

        if (!string.IsNullOrEmpty(url) &&
            (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) || (parsed.Scheme != "http" && parsed.Scheme != "https")))
        {
            LlmExtensionPage.ErrorToast($"'{url}' is not a valid URL, it should start with http:// or https://");
            return CommandResult.KeepOpen();
        }

        if (ApiKeyRequired(service.Value) && string.IsNullOrEmpty(apiKey))
        {
            LlmExtensionPage.ErrorToast("Please paste in your API key (or switch to Ollama, which doesn't need one)");
            return CommandResult.KeepOpen();
        }

        // Web search without an Exa key is allowed: keyless search is used.

        _client.Config.Service = service.Value;
        _client.Config.Model = model;
        _client.Config.Url = url;
        _client.Config.ApiKey = service.Value == Service.Ollama ? null : apiKey;
        _client.Config.WebSearch = webSearch;
        if (!string.IsNullOrEmpty(exaKey))
        {
            _client.Config.ExaApiKey = exaKey;
        }

        // Persist and let the main page refresh its state.
        LlmExtensionPage.ConfigurationApplied?.Invoke();

        LlmExtensionPage.SuccessToast($"Settings saved — chatting with {service.Value} ({model}).");

        return CommandResult.GoBack();
    }
}
