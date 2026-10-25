// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Windows.Foundation;
using System.ClientModel;
using Microsoft.SemanticKernel;

namespace LlmExtension;

internal sealed partial class LlmExtensionPage : DynamicListPage
{
    private static readonly string HelpMessage =
        "## What is this extension\n" +
        "\n" +
        "This extension lets you chat with LLM models — Ollama, OpenAI, Azure OpenAI, Google Gemini, Groq or Mistral — hosted on your own machine or by a third party.\n" +
        "\n" +
        "## Setting up the extension\n" +
        "\n" +
        "Run `/settings`, pick your service, and fill in the connection details (server URL, model, API key). When your configuration is incomplete, a warning item appears on the main page — press Enter on it to open the same settings form.\n" +
        "\n" +
        "- For **Ollama**, start the server yourself (`ollama serve`) and pull a model (`ollama pull llama3.1`). No API key needed.\n" +
        "- For **OpenAI**, **AzureOpenAI**, **Google**, **Groq** or **Mistral**, create an account and an API key on the provider's website, then paste the model name and key into the form.\n" +
        "- Any OpenAI-compatible API (such as Docker Model Runner) works through the **OpenAI** service by filling in its URL.\n" +
        "\n" +
        "## Web search\n" +
        "\n" +
        "Enable web search in the settings form (or with `/websearch`) and paste an Exa API key to let the LLM use fresh information from the web. When enabled, your message is first sent to Exa, and the results are given to the model together with your question.\n" +
        "\n" +
        "## How to chat\n" +
        "\n" +
        $"Type your message in the search box and press Enter to send it. Messages longer than {MaxChatLength} characters, or messages with multiple lines, are opened in ChatGPT in your browser instead of being sent to the configured model. Advanced commands are still available by typing `/`:\n" +
        "\n" +
        "- `/settings`: Open the settings form to set up the extension or change any setting.\n" +
        "- `/service`, `/url`, `/model`, `/apikey`: Change the connection (advanced alternative to the settings form).\n" +
        "- `/websearch`, `/exakey`: Toggle web search and set the Exa API key.\n" +
        "- `/system`, `/history`, `/maxtokens`, `/temperature`, `/topp`, `/detail`, `/debug`: Fine-tune the model behavior.\n" +
        "- `/clear`, `/reset`, `/config`, `/help`: Clear history, reset settings, open config.json in Notepad, show this message.\n";

    /// <summary>
    ///     Messages longer than this (or containing line breaks) are handed off
    ///     to ChatGPT in the browser instead of the configured model.
    /// </summary>
    private const int MaxChatLength = 200;

    /// <summary>
    ///     Set by the main page and raised by the settings form when the
    ///     configuration changed, so the main page can persist and refresh its state.
    /// </summary>
    internal static Action? ConfigurationApplied;

    private readonly Client _client;
    private readonly IList<ChatMessage> _messages;
    private readonly IDictionary<string, SlashCommand> _commands;

    private (string, ListItem)[] _commandsMemo;
    private ListItem[] _messagesMemo;

