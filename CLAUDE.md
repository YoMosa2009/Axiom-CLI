# Handoff: axiominference.work / Kestral 1 / Axiom-CLI

You are picking up an existing, working system. Read this fully before changing anything — several pieces here look simple but have non-obvious failure modes that already burned real time to find. Don't rediscover them the hard way.

## What this is

A personally-run AI inference server (`ai.axiominference.work`) that serves a local LLM
("Kestrel 1") through an OpenAI-compatible API, consumed by a branded OpenCode build called
**Axiom Code**. Three moving parts:

1. **Ollama** - runs the actual model, native API on `127.0.0.1:11434`.
2. **A FastAPI control-plane proxy** (`G:\AI_Server\proxy`) - sits between the public domain and
   Ollama. Handles API-key auth and accounts, pins every request to the selected model profile,
   can start/stop compute on demand, serves a few server-side tools, and - critically -
   translates between the OpenAI-compatible wire format the client speaks and Ollama's native
   API, because the two are NOT interchangeable for this use case (see Gotchas).
3. **Axiom-CLI** (`S:\Axiom_CLI`) - a .NET 10 C# launcher, GitHub repo `YoMosa2009/Axiom-CLI`,
   installed at `C:\Users\mosaa\AppData\Local\axiom-cli\bin\axiom.exe`. `axiom code` generates
   an OpenCode config and runs Axiom Code against Kestrel 1 through the proxy.

Note the naming: the repo and docs say **Kestral** in some places and **Kestrel** in others.
They refer to the same thing.

`G:\AI_Server` is **not** a git repo - it's live files on this machine, and it is the only copy.
`S:\Axiom_CLI` **is** a git repo, pushed to GitHub, with a release workflow.

## Current model config

Kestrel 1 is no longer a single pinned model. `G:\AI_Server\proxy\model_profiles.py` is the
single source of truth for the profiles the server can serve, and `active_model.json` records
which one is selected. Only the selected profile is ever loaded into Ollama.

| Profile key | Ollama model | Label | Context | Vision |
| --- | --- | --- | --- | --- |
| `gemma4-12b` (default) | `gemma4:12b` | Gemma 4 12B IT | 262144 | yes |
| `omnicoder-2-9b` | `axiom/omnicoder-2-9b:q5_k_m` | OmniCoder-2-9B Q5_K_M | 262144 | no |

- Both profiles run a full 262,144-token context with all layers resident on the GPU
  (`gpu_layers: 99`), verified on the RTX 3060 12 GB. The old `OLLAMA_CONTEXT_LENGTH=45056`
  measurement is obsolete: context now comes from the profile, per request, as `options.num_ctx`.
- `OLLAMA_MODELS` is `S:\Ollama\models` — the model library is NOT on the same drive as the
  proxy. See the removable-drive gotcha below.
- Only one model is loaded at a time; switching profiles unloads the previous one.
- Sampling is set by the client, not Ollama. For the Axiom Code path it comes from
  `KestrelOpenCodeConfiguration` (Gemma 0.6, OmniCoder 0.3, top_p 0.95) and the proxy forwards
  `temperature`/`top_p`/`top_k` straight through into the native `options` block. If the client
  sends nothing, Ollama's own default wins — which is 1.0 for Gemma, far too loose for a
  tool-calling loop. That is a real failure mode, not a theoretical one (see gotcha 7).

## Where things live and how to change them

### Ollama (the model server)
- Env is set by `G:\AI_Server\run_ollama_hidden.vbs`; warmup/priority by
  `G:\AI_Server\warmup_kestral.ps1` (pairs with `OLLAMA_KEEP_ALIVE=-1`).
- Verify with `ollama ps` (loaded model + GPU/CPU split) or
  `curl http://127.0.0.1:11434/api/tags`.
- **Never kill ollama.exe while an `ollama pull` might still be running in another shell.** Doing
  so once caused Ollama to respawn with default env vars (wrong `OLLAMA_MODELS` path), which
  looked like the model library had been wiped. It hadn't — it just started a fresh ad-hoc
  daemon. If this happens: kill the stray daemon, restart via the vbs script, confirm
  `ollama list` shows everything again.

