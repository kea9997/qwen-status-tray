# Portable Qwen runtime

Node.js 22 or newer is required. These ES modules use only Node built-ins; there is no npm install step. The gateway listens only on `127.0.0.1`. Queue and upstream overrides accept HTTP loopback origins only (no remote URL, credentials or URL path).

The gateway, MCP worker and benchmark **never install, start, restart or stop a model server**. An operator must start the selected model backend separately. A stopped backend produces an unavailable response. Starting the queue alone does not load a model.

## Strata FN first

The primary Windows path is Strata FN (SC117 Qwen3.8 Flash Next IQ3_XXS / IQ2_XS). Attach an existing loopback server without controlling its lifecycle, or use `setup/strata-install.ps1` to validate **already installed** pinned source 0.2.3.1, dedicated Python/CUDA dependencies, official engine 0.1.48 and prepared SC117 GGUF/pack/tokenizer/MTP configuration. This is not automatic engine/model acquisition or a verified fresh-GPU installation. See [INSTALL](../INSTALL.md). Legacy vLLM/ninfer paths remain available.

For Strata, set `QWEN_BACKEND=strata`, an explicit `QWEN_UPSTREAM_URL` and the exact `QWEN_MODEL_ID` discovered by setup. Setup persists the effective context (minimum of `/health.max_context` and matching `/v1/models` `meta.n_ctx`) in its receipt and launches the gateway with `QWEN_CONTEXT_TOKENS`. The runtime requires `service=strata`, a matching model, boolean `loaded` and a valid reported or explicitly verified context. Reachable but unloaded is not ready. A foreign service or wrong model is unavailable, never silently treated as vLLM.

**The Strata gateway path is text/tools only**, even if the upstream advertises images. Capabilities remain `vision_ready:false`; image requests receive a clear 400 before counting or generation. Pinned source has no `/tokenize`: a 404 triggers conversion of text/tool messages to `/v1/messages/count_tokens`. Tool-use IDs and JSON arguments are preserved. No image-counting or lossless multimodal compatibility is claimed; legacy media behavior is unchanged.

Strata JSON `/metrics` is exposed as normalized `GET /status` (native rates/context/totals) and a narrow Prometheus compatibility `GET /metrics`. Missing rates, KV and GPU gauges remain absent/null, not invented zero values. Upstream failure/malformed metrics returns a bounded non-200 error before headers are committed. Strata chat reserves at least 4,096 output tokens within actual context; benchmark profiles are filtered to those that fit (including 224k only at sufficient capacity). None run automatically.

Example for an already running server (replace model/address with verified values; this only starts the queue):

```powershell
$env:QWEN_BACKEND='strata'
$env:QWEN_UPSTREAM_URL='http://127.0.0.1:8088'
$env:QWEN_MODEL_ID='qwen3.8-flash-next-sc117-uncensored-iq3_xxs'
$env:QWEN_CONTEXT_TOKENS='262144' # only if verified for this server
node runtime/gateway/gateway.mjs
```

## Configuration

Set these environment variables in the launcher environment. All directory/executable paths must be absolute.

| Variable | Default / meaning |
| --- | --- |
| `QWEN_APP_ROOT` | App installation root, inferred as two directories above `runtime/gateway` when omitted |
| `QWEN_QUEUE_PORT` | `18022`; gateway binds to `127.0.0.1` |
| `QWEN_QUEUE_URL` | `http://127.0.0.1:<QWEN_QUEUE_PORT>`; worker and benchmark connect here |
| `QWEN_UPSTREAM_URL` | `http://127.0.0.1:18021`; existing backend origin |
| `QWEN_MODEL_ID` | Required for Strata, exact served model; legacy default `qwen3.8-27b` |
| `QWEN_BACKEND` | `strata`, `vllm` or `ninfer`; compatibility default `vllm`; not process lifecycle |
| `QWEN_CONTEXT_TOKENS` | Optional explicitly verified Strata capacity; clamps reported capacity; setup supplies its stored effective context |
| `QWEN_BACKEND_FILE` | Optional absolute file containing only `strata`, `vllm` or `ninfer` (up to 64 bytes). Reread per admission; missing falls back to `QWEN_BACKEND`, invalid/unreadable is an error. Strata connection changes require stopping the queue and explicitly configuring matching model/upstream/context before restart. |
| `QWEN_VISION_READY` | `1` explicitly enables images for verified vLLM vision; default off; Strata/ninfer remain text only |
| `QWEN_GATEWAY_DATA_DIR` | `<app root>/runtime/gateway`; UI token, request records, conversations and benchmark reports |
| `QWEN_WORKER_DATA_DIR` | `<app root>/runtime/worker`; worker writes the `jobs` subdirectory |
| `QWEN_WORK_DIR` | `<app root>/work`; default agent working directory, must already exist |

