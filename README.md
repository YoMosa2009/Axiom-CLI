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

## Get started with Kestrel 1 + Axiom Code

Install [Node.js LTS](https://nodejs.org/) first: Axiom uses its included npm client to install
the pinned runtime. Axiom Code is Axiom's branded OpenCode runtime; it retains OpenCode's tools,
commands, and TUI behavior. Then, in a new terminal after installing Axiom, run:

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

## Get started with OpenCode free models

No account, API key, or Kestrel connection is required. Select an `opencode/...` model and Axiom
starts OpenCode's built-in account-free provider:

```powershell
axiom --model opencode/big-pickle
axiom code --model opencode/big-pickle "add input validation to the signup form"
```

Suggested choices are `opencode/big-pickle`, `opencode/mimo-v2.5-free`,
`opencode/ling-3.0-flash-fin-free`, `opencode/nemotron-3-ultra-free`,
`opencode/nemotron-3.5-lightning-free`, and `opencode/muse-spark-1.3-contributor-free`.
Newly available provider models may be supplied directly as `opencode/<model-id>`.

### Legacy Axiom / OpenRouter

```sh
axiom config              # paste in an OpenRouter API key (openrouter.ai/keys)
axiom                     # full-window legacy Axiom TUI
axiom code "add input validation to the signup form"
```

Run `axiom update` any time to pull the latest release — the CLI also prints a one-line notice
when a newer version is available. The next `--engine opencode` launch automatically aligns an
existing Axiom-managed runtime with that release's tested version and Axiom Code wordmark; no npm
command or configuration prompt is required. If Axiom Code itself reports that an OpenCode update
is available, close it and run `axiom update`; its own updater is intentionally disabled because
Axiom distributes and verifies the compatible runtime.

## Commands

| Command | What it does |
|---|---|
| `axiom [--model <id>]` | Full-window TUI chat (default). `/` tools · `@` lock folder · `/help` |
| `axiom config` | Store your OpenRouter API key, a self-hosted endpoint, and/or a [Tavily](https://tavily.com) API key for reliable `web_search` (all encrypted at rest; DPAPI on Windows, AES key-file on macOS/Linux) |
| `axiom connect` | Save Kestrel 1's HTTPS endpoint and this computer's revocable access key |
| `axiom code "<task>"` | Axiom Code on the current directory, using the active Kestrel model |
| `axiom [path] --model opencode/big-pickle` | Account-free OpenCode TUI |
| `axiom code --model opencode/big-pickle "<task>"` | Account-free OpenCode coding agent |
| `axiom code --engine legacy [--model <id>] "<task>"` | Architect → Builder → Critic Council |
| `axiom connect openrouter` | Replace an unreadable Council credential through hidden local input |
| `axiom [path] --engine opencode` | Axiom Code agent TUI in `path`, backed by Kestrel 1 |
| `axiom [path] code --engine opencode [--yes] [--json] "<task>"` | Axiom Code coding agent in `path`, backed by Kestrel 1 |
| `axiom opencode install` | Install or refresh Axiom Code (Axiom's pinned OpenCode runtime) for the current user |
| `axiom update` | Download and install the latest release; the next Axiom Code launch refreshes its managed runtime automatically |

`axiom chat` remains a supported alias for the default TUI.

Available models: `eidos` (Eidos 1, general-purpose reasoning), `hepha` (Hepha 1,
code-specialized) — the same aliases as the desktop app — and `kestral` (Kestrel 1), a
self-hosted OpenAI-compatible endpoint you configure yourself via `axiom config` (base URL,
model id, and API key). Kestrel 1 runs on whatever machine you point it at — useful for using
your own PC as inference compute from a laptop or another machine. Bare `axiom code` uses
Axiom Code. Existing explicit `--model` or `--profile` selections retain the legacy route unless
`--engine opencode` is supplied; `opencode/<model-id>` always selects the OpenCode runtime.

### Axiom Code-backed Kestrel 1

`--engine opencode` keeps Kestrel 1 as the inference server while using Axiom Code, Axiom's
branded build of [OpenCode](https://opencode.ai), for the agent runtime. Its `/models` picker
offers both `Kestrel 1 · OmniCoder-2-9B Q5_K_M` and `Kestrel 1 Pro · Gemma 4 12B IT`, each with
a 262,144-token context window. Selecting one securely switches the self-hosted server; only one
model is loaded into VRAM at a time.
The agent runs locally, so it can use the files, tools, shell, tests, and Git available on the
computer where Axiom is launched.

```powershell
axiom opencode install
axiom connect                  # enter this computer's Kestrel device key
axiom --engine opencode G:\AxiomWork
axiom G:\AxiomWork code --engine opencode "explain the failing test and fix it"
```

Run `axiom opencode install` once to install Axiom Code into Axiom's own application-data folder.
It requires Node.js and npm; if you manage OpenCode yourself, put it on
`PATH` or set `AXIOM_OPENCODE_PATH` to its executable. Bare `axiom code` selects OpenCode;
use `--engine legacy` to select Council explicitly.

Choose the project folder when launching Axiom Code. For example, use
`axiom --engine opencode G:\AxiomWork` (interactive) or
`axiom G:\AxiomWork code --engine opencode "your task"` (one-off). In the `/move` picker,
use `Alt+G` to enter an existing folder within the current project, or `Alt+M` to create a project
copy. A session cannot be moved safely into an unrelated project: launch Axiom Code in that target
folder with one of the commands above instead. Axiom forwards the folder to Axiom Code instead of
silently discarding it.

Axiom Code compaction is enabled for Kestrel sessions. Kestrel has a 262,144-token service
window, and Axiom reserves 16,384 tokens for the response; compaction begins before the input
would exceed its 245,760-token budget.
It prunes older bulky tool output, retains the six newest user turns (up to OpenCode's supported
15,000 recent tokens), and continues from a fresh checkpoint. Kestrel requests have no fixed total
or header deadline; only a 15-minute no-output stream-stall safeguard remains.

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
- No account is needed for OpenCode's listed free models. An [OpenRouter](https://openrouter.ai)
  account and API key remain optional for the legacy Council engine.
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

## Scoped source review

### Automatic coverage in Axiom Code

Broad requests such as `axiom code "Analyze this repository"` now schedule source reads
inside the ordinary runtime. Up to four 200-line ranges are supplied per model turn;
large files continue from the actual returned line boundary. Reads use the existing
permission checks. Narrow requests containing `only`, `just`, or `limited to` keep
on-demand tool use instead of automatically expanding to the whole project.

The final coverage receipt reports supplied files, omissions, and unread ranges.
Limits are 10,000 inventory entries and 512 read ranges. Read failures, source-line
truncation, and exhausted limits are reported as incomplete, never full coverage.
Model comprehension is not guaranteed by source delivery. Repeated answers with
unfinished work and repeated tool loops now produce an explicit error.

Unchanged read ranges already retained in the conversation are referenced instead of
duplicating their source payload. Changed files and compacted results remain readable.

### Explicit excerpt review

Run `axiom review --plan` from a Git repository to preview a deterministic inventory.
Run `axiom review "Find correctness bugs"` to review those excerpts against the current
Kestrel model. This is a separate, read-only review command; ordinary `axiom code`
behavior is unchanged.

The command uses tracked working-tree source and documentation files (including staged
additions), ordered by path. Untracked files are outside the inventory. Non-allowlisted
extensions, empty/binary/unreadable files, links, and files over 4 MiB are reported as
omitted. The inventory is a snapshot taken at startup. Source excerpts are at most
12,000 characters, split at newlines where possible; very long lines can span passes.
Tracked source is sent to the configured Kestrel endpoint, just as source read by the
coding agent is. Preview the inventory before reviewing unfamiliar repositories.

Each excerpt gets a fresh request with no tools or delegation. Sampling remains model
specific. Responses are limited to 2,048 tokens and requests to three minutes. A failed,
empty, or truncated response stops the run with a nonzero exit code. Ctrl+C stops it.
The JSON report is checkpointed after every attempted pass under the application's
`reviews` directory; the command prints its path. It records answers, excerpt hashes,
omissions, failures, and unattempted ranges. It does not persist source excerpts or keys.

Coverage means **source supplied**, not verified comprehension. Independent excerpts
cannot establish cross-file correctness, and there is deliberately no generated final
summary that could hide missed work. This mode does not edit files, run tests, or claim
to replace an integrated architecture review.

## License

MIT — see [LICENSE](LICENSE).

## Author

Built by [YoMosa2009](https://github.com/YoMosa2009).