    public LlmExtensionPage()
    {
        Icon = IconHelpers.FromRelativePath("Assets\\SmallLlmExtensionLogo.png");
        Title = "LLM Chat";
        Name = "Chat with LLM";
        PlaceholderText = "Type here to chat, or start with '/' to use commands";
        ShowDetails = true;

        _client = new Client()
        {
            Config = ConfigStore.Read()
        };
        _client.ReinitializeService();
        _messages = [new() { User = "", Assistant = "" }];
        _commands = new Dictionary<string, SlashCommand>()
        {
            { "settings", new(
                () => $"Open the settings form (current service: {_client.Config.Service})",
                Page: () => new SettingsPage(_client)
            ) },
            { "service", new(
                () => $"Set the API service to call (currently: {_client.Config.Service})",
                "<one-of-[ Ollama | OpenAI | AzureOpenAI | Google | Groq | Mistral ]>",
                (sender, args, opts) => {
                    Service? service = opts.ToLowerInvariant() switch
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
                        ErrorToast($"Invalid service '{opts}', expected one of 'Ollama', 'OpenAI', 'AzureOpenAI', 'Google', 'Groq', 'Mistral'");
                        return;
                    }
                    else
                    {
                        _client.Config.Service = service ?? throw new ArgumentException();
                    }

                    RefreshConfigs();
                },
                "Service configured")
            },
            { "clear", new(
                () => $"Clear message history ({_messages.Count} message" + (_messages.Count > 1 ? "s" : "") + ")",
                Action: (sender, args, opts) => {
                    _messages.Clear();
                    _messages.Add(new() { User = "", Assistant = "" });
                    RaiseItemsChanged();
                },
                ActionToastText: "Message cleared")
            },
            { "url", new(
                () => $"Set server URL (current: {_client.Config.Url})",
                "<url>",
                (sender, args, opts) =>
                {
                    _client.Config.Url = opts;
                    RefreshConfigs();
                },
                "Server URL configured")
            },
            { "model", new(
                () => $"Set the model to use (current: {_client.Config.Model})",
                "<model-name>",
                (sender, args, opts) =>
                {
                    _client.Config.Model = opts;
                    RefreshConfigs();
                },
                "Model configured")
            },
            { "apikey", new(
                () => $"Set the API key (Ollama not applicable)",
                "<api-key>",
                (sender, args, opts) =>
                {
                    _client.Config.ApiKey = opts;
                    RefreshConfigs();
                },
                "API key configured")
            },
            { "websearch", new(
                () => $"Toggle Exa web search for your messages (current: {(_client.Config.WebSearch ? "on" : "off")})",
                Action: (sender, args, opts) =>
                {
                    _client.Config.WebSearch = !_client.Config.WebSearch;
                    RefreshConfigs();
                },
                ActionToastText: "Web search toggled")
            },
            { "exakey", new(
                () => $"Set the Exa API key for web search",
                "<exa-api-key>",
                (sender, args, opts) =>
                {
                    _client.Config.ExaApiKey = opts;
                    RefreshConfigs();
                },
                "Exa API key configured")
            },
            { "detail", new(
                () => $"Toggle full detailed response on the side (current: {_client.Config.Details})",
                Action: (sender, args, opts) =>
                {
                    _client.Config.Details = !_client.Config.Details;
                    RefreshConfigs();
                },
                ActionToastText: "Detailed response toggled")
            },
            { "system", new(
                () => $"Set the system prompt",
                "<system-prompt>",
                (sender, args, opts) =>
                {
                    _client.Config.System = opts;
                    RefreshConfigs();
                },
                "System prompt configured",
                () => ("Current System Prompt", _client.Config.System)
            ) },
            { "history", new(
                () => $"Set the message history count (current: {_client.Config.History})",
                "<history-count>",
                (sender, args, opts) =>
                {
                    try
                    {
                        var count = int.Parse(opts);

                        if (count <= 0)
                        {
                            ErrorToast($"Invalid history count {count}, expected positive integer");
                            return;
                        }

                        _client.Config.History = count;

                        RefreshConfigs();
                    }
                    catch (FormatException) when (!_client.Config.Debug)
                    {
                        ErrorToast($"Invalid history count '{opts}', expected integer");
                        return;
                    }
                },
                "Message history count configured")
            },
            { "maxtokens", new(
                () => $"Set the maximum token count (Ollama not applicable, current: {_client.Config.MaxTokens})",
                "<token-count>",
                (sender, args, opts) =>
                {
                    try
                    {
                        var count = int.Parse(opts);

                        if (count <= 0)
                        {
                            ErrorToast($"Invalid token count {count}, expected positive integer");
                            return;
                        }

                        _client.Config.MaxTokens = count;

                        RefreshConfigs();
                    }
                    catch (FormatException) when (!_client.Config.Debug)
                    {
                        ErrorToast($"Invalid token count '{opts}', expected integer");
                        return;
                    }
                },
                "Maximum token count configured")
            },
            { "temperature", new(
                () => $"Set the model temperature, indicating creativeness (current: {_client.Config.Temperature})",
                "<temperature-in-range-0.0-to-1.0>",
                (sender, args, opts) =>
                {
                    try
                    {
                        var value = float.Parse(opts);

                        if (value < 0.0 || value > 1.0)
                        {
                            ErrorToast($"Invalid temperature {value}, expected floating point number between 0.0 and 1.0");
                            return;
                        }

                        _client.Config.Temperature = value;

                        RefreshConfigs();
                    }
                    catch (FormatException) when (!_client.Config.Debug)
                    {
                        ErrorToast($"Invalid temperature '{opts}', expected floating point number");
                        return;
                    }
                },
                "Temperature configured")
            },
            { "topp", new(
                () => $"Set the model top P, indicating randomness (current: {_client.Config.TopP})",
                "<top-p-in-range-0.0-to-1.0>",
                (sender, args, opts) =>
                {
                    try
                    {
                        var value = float.Parse(opts);

                        if (value < 0.0 || value > 1.0)
                        {
                            ErrorToast($"Invalid top P {value}, expected floating point number between 0.0 and 1.0");
                            return;
                        }

                        _client.Config.TopP = value;

                        RefreshConfigs();
                    }
                    catch (FormatException) when (!_client.Config.Debug)
                    {
                        ErrorToast($"Invalid top P '{opts}', expected floating point number");
                        return;
                    }
                },
                "Top P configured")
            },
            { "help", new(
                () => $"Help message on usage of this extension",
                Details: () => ("Help message", HelpMessage)
            ) },
            { "config", new(
                () => "Open the configuration file (config.json) in Notepad",
                File: ConfigStore.ConfigFilePath
            ) },
            { "debug", new(
                () => $"Toggle printing of the complete exception (current: {_client.Config.Debug})",
                Action: (sender, args, opts) =>
                {
                    _client.Config.Debug = !_client.Config.Debug;
                    RefreshConfigs();
                },
                ActionToastText: "Printing of complete exception configured toggled")
            },
            { "reset", new(
                () => "Reset all settings",
                Action: (sender, args, opts) =>
                {
                    _client.Config = new Config();
                    RefreshConfigs();
                },
                ActionToastText: "All settings reset")
            },
        };

