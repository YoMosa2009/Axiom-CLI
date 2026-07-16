# Axiom CLI

A terminal coding agent — an Architect/Builder/Critic council that reads your codebase, proposes
patches, and applies them after your review. Cross-platform (Windows/macOS/Linux), cloud-first
via [OpenRouter](https://openrouter.ai), free to use with your own API key.

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

## Get started

```sh
axiom config              # paste in an OpenRouter API key (openrouter.ai/keys)
axiom chat                # opens a dedicated chat window (Windows) with tools & slash menu
axiom code "add input validation to the signup form"   # coding agent, run inside a repo
```

Run `axiom update` any time to pull the latest release — the CLI also prints a one-line notice
when a newer version is available.

## Commands

| Command | What it does |
|---|---|
| `axiom config` | Store your OpenRouter API key (encrypted at rest) |
| `axiom chat [--model <id>]` | Interactive chat in a dedicated window (Windows). Press `/` for tools & models (↑↓ + Enter), or use `/tools`, `/model`, `/clear` |
| `axiom code [--model <id>] "<task>"` | Connects the current directory, runs the Architect → Builder → Critic council, shows a diff, and asks before applying |
| `axiom update` | Downloads and installs the latest release over the current one |

Available models: `eidos` (Eidos 1, general-purpose reasoning) and `hepha` (Hepha 1,
code-specialized) — the same aliases as the desktop app. `axiom code` uses the desktop app's
Workplace Council default model unless `--model` is given.

In chat:
- Full-width layout with a dedicated multi-line prompt box and the active model labeled under it
- Messages stack like a GUI chat (You → Axiom) with live activity statuses (Thinking, Running, Building, Task completed, …)
- Type `/` for tools & commands (↑↓ + Enter); type `@` for recent folders to attach as agent workspaces
- The agent can run shell commands, edit files, search, build, and download inside attached dirs
- Assistant links are clickable (OSC-8); header shows `used / context` tokens across the full window


## How the council works

1. **Architect** reads your request (plus repo context, if you're in `axiom code`) and writes a short implementation plan.
2. **Builder** implements it — for coding tasks, as a structured patch proposal.
3. **Critic** reviews the result against the original request.
4. Based on what the Critic finds: no issues → done; 1–2 issues → a targeted repair pass; 3+ issues → a full revision. Bounded to 2 repair passes, then the best available output is kept.

The whole task runs on one model rather than silently switching mid-task, so a role can always
pick up exactly where the last one left off.

## System requirements

- Windows, macOS, or Linux (x64; arm64 on macOS)
- An [OpenRouter](https://openrouter.ai) account and API key (free tier available)
- For the Python sandbox tool: a system Python 3 install on PATH
- For the Java sandbox tool: a JDK (`javac`/`java`) on PATH

No .NET runtime install is required — releases are self-contained.

## What's not in v1

- **Local model inference.** This release is cloud-only via OpenRouter. The `IChatPipeline`
  abstraction has a seam for a local backend, but it isn't implemented yet.
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
