// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace LlmExtension;

/// <summary>
///     Live model picker: fetches the list of models from the configured
///     provider (with its API key) once when opened, lets the user search it,
///     and applies the chosen model to the configuration on Enter.
///     Reached with the /models command from the chat page.
/// </summary>
internal sealed partial class ModelsPage : DynamicListPage
{
    private readonly Client _client;
    private readonly ItemsChangedThrottle _itemsChanged;

    private List<string> _models = [];
    private bool _fetchStarted;
    private bool _fetchDone;
    private string? _error;

    public ModelsPage(Client client)
    {
        _client = client;
        _itemsChanged = new ItemsChangedThrottle(() => RaiseItemsChanged(), 90);

        Icon = new IconInfo("\uE713");
        Title = "Pick a model";
        Name = "Pick a model";
        PlaceholderText = "Search the model list...";
        ShowDetails = true;
    }

    public override void UpdateSearchText(string oldSearch, string newSearch)
    {
        // Filtering happens locally in GetItems from the latest SearchText; a
        // throttled raise on a worker thread keeps the visible list in sync
        // without flooding the host with a fetch per keystroke.
        _itemsChanged.Schedule();
    }

    public override IListItem[] GetItems()
    {
        if (!_fetchStarted)
        {
            _fetchStarted = true;
            IsLoading = true;
            _ = FetchModelsAsync();
        }

        if (_error != null)
        {
            return
            [
                new ListItem(new NoOpCommand())
                {
                    Icon = new IconInfo("\uE7BA"),
                    Title = "Could not load the model list",
                    Subtitle = TextUtils.OneLine(_error),
                    Details = new Details()
                    {
                        Title = "Error details",
                        Body = _error,
                    },
                },
            ];
        }

        if (!_fetchDone)
        {
            return
            [
                new ListItem(new NoOpCommand())
                {
                    Title = $"Loading models from {_client.Config.Service}...",
                    Subtitle = "The list is fetched live from the provider with your settings",
                },
            ];
        }

        var query = (SearchText ?? string.Empty).Trim();
        List<string> models = string.IsNullOrEmpty(query)
            ? _models
            : [.. _models.Where(m => m.Contains(query, StringComparison.OrdinalIgnoreCase))];

        if (models.Count == 0)
        {
            return
            [
                new ListItem(new NoOpCommand())
                {
                    Title = query.Length > 0 ? $"No models match '{query}'" : "No models found",
                    Subtitle = "Press Esc to go back, or check the connection details with /settings",
                },
            ];
        }

        var current = _client.Config.Model ?? string.Empty;

        return models
            .Select(model =>
            {
                var item = new ListItem(new PickModelCommand(_client, model))
                {
                    Title = model,
                    Subtitle = model == current
                        ? "Current model \u2014 press Enter to keep using it"
                        : "Press Enter to use this model",
                };

                if (model == current)
                {
                    item.Icon = new IconInfo("\uE8E5");
                }

                return item;
            })
            .ToArray();
    }

    private async Task FetchModelsAsync()
    {
        try
        {
            ExtLog.Log($"fetching models for {_client.Config.Service}");
            _models = await _client.FetchModelsAsync();
            ExtLog.Log($"fetched {_models.Count} models from {_client.Config.Service}");
        }
        catch (Exception ex)
        {
            ExtLog.LogError($"fetch models failed: {ex}");
            _error = ex.Message;
            LlmExtensionPage.ErrorToast(_error);
        }
        finally
        {
            IsLoading = false;
            _fetchDone = true;
            _itemsChanged.Schedule();
        }
    }
}

/// <summary>
///     Persists the chosen model and hands the update to the main page
///     (via ConfigurationApplied), which reinitializes the chat service.
/// </summary>
internal sealed partial class PickModelCommand : InvokableCommand
{
    private readonly Client _client;
    private readonly string _model;

    public PickModelCommand(Client client, string model)
    {
        _client = client;
        _model = model;
        Name = $"Use {model}";
    }

    public override ICommandResult Invoke()
    {
        _client.Config.Model = _model;
        LlmExtensionPage.ConfigurationApplied?.Invoke();
        LlmExtensionPage.SuccessToast($"Model set to {_model}");
        return CommandResult.GoBack();
    }
}