        ConfigurationApplied += RefreshConfigs;

        UpdateCommandsMemo();
        UpdateMessagesMemo();
    }

    public override void UpdateSearchText(string oldSearch, string newSearch)
    {
        if (string.IsNullOrEmpty(oldSearch) != string.IsNullOrEmpty(newSearch) || oldSearch.StartsWith('/') != newSearch.StartsWith('/'))
        {
            if (!IsLoading)
            {
                _messages[0].User = newSearch;
            }
            UpdateMessagesMemo();
            RaiseItemsChanged();
        }
        else if (newSearch.StartsWith('/'))
        {
            RaiseItemsChanged();
        }
    }

    public override IListItem[] GetItems()
    {
        try
        {
            if (!IsLoading && SearchText.StartsWith('/'))
            {
                var commandText = SearchText[1..];

                if (commandText.Contains(' '))
                {
                    commandText = commandText[..commandText.IndexOf(' ')];
                }

                return _commandsMemo
                    .OrderByDescending(c => c.Item1.Zip(commandText).TakeWhile((pair) => pair.First == pair.Second).Count())
                    .Select(c => // Shallow copy so that item selection/highlight won't stick to the previous first item
                    new ListItem(c.Item2)
                    {
                        Title = c.Item2.Title,
                        Subtitle = c.Item2.Subtitle,
                        Details = c.Item2.Details,
                    })
                    .ToArray();
            }

            if (_client.MissingConfigs.Any())
            {
                return [new ListItem(new SettingsPage(_client)) {
                    Icon = new IconInfo("\u26A0"),
                    Title = $"Configuration incomplete for {_client.Config.Service}",
                    Subtitle = $"Missing: {string.Join(", ", _client.MissingConfigs)} — press Enter to open the settings form" }];
            }

            return _messagesMemo;
        }
        catch (Exception ex)
        {
            ErrorToast(_client.Config.Debug ? ex.ToString() : "An unexpected error occurred");
            IsLoading = false;

            return [];
        }
    }

    private void RefreshConfigs()
    {
        _client.ReinitializeService();
        ConfigStore.Save(_client.Config);
        SearchText = "";
        UpdateCommandsMemo();
        UpdateMessagesMemo();
        RaiseItemsChanged();
    }

    private void UpdateCommandsMemo()
    {
        _commandsMemo = _commands.Select(c =>
        {
            // A ListItem whose command IS a page makes the host navigate to that
            // page; the GoToPage command result is not implemented by the host.
            ICommand command = c.Value.Action != null
                ? SendMessageCommand.CreateCommand(c.Key, c.Value, () => SearchText, _client.Config.Debug)
                : c.Value.Page != null
                    ? c.Value.Page()
                    : c.Value.Details != null
                        ? new MarkdownPage(c.Value.Details.Invoke().Item1, c.Value.Details.Invoke().Item2)
                        : c.Value.File != null
                            ? new OpenFileCommand(c.Value.File)
                            : new NoOpCommand();

            if (c.Value.Action != null)
            {
                ((SendMessageCommand)command).SendMessage += (_, _) =>
                {
                    SearchText = "";
                    OnPropertyChanged(nameof(SearchText));
                };
            }

            var item = new ListItem(command)
            {
                Title = $"/{c.Key}",
                Subtitle = c.Value.Description.Invoke()
            };

            if (c.Value.Param != null)
            {
                item.Title += $" {c.Value.Param}";
            }

            if (c.Value.Details != null)
            {
                var details = c.Value.Details.Invoke();
                item.Details = new Details()
                {
                    Title = details.Item1,
                    Body = details.Item2,
                };
            }

            return (c.Key, item);
        })
        .ToArray();
    }

    /// <summary>
    ///     Send-message logic for the search-box path (SendMessageCommand).
    /// </summary>
    private async Task SendMessageAsync(string userText)
    {
        if (IsLoading) return;
        if (string.IsNullOrWhiteSpace(userText)) return;

        // The palette's input is a single line — long or multiline messages are
        // opened in ChatGPT in the browser with the message as the query.
        if (userText.Contains('\r') || userText.Contains('\n') || userText.Length > MaxChatLength)
        {
            HandOffToChatGpt(userText);
            return;
        }

        var currentMessage = _messages[0];
        try
        {
            currentMessage.User = userText;
            IsLoading = true;

            SearchText = "";
            OnPropertyChanged(nameof(SearchText));

            UpdateMessagesMemo();
            RaiseItemsChanged();

            // Optional web search: fetch fresh results and hand them to the LLM.
            string? webContext = null;
            if (_client.Config.WebSearch)
            {
                if (string.IsNullOrEmpty(_client.Config.ExaApiKey))
                {
                    ErrorToast("Web search is on, but no Exa API key is set. Run /settings or /exakey to add one.");
                }
                else
                {
                    try
                    {
                        webContext = await _client.WebSearchAsync(userText);
                    }
                    catch (Exception ex) when (!_client.Config.Debug)
                    {
                        ErrorToast($"Exa web search failed: {ex.Message}");
                    }
                }
            }

            await foreach (var response in _client.Chat(_messages, webContext))
            {
                currentMessage.Assistant += response;
                UpdateMessagesMemo();
                RaiseItemsChanged();
            }

            SearchText = "";
            _messages.Insert(0, new() { User = "", Assistant = "" });
        }
        catch (Exception ex) when (!_client.Config.Debug && (ex is HttpRequestException || ex is ClientResultException))
        {
            ErrorToast(
                $"Error calling API over HTTP, is there a '{_client.Config.Service}' server running and accepting connections " +
                $"at '{_client.Config.Url}' with model '{_client.Config.Model}'? Or perhaps the API key is incorrect?"
                );
        }
        catch (HttpOperationException ex) when (_client.Config.Debug)
        {
            var dataString = "";
            foreach (DictionaryEntry item in ex.Data)
            {
                dataString += $"{item.Key}: {item.Value}\n";
            }

            if (!string.IsNullOrEmpty(dataString))
            {
                dataString = "\n" + dataString;
            }

            ErrorToast($"An HTTP error occurred: {ex.Message} with inner exception {ex.InnerException}{dataString}");
        }
        catch (Exception ex)
        {
            ErrorToast(_client.Config.Debug ? ex.ToString() : "An error occurred when attempting to chat with LLM");
        }
        finally
        {
            IsLoading = false;
            UpdateMessagesMemo();
            RaiseItemsChanged();
        }
    }

    /// <summary>
    ///     Opens ChatGPT in the default browser with the message as the query
    ///     parameter, then clears the draft in the palette.
    /// </summary>
    private void HandOffToChatGpt(string userText)
    {
        var url = $"https://chatgpt.com/?q={Uri.EscapeDataString(userText)}";

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            SuccessToast("Message opened in ChatGPT in your browser");
        }
        catch (Exception ex)
        {
            ErrorToast($"Could not open ChatGPT: {ex.Message}");
        }

        _messages[0].User = "";
        SearchText = "";
        OnPropertyChanged(nameof(SearchText));
        UpdateMessagesMemo();
        RaiseItemsChanged();
    }

    private void UpdateMessagesMemo()
    {
        var sendItem = new List<ListItem>();
        var historyItems = new List<ListItem>();

        foreach (var (m, index) in _messages.Select((m, i) => (m, i)))
        {
            if (string.IsNullOrEmpty(m.Assistant))
            {
                if (string.IsNullOrEmpty(m.User))
                {
                    // Idle slot — no item; an idle hint is inserted below when
                    // nothing else is left to show.
                }
                else if (index == 0)
                {
                    if (!IsLoading)
                    {
                        var command = new SendMessageCommand() { Debug = _client.Config.Debug };
                        command.SendMessage += async (sender, args) => await SendMessageAsync(SearchText);
                        sendItem.Add(new ListItem(command) { Title = "Press enter to send" });
                    }
                }
                else
                {
                    historyItems.Add(new ListItem(new DetailedResponsePage(m.User, "No response received.")) { Title = m.User, Subtitle = "No response received." });
                }
            }
            else
            {
                var item = new ListItem(new DetailedResponsePage(m.User, m.Assistant))
                {
                    Title = m.User,
                    Subtitle = m.Assistant,
                };

                if (_client.Config.Details)
                {
                    item.Details = new Details()
                    {
                        Title = m.User,
                        Body = m.Assistant,
                    };
                }

                historyItems.Add(item);
            }
        }

        if (!IsLoading)
        {
            var items = sendItem.Concat(historyItems).ToList();

            if (items.Count == 0)
            {
                // Nothing to show yet — hint how to start instead of an empty list.
                items.Add(new ListItem(new NoOpCommand())
                {
                    Title = "Type a message to chat",
                    Subtitle = "Press Enter to send — '/settings' to configure, '/help' for all commands",
                });
            }

            _messagesMemo = items.ToArray();
        }
        else
        {
            _messagesMemo = historyItems.ToArray();
        }
    }

    internal static void ErrorToast(string message)
    {
        new ToastStatusMessage(new StatusMessage()
        {
            Message = message,
            State = MessageState.Error,
        })
        {
            Duration = 10000,
        }.Show();
    }

    internal static void SuccessToast(string message)
    {
        new ToastStatusMessage(new StatusMessage()
        {
            Message = message,
            State = MessageState.Success,
        })
        {
            Duration = 10000,
        }.Show();
    }
}

