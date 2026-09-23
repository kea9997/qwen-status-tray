# Portable Qwen runtime

Node.js 22 or newer is required. These ES modules use only Node built-ins; there is no npm install step. The gateway listens only on `127.0.0.1`. Queue and upstream overrides accept HTTP loopback origins only (no remote URL, credentials or URL path).

The gateway, MCP worker and benchmark **never install, start, restart or stop a model server**. An operator must start the selected model backend separately. A stopped backend produces an unavailable response. Starting the queue alone does not load a model.

## Configuration

Set these environment variables in the launcher environment. All directory/executable paths must be absolute.

| Variable | Default / meaning |
| --- | --- |
| `QWEN_APP_ROOT` | App installation root, inferred as two directories above `runtime/gateway` when omitted |
| `QWEN_QUEUE_PORT` | `18022`; gateway binds to `127.0.0.1` |
| `QWEN_QUEUE_URL` | `http://127.0.0.1:<QWEN_QUEUE_PORT>`; worker and benchmark connect here |
| `QWEN_UPSTREAM_URL` | `http://127.0.0.1:18021`; existing backend origin |
| `QWEN_MODEL_ID` | `qwen3.8-27b`; must match the backend served model name |
| `QWEN_BACKEND` | `vllm` or `ninfer`, default `vllm`; controls context/time limits, not process lifecycle |
| `QWEN_BACKEND_FILE` | Optional absolute path to a file containing only `vllm` or `ninfer` (up to 64 bytes). Gateway rereads it for each new request; missing file falls back to `QWEN_BACKEND`, invalid/unreadable files return an error. Backend changes need no queue restart. |
| `QWEN_VISION_READY` | `1` explicitly enables images for a verified vision backend; default off; ninfer remains text only |
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

Benchmark profiles are `quick`, `8k`, `32k`, `64k`, and `224k` (last one requires the selected backend to be `ninfer`). Large profiles must fit the configured backend's context capacity. The 224K profile targets at most 228,864 input tokens, reserving 256 output tokens and another 256 tokens for template variation within the 229,376-token total limit. Prompt token counting uses the queue's normalized `/tokenize` endpoint; all model generation uses the shared queue. `cancel` followed by a newline on stdin cancels a benchmark. With `--ui`, closing stdin also cancels. Exit code is 0 for success, 1 for failure or cancellation; the final `보고서:` line contains the JSON report path. No model server is launched. Interrupted hard kills may leave `token-tests/running.lock`; remove it only after confirming that benchmark process has stopped.

Switch the backend only while the queue/benchmark is idle. A queued request keeps the backend mode selected at admission for its timeout/record; changing the backend selection file never starts or stops a server. A running benchmark keeps its startup mode for profile/time limits.

## HTTP API for the UI

The queue permits one generation at a time, keeps FIFO order and admits up to 24 queued requests. It rewrites requests to the configured model and disables reasoning, with a maximum output of 12,000 tokens for external requests. Direct chat reserves 4,096 output tokens: its input budget is 61,440 tokens for vLLM's 65,536-token context and 225,280 for NInfer's 229,376-token context. System text and conversation history count toward that input budget. Prompts over 12,000 characters are accepted when their actual token count fits; an independent 4,000,000-character safety limit remains. vLLM token counting uses `/tokenize`; NInfer uses `/v1/messages/count_tokens`, with system messages separated into `system` and `input_tokens` normalized to `count`. This replaces the need for a separate compatibility bridge. NInfer generation and streaming tool-call chunks pass through unchanged. No automatic backend detection or shell process scan occurs.

- `GET /health`: 200 with `{ready:true}` or 503 with `{ready:false}`.
- `GET /queue`: `{running,waiting,ui_running}` counts.
- `GET /capabilities`: `{vision_ready,image_limit:1,video_ready:false}`; vision capability is explicitly configured.
- `GET /v1/models`, `GET /metrics`: pass through to the backend.
- `POST /tokenize`: accepts `{messages}` and returns `{count}` for either backend. NInfer counting accepts text system/user/assistant messages; token counting does not perform generation.
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
node --test runtime/gateway/test-runtime.mjs runtime/gateway/test-ninfer.mjs runtime/gateway/test-context-limits.mjs runtime/gateway/test-conversations.mjs runtime/gateway/test-token-metrics.mjs runtime/gateway/test-media.mjs runtime/gateway/test-delete.mjs runtime/gateway/test-agent-sessions.mjs runtime/worker/test-stream.mjs
```

Coverage includes FIFO serialization, two-turn conversation context, persistence/compaction, usage/timing, queued and active cancellation, auth, unavailable Hermes, worker results/offline fallback, benchmark quick/cancellation, media limits/redaction, and fragmented UTF-8 SSE. NInfer fixtures also verify token-count translation, live backend-file switching/validation, streaming tool-call preservation, and a 224K benchmark with output/template reservation. Direct-chat boundary tests verify exact input budgets, one-token overflow rejection, and long character inputs. These fixtures verify API compatibility logic, not inference quality or throughput of a real model.

Release source allowlists must exclude `ui-token.txt`, `requests/`, `jobs/`, `conversations/`, `agent-sessions/`, `token-tests/`, Hermes homes and all generated configuration containing credentials. Never copy an existing runtime directory recursively into a public package.