### Startup: Task Scheduler owns it now, not the Startup folder
The old `AIServer-1-Ollama.vbs` / `-2-Proxy` / `-3-Tunnel` Startup entries are retired and kept
in `G:\AI_Server\disabled-startup-scripts-backup\`. Do not re-enable them; they would create a
second proxy loop.

- Scheduled task **`AxiomInference Proxy Control Plane`** runs
  `wscript "G:\AI_Server\proxy\run_proxy_resilient.vbs"`, which is a restart loop around
  `uvicorn app:app --host 127.0.0.1 --port 8080`, appending failures to `proxy_watchdog.log`.
  Read that log first when the proxy misbehaves — it survives restarts.
- The only remaining Startup-folder entry, `AIServer-Proxy-ControlPlane.vbs`, is a three-line
  shim that just does `schtasks /Run` on that task so logon behavior still works.
- `AxiomInference Private MVS Bridge` and `Proxy` are both **Disabled** on purpose.
- The control plane deliberately does **not** load Ollama or the model at sign-in. It is a
  low-memory front door that can start compute on demand.

### The proxy (FastAPI, Python) — `G:\AI_Server\proxy`
`app.py` is the whole API surface. Supporting modules, each owning one concern:

| File | Owns |
| --- | --- |
| `keys_store.py` | API keys (`create_key`, `list_keys`, `revoke_key`) |
| `account_store.py` | End-user accounts (`kestrel_account.sqlite3`) |
| `admin_store.py` | Admin dashboard auth |
| `gate_store.py` | Chat access code |
| `control_store.py` | Power/compute control passcode — a **separate** secret from the gate by design, so a leaked chat code alone can never stop/start the machine's compute |
| `model_profiles.py` | The profile table above |
| `code_tool.py` | Sandboxed `run_code` |

Beyond `/v1/chat/completions` the proxy also serves `/health`, `/v1/models`,
`/v1/models/current` (Axiom Code reads this to learn which profile is live), the control-plane
routes, and server-side tools (`web_search`, `fetch_url`, `run_code`, project file
create/extract).

To restart after editing `app.py`:
1. Syntax-check first: `.venv\Scripts\python.exe -c "import ast; ast.parse(open('app.py').read())"`
2. Restart the task rather than hand-launching uvicorn:
   `schtasks /End /TN "AxiomInference Proxy Control Plane"` then `/Run`.
   (Hand-launching leaves the watchdog loop running and you get two proxies fighting for 8080.)
3. `curl http://127.0.0.1:8080/health` → `{"status":"ok","model":...,"context_window":...}`.