internal sealed record SlashCommand(
    Func<string> Description,
    string? Param = null,
    Action<SendMessageCommand, object?, string>? Action = null,
    string? ActionToastText = null,
    Func<(string, string)>? Details = null,
    string? File = null,
    Func<ContentPage>? Page = null
);

internal sealed partial class SendMessageCommand : InvokableCommand
{
    public event TypedEventHandler<SendMessageCommand, ICommandResult?>? SendMessage;
    public required bool Debug { get; set; }
    public string? ToastText { get; set; }

    public static SendMessageCommand CreateCommand(
        string key,
        SlashCommand value,
        Func<string> searchTextFunc,
        bool debug)
    {
        var command = new SendMessageCommand() { Debug = debug };
        command.ToastText = value.ActionToastText;
        command.SendMessage += (sender, args) =>
        {
            var searchText = searchTextFunc();
            var opts = "";

            if (!searchText.StartsWith($"/{key}", StringComparison.InvariantCulture))
            {
                LlmExtensionPage.ErrorToast($"Command '{searchText}' not found");
                return;
            }

            if (value.Param != null)
            {
                if (searchText.StartsWith($"/{key} ", StringComparison.InvariantCulture))
                {
                    if (searchText.Length > $"/{key} ".Length)
                    {
                        opts = searchText[$"/{key} ".Length..].Trim();
                    }
                }
                else
                {
                    LlmExtensionPage.ErrorToast($"Expected argument '{value.Param}' for command '/{key}'");
                    return;
                }
            }

            value.Action?.Invoke(sender, args, opts);
        };

        return command;
    }

