# Handoff: axiominference.work / Kestral 1 / Axiom-CLI

You are picking up an existing, working system. Read this fully before changing anything — several pieces here look simple but have non-obvious failure modes that already burned real time to find. Don't rediscover them the hard way.

## What this is

A personally-run AI inference server (`ai.axiominference.work`) that serves one local LLM ("Kestral 1") through an OpenAI-compatible API, consumed by a custom CLI coding agent called **Axiom-CLI**. Three moving parts:

1. **Ollama** — runs the actual model, native API on `127.0.0.1:11434`.
2. **A FastAPI reverse proxy** (`G:\AI_Server\proxy`) — sits between the public domain and Ollama. Handles API-key auth, forces every request to the one installed model, and — critically — translates between the OpenAI-compatible wire format Axiom-CLI speaks and Ollama's native API, because the two are NOT interchangeable for this use case (see Gotchas).
3. **Axiom-CLI** (`G:\VSS_Projects\Axiom-CLI`) — a .NET 10 C# terminal coding agent, GitHub repo `YoMosa2009/Axiom-CLI`, installed locally at `C:\Users\mosaa\AppData\Local\axiom-cli\bin\axiom.exe`. Talks to Kestral 1 via the proxy using the OpenAI-compatible `/v1/chat/completions` shape, model id `custom-endpoint`.

`G:\AI_Server` is **not** a git repo — it's just live files on this machine. `G:\VSS_Projects\Axiom-CLI` **is** a git repo, pushed to GitHub, with a release workflow.

## Current model config

