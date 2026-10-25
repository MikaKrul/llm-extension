<p align=center>
  <img src="./Assets/Logo.png" width="240px"/>
</p>

# <p align=center>LLM Extension for Command Palette</p>

<p align=center>Chat with a large language model (LLM) directly from <a href="https://learn.microsoft.com/en-us/windows/powertoys/command-palette/overview">PowerToys Command Palette</a>.</p>

It currently supports the following APIs:

- [Ollama](https://ollama.com/) — run models on your own machine
- [OpenAI](https://platform.openai.com/docs/overview) — and any other compatible API, such as [Docker Model Runner](https://docs.docker.com/model-runner/)
- [Azure OpenAI](https://learn.microsoft.com/en-us/azure/cognitive-services/openai/overview)
- [Google Gemini](https://aistudio.google.com/)
- [Groq](https://console.groq.com/)
- [Mistral](https://console.mistral.ai/)

Features:

- **One simple settings form** — type `/settings`, pick your service and paste in the connection details. No wizard, no `/` commands needed to get started.
- **Direct access to `config.json`** — type `/config` to open the raw configuration file in Notepad.
- **Web search with Exa** (optional) — let the LLM answer using fresh results from the web.
- **ChatGPT hand-off** — messages longer than 200 characters (or with multiple lines) open in ChatGPT in your browser instead of the configured model.
- Streaming responses, Markdown rendering and conversation history.

## Requirements

- Windows 11 with [PowerToys Command Palette](https://learn.microsoft.com/en-us/windows/powertoys/command-palette/overview) installed.

## Installation

### GitHub Releases

1. Download the `.msix` for your CPU architecture (usually `x64`) and the `.cer` file from the [Releases](../../releases) page.
2. Install the certificate: double-click the `.cer` file → **Install Certificate...** → **Local Machine** → **Place all certificates in the following store** → **Trusted People**. (This needs administrator rights.)
3. Double-click the `.msix` file to install the extension, or run:

    ```powershell
    Add-AppxPackage .\LlmExtension_<version>_x64.msix
    ```

### From source

Clone the repository and build it with Visual Studio 2022 (Windows Application SDK workload) or msbuild:

```powershell
msbuild LlmExtension\LlmExtension.csproj /restore /p:Configuration=Release /p:Platform=x64 /p:GenerateAppxPackageOnBuild=true /p:UapAppxPackageBuildMode=SideloadOnly
```

## Setup

Open Command Palette → **LLM Chat** → type `/settings`. The form shows all settings at once, prefilled with your current configuration:

- **Service** — Ollama, OpenAI (or any OpenAI-compatible API, such as Docker Model Runner), Azure OpenAI, Google Gemini, Groq or Mistral.
- **Model** — the model to use (for Azure OpenAI, the name of your deployment).
- **Server URL** — required for Ollama (default `http://localhost:11434/`) and Azure OpenAI; optional for the rest.
- **API key** — not needed for Ollama; for the other services, paste the key from the provider's website.
- **Web search** — toggle to let the LLM use [Exa](https://exa.ai/) web search, and paste your Exa API key (from the [Exa dashboard](https://dashboard.exa.ai/)).

Press **Save settings** and you're done. If your configuration is incomplete, a warning item appears on the main page of the extension — press Enter on it to open the same form.

To change settings later, use `/settings` again, or open `config.json` directly with `/config`. Advanced users can also tweak individual settings with `/` commands (type `/help` in the extension for the full list).

### Long messages

The palette's input is a single line, so messages longer than 200 characters (or containing line breaks) are handed off to ChatGPT: the extension opens `chatgpt.com` in your default browser with your message as the query, ready to send.

## Development

- CI builds the project (and a signed MSIX) on every push and pull request — see [`.github/workflows/ci.yml`](.github/workflows/ci.yml).
- Pushing a tag like `v1.2.3` builds and publishes a GitHub Release with the installers — see [`.github/workflows/release.yml`](.github/workflows/release.yml).

## License

[MIT](LICENSE)