The app root must be writable for default storage. A read-only installation can use the two data directory overrides. The UI must read the generated token and request files from the same gateway data directory; it must not ship a pre-existing token.

Optional Hermes agent mode requires all four settings below. Ordinary chat works without Hermes. Missing paths cause a clear `Hermes unavailable` error before a job is accepted.

| Variable | Required value |
| --- | --- |
| `QWEN_HERMES_PYTHON` | Hermes virtual environment's Python executable (`.exe` on Windows) |
| `QWEN_HERMES_ROOT` | Hermes source working directory |
| `QWEN_HERMES_HOME` | Dedicated, already configured Hermes home directory |
| `QWEN_HERMES_GIT_BASH` | Git Bash executable; forwarded as `HERMES_GIT_BASH_PATH` |

Hermes receives fixed argument arrays through `spawn(..., {shell:false})`, a local API URL, its dedicated home, and the selected working directory. Inherited `HERMES_PROFILE`, `HERMES_CONFIG`, and `HERMES_ENV` are removed so that another installation cannot override the dedicated home. Environment variables cannot supply arbitrary shell command strings. Hermes itself can use terminal/file tools when the user requests agent work; choosing a folder is not a sandbox. Cancelling a Hermes job ends its process tree on Windows; it does not undo prior file changes.

## Manual commands

From the app installation root, in a terminal that has the configured environment:

```powershell
# Queue only. Keep the terminal open; Ctrl+C stops this queue process.
node runtime/gateway/gateway.mjs

# MCP stdio entry point. Configure this command in the host app's MCP settings.
node runtime/worker/server.mjs

# Benchmark an already running, idle backend via the queue.
node runtime/gateway/token-test.mjs quick
node runtime/gateway/token-test.mjs 8k
```

Benchmark profiles are `quick`, `8k`, `32k`, `64k`, and `224k` (224k requires ninfer or a sufficiently large verified Strata context). Large profiles must fit the configured backend's context capacity. The 224K profile targets at most 228,864 input tokens, reserving 256 output tokens and another 256 tokens for template variation within the 229,376-token total limit. Prompt token counting uses the queue's normalized `/tokenize` endpoint; all model generation uses the shared queue. `cancel` followed by a newline on stdin cancels a benchmark. With `--ui`, closing stdin also cancels. Exit code is 0 for success, 1 for failure or cancellation; the final `보고서:` line contains the JSON report path. No model server is launched. Interrupted hard kills may leave `token-tests/running.lock`; remove it only after confirming that benchmark process has stopped.

Switch the backend only while the queue/benchmark is idle. A queued request keeps the backend mode selected at admission for its timeout/record; changing the backend selection file never starts or stops a server. A running benchmark keeps its startup mode for profile/time limits.

## HTTP API for the UI

The queue permits one generation at a time, keeps FIFO order and admits up to 24 queued requests. It rewrites requests to the configured model and disables reasoning, with a maximum output of 12,000 tokens for external requests. Direct chat reserves 4,096 output tokens: its input budget is 61,440 tokens for vLLM's 65,536-token context and 225,280 for NInfer's 229,376-token context. System text and conversation history count toward that input budget. Prompts over 12,000 characters are accepted when their actual token count fits; an independent 4,000,000-character safety limit remains. vLLM token counting uses `/tokenize`; NInfer uses `/v1/messages/count_tokens`, with system messages separated into `system` and `input_tokens` normalized to `count`. This replaces the need for a separate compatibility bridge. NInfer generation and streaming tool-call chunks pass through unchanged. No automatic backend detection or shell process scan occurs.

