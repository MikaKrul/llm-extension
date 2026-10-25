// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace LlmExtension;

/// <summary>
///     Opens a page in the palette. The page is created when the command runs,
///     so the form always starts fresh.
/// </summary>
internal sealed partial class GoToPageCommand : InvokableCommand
{
    private readonly Func<ContentPage> _pageFactory;

    public GoToPageCommand(Func<ContentPage> pageFactory, string? name = null)
    {
        _pageFactory = pageFactory;
        Name = name ?? "Open";
    }

    public override ICommandResult Invoke()
    {
        var page = _pageFactory();
        return CommandResult.GoToPage(new GoToPageArgs { PageId = page.Id });
    }
}

/// <summary>
///     Small helpers to build Adaptive Card JSON for the setup forms.
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
            ["style"] = "expanded",
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
///     Connection questions for a single service, used to build step 2 of the wizard.
/// </summary>
internal sealed record SetupInfo(
    string Help,
    string ModelLabel,
    string ModelPlaceholder,
    bool AskUrl,
    string UrlLabel,
    bool UrlRequired,
    string UrlPlaceholder,
    bool AskApiKey)
{
    public static SetupInfo For(Service service)
    {
        return service switch
        {
            Service.Ollama => new SetupInfo(
                "Ollama runs models on your own machine. Start it with 'ollama serve', then pull a model with 'ollama pull <name>'. No API key needed.",
                "Model", "llama3.1",
                true, "Server URL", true, "http://localhost:11434/",
                false),
            Service.OpenAI => new SetupInfo(
                "OpenAI's official API. You can also point this at any OpenAI-compatible API (for example Docker Model Runner) by filling in its URL.",
                "Model", "gpt-4o-mini",
                true, "Server URL (optional)", false, "Leave empty for the official OpenAI API",
                true),
            Service.AzureOpenAI => new SetupInfo(
                "Azure OpenAI. The 'model' is the name of your deployment.",
                "Deployment name", "<deployment-name>",
                true, "Endpoint", true, "https://<your-resource>.openai.azure.com/",
                true),
            Service.Google => new SetupInfo(
                "Google Gemini via Google AI Studio (aistudio.google.com).",
                "Model", "gemini-2.0-flash",
                false, "", false, "",
                true),
            Service.Groq => new SetupInfo(
                "Groq runs open models very fast. Create an API key at console.groq.com/keys.",
                "Model", "llama-3.3-70b-versatile",
                false, "", false, "",
                true),
            Service.Mistral => new SetupInfo(
                "Mistral AI's cloud service. You only need a URL when using a custom endpoint.",
                "Model", "mistral-large-latest",
                true, "Server URL (optional)", false, "Leave empty for the official Mistral API",
                true),
            _ => throw new ArgumentException($"Unknown service {service}"),
        };
    }
}

/// <summary>
///     Setup wizard, step 1 of 3: pick the service.
/// </summary>
internal sealed partial class SetupPage : ContentPage
{
    private readonly SetupServiceForm _form;

    public SetupPage(Client client)
    {
        Title = "Set up LLM chat";
        Name = "Set up LLM chat";
        Icon = new IconInfo("\uE713");

        _form = new SetupServiceForm(client);
    }

    public override IContent[] GetContent() => [_form];
}

internal sealed partial class SetupServiceForm : FormContent
{
    private readonly Client _client;

    public SetupServiceForm(Client client)
    {
        _client = client;

        var currentValue = client.Config.Service switch
        {
            Service.Ollama => "ollama",
            Service.OpenAI => "openai",
            Service.AzureOpenAI => "azureopenai",
            Service.Google => "google",
            Service.Groq => "groq",
            Service.Mistral => "mistral",
            _ => "ollama",
        };

        TemplateJson = AdaptiveCards.Form("Continue",
            AdaptiveCards.TextBlock("Set up LLM chat"),
            AdaptiveCards.TextBlock(
                "Step 1 of 3 — pick the service you want to chat with. The next steps ask you to paste in the connection details.",
                subtle: true),
            AdaptiveCards.ChoiceSet("service", "Which service do you want to use?", [
                ("Ollama — run models on your own machine", "ollama"),
                ("OpenAI — or any OpenAI-compatible API", "openai"),
                ("Azure OpenAI", "azureopenai"),
                ("Google — Gemini", "google"),
                ("Groq — fast open models", "groq"),
                ("Mistral AI", "mistral"),
            ], currentValue));
    }

    public override CommandResult SubmitForm(string payload)
    {
        var data = JsonNode.Parse(payload)?.AsObject();
        var choice = data?["service"]?.ToString();

        Service? service = choice switch
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
            LlmExtensionPage.ErrorToast($"Unknown service '{choice}', please pick one from the list");
            return CommandResult.KeepOpen();
        }

        return CommandResult.GoToPage(new GoToPageArgs { PageId = new SetupConnectionPage(_client, service.Value).Id });
    }
}

/// <summary>
///     Setup wizard, step 2 of 3: paste in the connection details for the chosen service.
/// </summary>
internal sealed partial class SetupConnectionPage : ContentPage
{
    private readonly SetupConnectionForm _form;

    public SetupConnectionPage(Client client, Service service)
    {
        Title = $"Connect to {service}";
        Name = "Set up LLM chat";
        Icon = new IconInfo("\uE713");

        _form = new SetupConnectionForm(client, service);
    }

    public override IContent[] GetContent() => [_form];
}

internal sealed partial class SetupConnectionForm : FormContent
{
    private readonly Client _client;
    private readonly Service _service;

