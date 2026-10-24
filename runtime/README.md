# Portable Strata FN runtime

Node.js 22+ is required. These ES modules use Node built-ins only; no npm install is needed. The gateway binds only to `127.0.0.1`. Upstream/queue overrides accept HTTP loopback origins, without credentials, paths, queries or redirects.

**Strata is the only supported engine.** Unsupported environment/backend-file selections fail explicitly; no engine detection, legacy dispatch, model substitution or installation migration occurs. Historical request/benchmark labels remain readable. The gateway, worker and benchmark never install, start, restart or stop a model. Starting the queue alone does not load one.

## Setup and identity

Attach an existing loopback Strata server without controlling it, or validate already installed pinned Strata 0.2.3.1 source, dedicated Python/CUDA dependencies, official engine 0.1.48 and prepared SC117 IQ3_XXS/IQ2_XS GGUF/pack/tokenizer/MTP configuration. This is not automatic engine/model acquisition or a verified fresh-GPU installation. See [INSTALL](../INSTALL.md) and [pinned inputs](../setup/backends.json).

The exact `QWEN_MODEL_ID` is required. Setup discovers/persists the effective context (minimum of health `max_context` and matching models `meta.n_ctx`) and passes `QWEN_CONTEXT_TOKENS`. Runtime requires `service=strata`, a matching model, boolean `loaded` and reported or explicitly verified capacity. Unloaded is reachable but not ready. No default 27B identity/context is substituted for Flash Next.

**Text and tool calls only**, even if upstream advertises images. Capabilities always report `vision_ready:false`. Images are rejected before token counting/generation. A 404 from `/tokenize` triggers text/tool conversion to `/v1/messages/count_tokens`, preserving tool IDs and JSON arguments. Image counting/multimodal forwarding is not supported.

JSON `/metrics` is normalized as `GET /status` and a narrow Prometheus compatibility `GET /metrics`. The compatibility metric prefix is retained for existing consumers; it is not selectable engine support. Missing rates/KV/GPU gauges stay absent/null. Failed/malformed metrics return a bounded non-200 response. Generation/tool SSE is forwarded unchanged, with usage/timings recorded.

Example for an already running server; replace model/address/context with verified values. This starts only a queue:

```powershell
$env:QWEN_BACKEND='strata'
$env:QWEN_UPSTREAM_URL='http://127.0.0.1:8088'
$env:QWEN_MODEL_ID='qwen3.8-flash-next-sc117-uncensored-iq3_xxs'
$env:QWEN_CONTEXT_TOKENS='262144' # only when verified for this server
node runtime/gateway/gateway.mjs
```

## Configuration

Set in the launcher's environment. Directory/executable paths must be absolute.

| Variable | Default / meaning |
| --- | --- |
| `QWEN_APP_ROOT` | Installation root inferred from module location |
| `QWEN_QUEUE_PORT` | `18022`, bound to loopback |
| `QWEN_QUEUE_URL` | `http://127.0.0.1:<QWEN_QUEUE_PORT>` for worker/benchmark |
| `QWEN_UPSTREAM_URL` | `http://127.0.0.1:18021`, or explicit existing Strata origin |
| `QWEN_MODEL_ID` | Required exact served Strata model; no default |
| `QWEN_BACKEND` | `strata` only, also the default; not process lifecycle |
| `QWEN_CONTEXT_TOKENS` | Optional explicitly verified capacity; clamps reported context; setup supplies its stored effective context |
| `QWEN_BACKEND_FILE` | Optional absolute file containing only `strata`, up to 64 bytes. Reread at admission; missing falls back to the supported configured selection; invalid/unreadable is an error |
| `QWEN_GATEWAY_DATA_DIR` | `<app root>/runtime/gateway`: token, requests, conversations, reports |
| `QWEN_WORKER_DATA_DIR` | `<app root>/runtime/worker`: `jobs` subdirectory |
| `QWEN_WORK_DIR` | `<app root>/work`: default agent working directory, must exist |

The default app root must be writable. Use data-directory overrides for a read-only installation. UI and gateway must use the same data/token directory. Do not ship an existing token. Changing upstream/model/context requires explicitly stopping the owned queue and preparing the matching connection before restart, never live switching an engine file.

Optional Hermes requires all four absolute paths: `QWEN_HERMES_PYTHON` (native `.exe` on Windows), `QWEN_HERMES_ROOT` (source), `QWEN_HERMES_HOME` (dedicated home), `QWEN_HERMES_GIT_BASH` (executable). Ordinary chat works without Hermes. Missing paths reject agent jobs before acceptance. Hermes receives fixed argument arrays with `shell:false`, local queue URL and its dedicated home; inherited `HERMES_PROFILE/CONFIG/ENV` are cleared. Folder selection is not a sandbox. Windows cancellation ends the Hermes process tree but does not undo prior changes. The bundled gateway requests terminal/file tools, not vision.