- `GET /health`: 200 with `{ready:true}` or 503 with `{ready:false}`.
- `GET /queue`: `{running,waiting,ui_running}` counts.
- `GET /capabilities`: backend/model/loaded, actual `context_tokens`, `chat_input_budget`, fitting `benchmark_profiles`, `vision_ready`, `image_limit:1`, `video_ready:false`; Strata remains text only.
- `GET /v1/models`: pass through. `GET /metrics`: pass through for legacy backends, normalized compatibility metrics for Strata. `GET /status`: normalized Strata JSON metrics.
- `POST /tokenize`: accepts `{messages}` and returns `{count}` for the selected backend. NInfer counting accepts text system/user/assistant messages; token counting does not perform generation.
- `POST /v1/chat/completions`: OpenAI-compatible chat/SSE; `x-qwen-request-id` response header identifies the saved request. Requests with a browser `Origin` header are rejected.
- All `/ui/*` routes require `x-ui-token`, read from `<gateway data dir>/ui-token.txt`, generated on first queue startup.
- `POST /ui/jobs`: `{mode:"chat"|"agent",prompt,session,cwd?}` returns `{id}`. `cwd` applies to agent mode.
- `GET /ui/jobs/<id>`: status (`running`, `completed`, `incomplete`, `failed`, `cancelled`), streamed `output`, usage, timestamps and available `performance` data. Poll until `finished_at` is present. Jobs are in-memory; unavailable IDs return 404 after a queue restart.
- `DELETE /ui/jobs/<id>`: cancels that job; completed conversation pairs remain persisted, cancelled replies are not committed.
- `GET /ui/queue`: active/pending entries and `ui_job_id` for matching UI requests, without prompt content.
- `DELETE /ui/conversations/<session>` or `/ui/agent-conversations/<session>`: deletes that session's stored state. Active sessions return 409; model-off deletion is supported.

`requests/<id>.json` contains usage/status/timing; `.request.txt` contains the request text; `.md` and `.live.txt` contain generated output. Image binaries are redacted from request logs. Request text and output can contain private user data and must never be copied into a public release.

The MCP worker exposes `qwen_submit` (task plus explicit absolute text-file paths/ranges) and `qwen_result` (job ID). It reads only submitted files, returns `unavailable` if the queue/backend is offline, and writes results under `<worker data dir>/jobs`. It does not execute generated code or edit submitted files. `usage` is local model usage; request speed includes queue/HTTP time and is not an OpenAI cost or token-savings metric.

## Offline validation

These tests use only temporary fixture data and ephemeral loopback fake backends. No installed model or existing user records are read.

```powershell
node --test runtime/gateway/test-*.mjs runtime/worker/test-*.mjs
# Approved PowerShell environment; no policy override
./setup/strata-tests.ps1
./setup/strata-attach-tests.ps1
# After build.cmd, all ten app tests use a new isolated results directory
./setup/app-tests.ps1
```

Coverage includes FIFO serialization, two-turn conversation context, persistence/compaction, usage/timing, queued and active cancellation, auth, unavailable Hermes, worker results/offline fallback, benchmark quick/cancellation, media limits/redaction, and fragmented UTF-8 SSE. NInfer fixtures also verify token-count translation, live backend-file switching/validation, streaming tool-call preservation, and a 224K benchmark with output/template reservation. Direct-chat boundary tests verify exact input budgets, one-token overflow rejection, and long character inputs. Strata fixtures additionally cover loaded/model/context validation, fallback/tool token conversion, native/missing/reset metrics, failed/malformed metrics without hanging, text-only capabilities and image rejection, overflow before generation and tool SSE usage/timings. The attach setup test uses an ephemeral fake Strata plus already available Node to exercise Install/receipt/Verify and isolated opt-in app configuration without starting a gateway/model. App tests write only to `QWEN_SELF_TEST_DIR` and retain failures/previews there. These fixtures verify API compatibility logic, not inference quality or throughput of a real model.

Release source allowlists must exclude `ui-token.txt`, `requests/`, `jobs/`, `conversations/`, `agent-sessions/`, `token-tests/`, Hermes homes and all generated configuration containing credentials. Never copy an existing runtime directory recursively into a public package.
