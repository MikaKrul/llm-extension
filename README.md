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

- **Guided setup wizard** — the extension asks you questions (service, server URL, model, API key) and you simply paste in the answers. No complicated `/` commands needed to get started.
- **Web search with Exa** (optional) — let the LLM answer using fresh results from the web.
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

Open Command Palette → **LLM Chat** → **Set up LLM chat** (or type `/setup`). The wizard asks you three questions:

1. **Which service** do you want to use?
2. **Connection details** — paste in your server URL, model name and API key. The wizard shows hints for each service, like the default Ollama URL (`http://localhost:11434/`).
3. **Web search** (optional) — enable Exa web search and paste your Exa API key.

That's it — you can start chatting. Advanced users can still tweak everything with `/` commands (type `/help` for the full list).

### Web search (optional)

When web search is enabled, your message is first sent to the [Exa](https://exa.ai/) search API and the results are given to the LLM together with your question, so answers can use fresh information from the web.

1. Create an API key at the [Exa dashboard](https://dashboard.exa.ai/).
2. Enable web search in the last setup step, or with `/websearch`.
3. Paste the key when asked, or with `/exakey <your-key>`.

## Development

- CI builds the project (and a signed MSIX) on every push and pull request — see [`.github/workflows/ci.yml`](.github/workflows/ci.yml).
- Pushing a tag like `v1.2.3` builds and publishes a GitHub Release with the installers — see [`.github/workflows/release.yml`](.github/workflows/release.yml).

## License

[MIT](LICENSE)
