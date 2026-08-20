# Axiom CLI

A terminal coding agent that can use Axiom's Architect/Builder/Critic council or an
[OpenCode](https://opencode.ai)-powered agent runtime. Cross-platform (Windows/macOS/Linux),
with OpenRouter and self-hosted Kestrel 1 support.

Axiom CLI is the command-line sibling of [Axiom](https://github.com/YoMosa2009/Axiom), a
free local-first AI desktop app for Windows. This project extracts and adapts Axiom's coding-agent
core into a standalone, cross-platform tool — the two are separate codebases with separate
licenses (MIT here).

---

## Install

**macOS / Linux**

```sh
curl -fsSL https://raw.githubusercontent.com/YoMosa2009/Axiom-CLI/main/install.sh | sh
```

**Windows (PowerShell)**

```powershell
irm https://raw.githubusercontent.com/YoMosa2009/Axiom-CLI/main/install.ps1 | iex
```

Both scripts detect your OS/architecture, download the matching release from
[Releases](../../releases), and put `axiom` on your PATH.

### Windows copy/paste setup

After installing [Node.js LTS](https://nodejs.org/), paste this entire block into PowerShell:

```powershell
irm https://raw.githubusercontent.com/YoMosa2009/Axiom-CLI/main/install.ps1 | iex
axiom opencode install
axiom connect
axiom --engine opencode
```

## Get started with Kestrel 1 + OpenCode

Install [Node.js LTS](https://nodejs.org/) first: Axiom uses its included npm client to install
the pinned OpenCode runtime. Then, in a new terminal after installing Axiom, run:

```powershell
axiom opencode install
axiom connect
axiom --engine opencode
```

`axiom connect` prompts for the Kestrel URL (press Enter to use
`https://ai.axiominference.work/v1`) and a dedicated, revocable device key. Use a different key
for each computer; never reuse a browser key. It is saved in Axiom's encrypted local secret store.

To run a one-off agent task in the current repository:

```powershell
axiom code --engine opencode --yes "explain the failing test and fix it"
```

The OpenCode TUI, tools, file edits, shell commands, tests, and Git operations run on the computer
where you launch Axiom. Only model requests travel to Kestrel 1 over HTTPS.

### Legacy Axiom / OpenRouter

```sh
axiom config              # paste in an OpenRouter API key (openrouter.ai/keys)
axiom                     # full-window legacy Axiom TUI
axiom code "add input validation to the signup form"
```

Run `axiom update` any time to pull the latest release — the CLI also prints a one-line notice
when a newer version is available.

## Commands

| Command | What it does |
|---|---|
| `axiom [--model <id>]` | Full-window TUI chat (default). `/` tools · `@` lock folder · `/help` |
| `axiom config` | Store your OpenRouter API key, a self-hosted endpoint, and/or a [Tavily](https://tavily.com) API key for reliable `web_search` (all encrypted at rest; DPAPI on Windows, AES key-file on macOS/Linux) |
| `axiom connect` | Save Kestrel 1's HTTPS endpoint and this computer's revocable access key |
| `axiom code [--model <id>] "<task>"` | Architect → Builder → Critic council on the current directory |
| `axiom --engine opencode` | OpenCode's agent TUI, backed by Kestrel 1 |
| `axiom code --engine opencode [--yes] [--json] "<task>"` | OpenCode coding agent, backed by Kestrel 1 |
| `axiom opencode install` | Install Axiom's pinned OpenCode runtime for the current user |
| `axiom update` | Download and install the latest release for your platform |

`axiom chat` remains a supported alias for the default TUI.

Available models: `eidos` (Eidos 1, general-purpose reasoning), `hepha` (Hepha 1,
code-specialized) — the same aliases as the desktop app — and `kestral` (Kestrel 1), a
self-hosted OpenAI-compatible endpoint you configure yourself via `axiom config` (base URL,
model id, and API key). Kestrel 1 runs on whatever machine you point it at — useful for using
your own PC as inference compute from a laptop or another machine. `axiom code` uses the
desktop app's Workplace Council default model unless `--model` is given.

### OpenCode-backed Kestrel 1

`--engine opencode` keeps Kestrel 1 as the inference server while using OpenCode for the agent
runtime. Kestrel is fixed to `axiom/omnicoder-2-9b:q5_k_m` with a 131,072-token context window.
The agent runs locally, so it can use the files, tools, shell, tests, and Git available on the
computer where Axiom is launched.

```powershell
axiom opencode install
axiom connect                  # enter this computer's Kestrel device key
axiom --engine opencode
axiom code --engine opencode "explain the failing test and fix it"
```

Run `axiom opencode install` once to install Axiom's pinned OpenCode runtime into Axiom's own
application-data folder. It requires Node.js and npm; if you manage OpenCode yourself, put it on
`PATH` or set `AXIOM_OPENCODE_PATH` to its executable. The legacy engine remains the default;
choose OpenCode explicitly with `--engine opencode`.

### Cross-platform chat TUI
`axiom` paints its own interface (alternate screen) on **Windows, macOS, and Linux** so the
host terminal scrollbar is not part of the UX:

- Fixed header (◆ Axiom), scrollable transcript (PgUp/PgDn / arrows / wheel), pinned prompt at the bottom
- Shell tools use PowerShell/pwsh on Windows and bash/sh on macOS/Linux
- `/workspace <path>` or `@` locks the agent to a folder (sandbox cannot leave it)
- Sessions auto-save; `/sessions`, `/pick`, `/del`, `/resume`
- Ctrl+K command palette · Ctrl+Shift+M cycle approval mode (`auto` / `ask` / `plan`)
- Workflow: `/checkpoint` · `/plan` · `/changes` · `/accept` · `/reject` · `/replay` · `/jobs` · `/watch` · `/sticky` · `/pr`
- Tools: `str_replace` / `apply_patch` / `write_files` · `fetch_url` · `run_tests` · `find_symbol` · `/network` · `/policy` · secret redaction
- Intelligence: repo map + retrieval · history compaction · Critic evidence rules · auto diagnostics · `/spec` · `/map`
- Council: severity policy · parallel explore · user-in-loop Critic · post-merge · `.axiom/acceptance.md` · `/council`
- Optional: set `AXIOM_CLI_NO_NEW_WINDOW=1` to always run in the current terminal; `AXIOM_CLI_MOUSE=1` enables mouse tracking


## How the council works

1. **Architect** reads your request (plus repo context, if you're in `axiom code`) and writes a short implementation plan.
2. **Builder** implements it — for coding tasks, as a structured patch proposal.
3. **Critic** reviews the result against the original request.
4. Based on what the Critic finds: no issues → done; 1–2 issues → a targeted repair pass; 3+ issues → a full revision. Bounded to 2 repair passes, then the best available output is kept.

The whole task runs on one model rather than silently switching mid-task, so a role can always
pick up exactly where the last one left off.

## System requirements

- Windows (x64), macOS (x64 + arm64), or Linux (x64 + arm64)
- An [OpenRouter](https://openrouter.ai) account and API key (free tier available)
- For the Python sandbox tool: a system Python 3 install on PATH
- For the Java sandbox tool: a JDK (`javac`/`java`) on PATH

No .NET runtime install is required — releases are self-contained.

## What's not in v1

- **In-process local model inference.** This release doesn't run a GGUF/llama.cpp model inside
  the CLI itself — the `IChatPipeline` abstraction has a seam for that, but it isn't implemented
  yet. (A self-hosted OpenAI-compatible endpoint you point the CLI at over the network — see
  `kestral`/`axiom config` above — is supported today; it's still a network call, just to a
  server you control instead of OpenRouter.)
- **Visual/artifact rendering, KaTeX math, document ingestion.** These are GUI-specific features
  of the desktop app that don't have a terminal equivalent yet.
- **MCP connectors** (GitHub/Google/Todoist integrations from the desktop app).

## Building from source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```sh
git clone https://github.com/YoMosa2009/Axiom-CLI.git
cd axiom-cli
dotnet build
dotnet run --project Axiom.Cli -- chat
dotnet test
```

## License

MIT — see [LICENSE](LICENSE).

## Author

Built by [YoMosa2009](https://github.com/YoMosa2009).