    public SetupConnectionForm(Client client, Service service)
    {
        _client = client;
        _service = service;

        var info = SetupInfo.For(service);
        var config = client.Config;

        var body = new List<JsonObject>
        {
            AdaptiveCards.TextBlock($"Connect to {service}"),
            AdaptiveCards.TextBlock("Step 2 of 3 — paste your answers below, then press Continue.", subtle: true),
            AdaptiveCards.TextBlock(info.Help, subtle: true),
            AdaptiveCards.TextField("model", info.ModelLabel, info.ModelPlaceholder, config.Model ?? "", required: true),
        };

        if (info.AskUrl)
        {
            body.Add(AdaptiveCards.TextField("url", info.UrlLabel, info.UrlPlaceholder, config.Url, required: info.UrlRequired));
        }

        if (info.AskApiKey)
        {
            body.Add(AdaptiveCards.TextField("apikey", "API key", "Paste your API key here", config.ApiKey ?? "", required: true));
        }

        TemplateJson = AdaptiveCards.Form("Continue", body.ToArray());
    }

    public override CommandResult SubmitForm(string payload)
    {
        var data = JsonNode.Parse(payload)?.AsObject();
        if (data == null)
        {
            return CommandResult.GoBack();
        }

        var model = data["model"]?.ToString()?.Trim() ?? "";
        var url = data["url"]?.ToString()?.Trim() ?? "";
        var apiKey = data["apikey"]?.ToString()?.Trim() ?? "";

        var info = SetupInfo.For(_service);

        if (string.IsNullOrEmpty(model))
        {
            LlmExtensionPage.ErrorToast($"Please fill in the {info.ModelLabel.ToLowerInvariant()}");
            return CommandResult.KeepOpen();
        }

        if (info.AskUrl && info.UrlRequired && string.IsNullOrEmpty(url))
        {
            LlmExtensionPage.ErrorToast("Please fill in the server URL");
            return CommandResult.KeepOpen();
        }

        if (!string.IsNullOrEmpty(url) &&
            (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) || (parsed.Scheme != "http" && parsed.Scheme != "https")))
        {
            LlmExtensionPage.ErrorToast($"'{url}' is not a valid URL, it should start with http:// or https://");
            return CommandResult.KeepOpen();
        }

        if (info.AskApiKey && string.IsNullOrEmpty(apiKey))
        {
            LlmExtensionPage.ErrorToast("Please paste in your API key");
            return CommandResult.KeepOpen();
        }

        _client.Config.Service = _service;
        _client.Config.Model = model;
        _client.Config.Url = info.AskUrl ? url : string.Empty;
        _client.Config.ApiKey = info.AskApiKey ? apiKey : null;

        // Persist and let the main page pick up the new state before continuing.
        LlmExtensionPage.ConfigurationApplied?.Invoke();

        if (_client.MissingConfigs.Any())
        {
            LlmExtensionPage.ErrorToast($"Some settings are still missing: {string.Join(", ", _client.MissingConfigs)}");
            return CommandResult.KeepOpen();
        }

        return CommandResult.GoToPage(new GoToPageArgs { PageId = new SetupWebSearchPage(_client).Id });
    }
}

/// <summary>
///     Setup wizard, step 3 of 3: optional Exa web search, then done.
/// </summary>
internal sealed partial class SetupWebSearchPage : ContentPage
{
    private readonly SetupWebSearchForm _form;

    public SetupWebSearchPage(Client client)
    {
        Title = "Web search (optional)";
        Name = "Set up web search";
        Icon = new IconInfo("\uE713");

        _form = new SetupWebSearchForm(client);
    }

    public override IContent[] GetContent() => [_form];
}

internal sealed partial class SetupWebSearchForm : FormContent
{
    private readonly Client _client;

    public SetupWebSearchForm(Client client)
    {
        _client = client;

        TemplateJson = AdaptiveCards.Form("Finish setup",
            AdaptiveCards.TextBlock("Web search (optional)"),
            AdaptiveCards.TextBlock(
                "Step 3 of 3 — when enabled, the extension first searches the web with Exa and gives the results to the LLM together with your question, so answers can use fresh information.",
                subtle: true),
            AdaptiveCards.Toggle("websearch", "Let the LLM search the web (Exa)", client.Config.WebSearch),
            AdaptiveCards.TextField(
                "exakey", "Exa API key",
                "Paste your Exa API key (dashboard.exa.ai) — or leave empty to skip",
                client.Config.ExaApiKey ?? "",
                required: false));
    }

    public override CommandResult SubmitForm(string payload)
    {
        var data = JsonNode.Parse(payload)?.AsObject();
        var enabled = data?["websearch"]?.ToString() == "true";
        var key = data?["exakey"]?.ToString()?.Trim();

        if (enabled && string.IsNullOrEmpty(key) && string.IsNullOrEmpty(_client.Config.ExaApiKey))
        {
            LlmExtensionPage.ErrorToast("Web search needs an Exa API key. Paste one (from dashboard.exa.ai), or set the toggle to off.");
            return CommandResult.KeepOpen();
        }

        _client.Config.WebSearch = enabled;
        if (!string.IsNullOrEmpty(key))
        {
            _client.Config.ExaApiKey = key;
        }

        // Persist and let the main page pick up the new state.
        LlmExtensionPage.ConfigurationApplied?.Invoke();

        LlmExtensionPage.SuccessToast($"Setup complete — chatting with {_client.Config.Service} ({_client.Config.Model}).");

        return CommandResult.GoHome();
    }
}