### Axiom-CLI / Axiom Code (the C# client)
- Repo: **`S:\Axiom_CLI`** (the `G:\VSS_Projects\Axiom-CLI` clone lives on the removable drive
  and goes away with it — don't treat it as the working copy). GitHub: `YoMosa2009/Axiom-CLI`.
- Build: `dotnet build`. Test: `dotnet test` (currently 247 tests, keep it green).
- **The product is now a bridge, not a bespoke agent loop.** `axiom code` generates an OpenCode
  config in memory (`Axiom.Core/OpenCode/KestrelOpenCodeConfiguration.cs`) and launches a
  branded OpenCode build called **Axiom Code** (`Axiom.Cli/OpenCodeRunner.cs`). The API key is
  passed as an env-var reference (`{env:AXIOM_KESTREL_API_KEY}`); no credential is ever written
  to a config file. OpenCode's data/config is isolated under
  `%LOCALAPPDATA%\axiom-cli\OpenCode` via `XDG_*` so it never collides with a standalone
  OpenCode install.
- The branding is two patches in `assets/opencode/` applied to a pinned upstream tag
  (`OpenCodeRunner.PinnedRuntimeVersion`, currently **1.18.29**). Both workflows derive the tag
  from that constant so the built runtime can never drift from what the CLI installs, and the
  `opencode-patches` CI job fails the build when a bump makes a patch stale. **When bumping the
  pin, always regenerate both patches** — they carry context lines and index hashes.
- Version is a single source of truth: `Directory.Build.props` → `<Version>`. Bump before release.
- To ship: commit → `git push origin main` → `git tag -a vX.Y.Z` → `git push origin vX.Y.Z`.
  Watch with `gh run watch <run-id> --exit-status`, and **confirm all 10 assets exist** (5 CLI +
  5 runtime). The release job now fails loudly on a partial release, but check anyway.
- **Before pushing, always `git fetch --tags` and check you're not behind `origin/main`.** Work
  has happened in parallel more than once. If behind, `git merge --ff-only origin/main` first.

## Gotchas that cost real time to find — don't relearn these

1. **Ollama's OpenAI-compatible endpoint (`/v1/chat/completions`) silently ignores every way to disable "thinking" mode** (`think:false`, `reasoning:{enabled:false}`, `reasoning_effort:"low"` — all no-ops, confirmed both via upstream GitHub issues and direct testing). Only the **native** `/api/chat` endpoint honors `think:false`. Qwen3.5-based models (which OmniCoder is) can spiral into thousands of tokens of chain-of-thought on prompts with precise constraints (e.g. "say hello in exactly 3 words"), burning the whole token budget and returning empty output. **Fix already in place**: the proxy has a dedicated `/v1/chat/completions` route that translates the request to native `/api/chat` (injecting `think:false`) and translates the response back to OpenAI shape. Don't remove this thinking it's redundant with a "real" OpenAI-compat passthrough — it isn't, the passthrough is what was broken.

2. **Ollama's native tool-calling does not stream incrementally.** It buffers the entire tool call server-side and emits it as a single line only once fully generated — measured a 52-second silent gap for one real file-write tool call. Combined with `httpx`'s `client.send(stream=True)` blocking until Ollama has *anything* to send, this means the naive version of the proxy's streaming code produced a multi-second-to-minute stretch with literally zero bytes reaching the client. Axiom-CLI has a client-side idle timeout (`StreamLineIdleTimeout = 60s`) that fires in that gap and mislabels it "(Stopped by user.)" even though nothing was cancelled. **Fix already in place**: the proxy's streaming generator (`_stream_openai_chunks` in `app.py`) owns the *entire* request lifecycle — including the initial `client.send()` — inside a background task, and races it against a 15-second heartbeat that yields an SSE comment line (`: keep-alive\n\n`) to keep the client's idle timer from firing. If you touch this code: the heartbeat has to wrap the send-and-open-connection step too, not just subsequent reads — that was the actual bug the first two attempts at this fix missed.

3. **Tool-call argument shape differs between native and OpenAI-compat.** Native Ollama returns `tool_calls[].function.arguments` as a JSON **object**. Real OpenAI wire format (and what Axiom-CLI's parser expects, via `JsonElement.GetString()`) is a JSON-**encoded string**. The proxy converts object→string on the way out (response) and string→object on the way back in (when a multi-turn conversation replays a prior assistant tool-call message from history) — both directions are needed, not just one.

4. **Windows PowerShell 5.1** (still the default shell resolved on this box unless `pwsh`/PowerShell 7+ is installed) defaults `Out-File`/`Set-Content`/`Add-Content`/`>` redirection to **UTF-16LE with a BOM**. If a model creates a file via `run_shell` instead of the `write_file` tool, you get a file with perfectly correct text but wrong on-disk encoding (garbled when read as UTF-8/ASCII). `write_file`/`write_files` themselves are fine (`File.WriteAllText` defaults to UTF-8 no-BOM in modern .NET) — the bug is specifically in shell-invoked writes. **Fix already in place** in `AgentToolExecutor.cs`: every Windows shell command gets an encoding-normalization preamble injected before it runs, and shell resolution now prefers `pwsh` over `powershell.exe` when available.

5. **VRAM/context tradeoff is not smooth.** Raising `OLLAMA_CONTEXT_LENGTH` past the point where it fully fits in VRAM doesn't gradually shift work to CPU — it jumps in large discrete steps (e.g. 0% CPU → 13% CPU for a tiny context increase, a bad trade; then a bigger context jump to 15% CPU that's actually a *better* value trade). If you're tuning context/VRAM, measure real tok/s at several points around the boundary — don't assume linearity.

6. **API keys accumulate revoked entries** in `keys_store`'s storage — normal, but if the CLI ever gets 401s that don't make sense, check `keys_store.list_keys()` for what's actually still valid vs. revoked before assuming something else is broken.

7. **OpenCode silently drops the temperature unless the model declares the capability.**
   `session/llm/request.ts` reads `input.model.capabilities.temperature` and sends *no*
   temperature at all when it is false - which is the default. The request then inherits
   Ollama's per-model default (1.0 for Gemma 4). At 1.0 the model narrates its next step instead
   of calling the tool, the turn ends with no tool call, and OpenCode correctly exits the loop.
   From the outside this looks exactly like "the agent just halts after making a plan."
   **Fix in place**: every entry in the generated model catalog sets `temperature: true` (plus
   `tool_call`, `attachment`, `modalities`), and `agent.build`/`agent.plan` carry explicit
   values. Do not "clean up" that `temperature: true` flag - it is a capability declaration, not
   a value.

8. **Native `/api/chat` splits a tool call across two objects.** The tool call arrives in one
   object and the terminating `done: true` line in another, and that last one carries an *empty*
   message. Computing `finish_reason` from it alone therefore reported `"stop"` for every
   streamed tool-calling turn. **Fix in place**: `_finish_reason` in `app.py` takes a
   `saw_tool_calls` flag threaded through the stream loop.

9. **The whole server lives on a removable drive.** `G:` is an external/removable volume. When it
   is unplugged the proxy cannot start at all (the watchdog loop just fails in a cycle and
   `netstat` shows nothing on 8080), while Ollama keeps serving happily because its model library
   is on `S:`. The symptom is a dead endpoint with a healthy-looking Ollama - check
   `Get-PSDrive` / `Get-Disk` before debugging anything else. Reconnecting the drive is enough;
   the watchdog recovers on its own.

10. **`Option Explicit` in the startup VBScripts.** A missing `Dim` raises a modal
    "Variable is undefined" dialog at every logon and the script never runs. It is easy to
    introduce and invisible until the next reboot. After editing any `.vbs`, verify with
    `cscript //nologo //B <file>` and confirm exit code 0.

11. **Kestrel's models cannot drive OpenCode's delegation tools, and the failure looks like a
    hang.** Given `task` or `skill`, a 9-12B model calls them with malformed arguments and
    retries forever: measured against a 26k-line repository, eleven consecutive `task` calls
    rejected for a missing `description`, not one file read, then a statement of intent. On
    screen this is simply "it ran for 31 seconds and did nothing". Both are turned off in the
    generated config (`DelegationTools`), via `tools[name]=false` *and* a permission deny -- the
    permission is what also strips the skills catalog out of the system prompt.

12. **A small model ends a turn by announcing its next step, and OpenCode is right to stop.** The
    loop exits when a turn produces no tool call, which is correct in general and wrong here: the
    model's own todo list still has unfinished items. The operating-rules instructions file alone
    does not fix this -- it is demonstrably in the prompt (verified on the wire) and the model
    still signs off with "I will continue...". `assets/opencode/axiom-code-persistence.patch`
    makes the loop refuse such a turn while todos remain, up to six times *in a row* (the counter
    resets whenever the model does real work). Do not reach for `agent.prompt` to inject rules
    instead: in `session/llm/request.ts` it *replaces* the built-in system prompt rather than
    adding to it, which strips the tool-usage guidance a small model needs most.

## Verification pattern that actually works

For any proxy change: test with raw `curl`/Python `urllib` directly against `http://127.0.0.1:8080/v1/chat/completions` first (fast iteration, full visibility into wire format) before testing through the real `axiom.exe` binary. For any Axiom-CLI change: `dotnet build && dotnet test`, then `dotnet publish` a throwaway binary and run real prompts against it in a scratch directory — don't trust unit tests alone for anything touching the model-facing prompt/wire format, since that's exactly where subtle regressions hide. Always confirm with real file output (`xxd`/hexdump for encoding issues) rather than trusting a JSON success summary alone.