- Model: `hf.co/Tesslate/OmniCoder-9B-GGUF:Q5_K_M` (Tesslate's fine-tune of Qwen3.5-9B for agentic coding, replaced granite3.2:8b — granite unreliably emitted tool calls as text instead of structured `tool_calls`).
- Context: `OLLAMA_CONTEXT_LENGTH=45056` — chosen after measuring the actual tok/s-vs-context curve on this GPU (see `run_ollama_hidden.vbs` comments for the numbers). VRAM headroom on this card does **not** scale smoothly with context — it jumps in discrete CPU-offload steps. Don't guess at a new value; measure before changing it.
- Flash attention + q4_0 KV cache enabled.
- Only one model loaded at a time (`OLLAMA_MAX_LOADED_MODELS=1`) — VRAM is tight, don't add a second model without redoing the VRAM math.
- Sampling for Kestral 1 (set in Axiom-CLI, not Ollama): temperature 0.2–0.4 depending on task type, top_p 0.95, top_k 20 — these match Tesslate's official OmniCoder-9B model card recommendations for agentic/tool-calling use, not arbitrary values.

## Where things live and how to change them

### Ollama (the model server)
- Startup script (source of truth): `G:\AI_Server\run_ollama_hidden.vbs`
- **Duplicate copy that actually runs on boot**: `C:\Users\mosaa\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup\AIServer-1-Ollama.vbs` — these two files must be kept byte-identical. Editing only the `G:\AI_Server` copy does nothing until you also copy it to Startup.
- Warmup script: `G:\AI_Server\warmup_kestral.ps1` — forces the model into VRAM on startup (paired with `OLLAMA_KEEP_ALIVE=-1`) and raises `ollama.exe`/`llama-server.exe` process priority to `High`.
- To restart Ollama after a config change: kill `ollama.exe`, then run `wscript "G:\AI_Server\run_ollama_hidden.vbs"`. Verify with `ollama ps` (shows loaded model + GPU/CPU split) or `curl http://127.0.0.1:11434/api/tags`.
- **Never kill ollama.exe while a `ollama pull` might still be running in another shell.** Doing so once caused Ollama to respawn with default env vars (wrong `OLLAMA_MODELS` path), which looked like the model library had been wiped. It hadn't — it just started a fresh ad-hoc daemon. If this happens: kill the stray daemon, restart via the vbs script (which sets the correct env), confirm `ollama list` shows everything again.

### The proxy (FastAPI, Python)
- Code: `G:\AI_Server\proxy\app.py` (single file). Also `keys_store.py` (API key management), `admin_store.py` (admin dashboard auth), `templates/` (dashboard HTML).
- Launch script: `G:\AI_Server\proxy\run_proxy_hidden.vbs` — sleeps 15s then runs `.venv\Scripts\python.exe -m uvicorn app:app --host 127.0.0.1 --port 8080` hidden. This is also duplicated to the Startup folder; keep both in sync the same way as the Ollama script.
- To restart after editing `app.py`:
  1. Syntax-check first: `cd G:\AI_Server\proxy && .venv\Scripts\python.exe -c "import ast; ast.parse(open('app.py').read())"`
  2. `taskkill //F //IM python.exe` (kills both the proxy and nothing else should be named python.exe on this box — check `tasklist` if unsure)
  3. `wscript "G:\AI_Server\proxy\run_proxy_hidden.vbs"`
  4. Wait ~20s (the script itself sleeps 15s before launching), then `curl http://127.0.0.1:8080/health` should return `{"status":"ok"}`.
- API keys: managed via `keys_store.py`. From a Python shell in that directory: `keys_store.create_key("name")` (returns the raw key, shown once), `keys_store.list_keys()`, `keys_store.revoke_key(id)`. The public base URL clients use is `https://ai.axiominference.work/v1`.
- `FORCED_MODEL` in `app.py` pins every request to the one installed model regardless of what the client asks for — this is intentional (single-GPU box, don't want a client typo loading a second model).

### Axiom-CLI (the C# client)
- Repo: `G:\VSS_Projects\Axiom-CLI`, git + GitHub (`YoMosa2009/Axiom-CLI`).
- Build: `dotnet build` (from repo root). Test: `dotnet test` (currently 165 tests, keep it green). Local binary for manual testing: `dotnet publish Axiom.Cli -c Release -r win-x64 --self-contained false -o <dir>`.
- Version is a single source of truth: `Directory.Build.props` → `<Version>`. Bump it before every release.
- To ship a release: commit → `git push origin main` → `git tag -a vX.Y.Z -m "..."` → `git push origin vX.Y.Z`. `.github/workflows/release.yml` auto-builds all 5 platform RIDs and publishes a GitHub release on tag push — nothing manual needed. Watch it with `gh run watch <run-id> --exit-status`.
- **Before pushing, always `git fetch --tags` and check you're not behind `origin/main`.** More than once this session, work happened in parallel (another session, or an earlier phase) and pushed a release without this one's local checkout knowing. If you're behind, `git merge --ff-only origin/main` first, resolve conflicts if two changes touched the same file (has happened — `AgentLoop.cs` and `CouncilRolePrompts.cs` are common collision points), rebuild, retest, then proceed.
- The installed CLI (`axiom.exe`) updates itself via `axiom update`. Verify a new release actually reaches it after shipping — don't just trust the GitHub release exists.
- Configuring the CLI's own credentials/endpoint: `axiom config` is interactive (5 prompts: OpenRouter key, base URL, model id, context window, custom-endpoint API key — blank = keep existing). Can be driven non-interactively by piping newlines for fields to skip, e.g. `printf '\n\n\n\n%s\n' "$NEWKEY" | axiom config` to update only the API key.
- The custom endpoint's model id inside Axiom-CLI is the literal string `"custom-endpoint"` (constant `OpenRouterChatService.CustomEndpointModelId`), display label `"Kestral 1"`. It's named after the mechanism, not the model currently behind it.

## Gotchas that cost real time to find — don't relearn these

1. **Ollama's OpenAI-compatible endpoint (`/v1/chat/completions`) silently ignores every way to disable "thinking" mode** (`think:false`, `reasoning:{enabled:false}`, `reasoning_effort:"low"` — all no-ops, confirmed both via upstream GitHub issues and direct testing). Only the **native** `/api/chat` endpoint honors `think:false`. Qwen3.5-based models (which OmniCoder is) can spiral into thousands of tokens of chain-of-thought on prompts with precise constraints (e.g. "say hello in exactly 3 words"), burning the whole token budget and returning empty output. **Fix already in place**: the proxy has a dedicated `/v1/chat/completions` route that translates the request to native `/api/chat` (injecting `think:false`) and translates the response back to OpenAI shape. Don't remove this thinking it's redundant with a "real" OpenAI-compat passthrough — it isn't, the passthrough is what was broken.

2. **Ollama's native tool-calling does not stream incrementally.** It buffers the entire tool call server-side and emits it as a single line only once fully generated — measured a 52-second silent gap for one real file-write tool call. Combined with `httpx`'s `client.send(stream=True)` blocking until Ollama has *anything* to send, this means the naive version of the proxy's streaming code produced a multi-second-to-minute stretch with literally zero bytes reaching the client. Axiom-CLI has a client-side idle timeout (`StreamLineIdleTimeout = 60s`) that fires in that gap and mislabels it "(Stopped by user.)" even though nothing was cancelled. **Fix already in place**: the proxy's streaming generator (`_stream_openai_chunks` in `app.py`) owns the *entire* request lifecycle — including the initial `client.send()` — inside a background task, and races it against a 15-second heartbeat that yields an SSE comment line (`: keep-alive\n\n`) to keep the client's idle timer from firing. If you touch this code: the heartbeat has to wrap the send-and-open-connection step too, not just subsequent reads — that was the actual bug the first two attempts at this fix missed.

3. **Tool-call argument shape differs between native and OpenAI-compat.** Native Ollama returns `tool_calls[].function.arguments` as a JSON **object**. Real OpenAI wire format (and what Axiom-CLI's parser expects, via `JsonElement.GetString()`) is a JSON-**encoded string**. The proxy converts object→string on the way out (response) and string→object on the way back in (when a multi-turn conversation replays a prior assistant tool-call message from history) — both directions are needed, not just one.

4. **Windows PowerShell 5.1** (still the default shell resolved on this box unless `pwsh`/PowerShell 7+ is installed) defaults `Out-File`/`Set-Content`/`Add-Content`/`>` redirection to **UTF-16LE with a BOM**. If a model creates a file via `run_shell` instead of the `write_file` tool, you get a file with perfectly correct text but wrong on-disk encoding (garbled when read as UTF-8/ASCII). `write_file`/`write_files` themselves are fine (`File.WriteAllText` defaults to UTF-8 no-BOM in modern .NET) — the bug is specifically in shell-invoked writes. **Fix already in place** in `AgentToolExecutor.cs`: every Windows shell command gets an encoding-normalization preamble injected before it runs, and shell resolution now prefers `pwsh` over `powershell.exe` when available.

5. **VRAM/context tradeoff is not smooth.** Raising `OLLAMA_CONTEXT_LENGTH` past the point where it fully fits in VRAM doesn't gradually shift work to CPU — it jumps in large discrete steps (e.g. 0% CPU → 13% CPU for a tiny context increase, a bad trade; then a bigger context jump to 15% CPU that's actually a *better* value trade). If you're tuning context/VRAM, measure real tok/s at several points around the boundary — don't assume linearity.

6. **API keys accumulate revoked entries** in `keys_store`'s storage — normal, but if the CLI ever gets 401s that don't make sense, check `keys_store.list_keys()` for what's actually still valid vs. revoked before assuming something else is broken.

## Verification pattern that actually works

For any proxy change: test with raw `curl`/Python `urllib` directly against `http://127.0.0.1:8080/v1/chat/completions` first (fast iteration, full visibility into wire format) before testing through the real `axiom.exe` binary. For any Axiom-CLI change: `dotnet build && dotnet test`, then `dotnet publish` a throwaway binary and run real prompts against it in a scratch directory — don't trust unit tests alone for anything touching the model-facing prompt/wire format, since that's exactly where subtle regressions hide. Always confirm with real file output (`xxd`/hexdump for encoding issues) rather than trusting a JSON success summary alone.