## Manual entry points

From the app root with the configured environment:

```powershell
node runtime/gateway/gateway.mjs       # queue only; Ctrl+C stops this queue
node runtime/worker/server.mjs         # MCP stdio entry point for host registration
node runtime/gateway/token-test.mjs quick # only an explicitly selected idle-server test
```

Benchmarks offer quick/8k/32k/64k/224k only when actual context fits. Chat reserves at least 4,096 output tokens (or the larger requested allowance); external output is capped at 12,000. Benchmark context tests reserve 256 output plus 256 template headroom. The 224k target is at most 228,864 input tokens and is offered only with sufficient verified capacity. All generation uses the shared FIFO. Counting does not generate tokens.

`cancel` plus newline on stdin cancels a benchmark; `--ui` also cancels on stdin closure. Exit 0 means completed; exit 1 means failed/cancelled. `보고서:` identifies the JSON report. Remove a stale `token-tests/running.lock` only after confirming that its process stopped. No benchmark auto-starts or loads a model.

## HTTP and MCP contracts

One active generation, FIFO admission and up to 24 pending requests. Requests use the verified model and disable reasoning. Chat counts system/history toward its context budget. A prompt over 12,000 characters is valid when token count fits; an independent 4,000,000-character limit remains. Browser Origin requests are refused. No remote redirect is followed.

- `GET /health`: 200 `{ready:true}` or 503 `{ready:false}`.
- `GET /queue`: `{running,waiting,ui_running}` counts.
- `GET /capabilities`: Strata model/loaded/context, chat input budget, fitting benchmark profiles, `vision_ready:false`, `video_ready:false`.
- `GET /v1/models`: upstream models. `GET /status`: normalized JSON metrics. `GET /metrics`: compatibility text metrics.
- `POST /tokenize`: `{messages}` → `{count}` via the Strata count adapter.
- `POST /v1/chat/completions`: OpenAI-compatible chat/SSE with `x-qwen-request-id`.
- All `/ui/*` routes require `x-ui-token` from the first-start generated `ui-token.txt`.
- `POST /ui/jobs`: `{mode:"chat"|"agent",prompt,session,cwd?}` → `{id}`; cwd applies to agent mode.
- `GET /ui/jobs/<id>`: output/status/usage/timestamps/performance. Poll until `finished_at`. Unknown IDs after restart return 404; never blindly resubmit.
- `DELETE /ui/jobs/<id>`: cancel. Cancelled replies are not committed as completed conversation pairs.
- `GET /ui/queue`: active/pending IDs, source and matching UI-job association, no prompt text.
- `DELETE /ui/conversations/<session>` or `/ui/agent-conversations/<session>`: authenticated/idempotent session deletion, active-session protection (409), works with model off.

`requests/<id>.json` stores usage/status/timing; `.request.txt`, `.md` and `.live.txt` contain private text/output. Binary media is redacted by logging helpers, but text can still contain secrets. Never publish runtime data.

The MCP worker exposes `qwen_submit` (task and explicit absolute text-file paths/ranges) and `qwen_result` (job ID). It reads only submitted files, returns unavailable when offline and saves under its own jobs directory. It does not run generated code or edit the submitted files. Local usage/speed includes queue/HTTP time; it is not a paid-provider savings metric.

## Offline validation

Use an approved PowerShell environment, already installed Node 22+ and a clean isolated results directory. Tests use ephemeral fake Strata loopback servers and temporary data, not installed models/GPU or user history:

```powershell
./setup/runtime-tests.ps1
./setup/setup-tests.ps1
./setup/strata-tests.ps1
./setup/strata-attach-tests.ps1
# After build.cmd; app tests require a new isolated results directory
./setup/app-tests.ps1
```

The explicit runtime suite covers FIFO, auth, two-turn context, session persistence/compaction, usage/timing, queued/active cancellation, optional Hermes failure, worker/offline fallback, quick/cancel and 224k benchmarks with reservations, text-only media rejection/redaction, fragmented UTF-8/tool SSE, backend-file validation, loaded/identity/context, count fallback/errors, native/missing/reset/malformed metrics and no redirects. Context tests verify exact 32K/256K server-derived budgets, one-token overflow and character limits without weakening assertions/timeouts. Setup tests cover package-copy completeness, Node reuse, opt-in settings backup, identity/ownership, and refusal to migrate legacy selections. App tests write only to `QWEN_SELF_TEST_DIR` and retain failures/previews. These are compatibility checks, not inference quality/throughput or fresh GPU installation evidence.

The package allowlist excludes generated tokens, requests, jobs, conversations, agent sessions, token reports, Hermes homes and credentials. Never recursively copy an existing runtime directory into a public release. Updating source does not delete prior user installations/history.