    public override ICommandResult Invoke()
    {
        CommandResult? result = null;

        try
        {
            SendMessage?.Invoke(this, result);
        }
        catch (Exception ex)
        {
            LlmExtensionPage.ErrorToast(Debug ? ex.ToString() : "An error occurred when running the command");
        }

        return ToastText != null
            ? CommandResult.ShowToast(new ToastArgs { Message = ToastText, Result = CommandResult.KeepOpen() })
            : CommandResult.KeepOpen();
    }
}

internal sealed partial class DetailedResponsePage : ContentPage
{
    public string User { get; set; }
    public string Assistant { get; set; }

    public DetailedResponsePage(string user, string assistant)
    {
        Title = "Detailed Response";
        User = user;
        Assistant = assistant;
    }

    public override IContent[] GetContent()
    {
        return [
            new MarkdownContent($"**{User}**\n\n---\n\n{Assistant}"),
        ];
    }
}

internal sealed partial class MarkdownPage : ContentPage
{
    public string Content { get; set; }

    public MarkdownPage(string title, string content)
    {
        Title = title;
        Content = content;
    }

    public override IContent[] GetContent()
    {
        return [
            new MarkdownContent(Content),
        ];
    }
}

/// <summary>
///     Opens a file in Notepad. Used by /config for direct access to
///     config.json.
/// </summary>
internal sealed partial class OpenFileCommand : InvokableCommand
{
    private readonly string _filePath;

    public OpenFileCommand(string filePath, string? name = null)
    {
        _filePath = filePath;
        Name = name ?? "Open file";
    }

    public override ICommandResult Invoke()
    {
        try
        {
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{_filePath}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            LlmExtensionPage.ErrorToast($"Could not open '{_filePath}': {ex.Message}");
            return CommandResult.KeepOpen();
        }

        return CommandResult.Dismiss();
    }
}
