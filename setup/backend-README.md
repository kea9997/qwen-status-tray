# Model backend installation

This installer builds source-pinned Docker backends and downloads model files into your own installation. It does **not** distribute weights or credentials. `Install` prepares files and images; only a separate `Start` runs the model server. No measured speed or successful GPU execution is claimed for these deployment scripts.

## Supported profiles

| Backend / model | GPU | Engine | Context | Model preparation |
|---|---|---|---:|---|
| vllm / standard | RTX 3090, 4090, 5090; at least 23,000 MiB VRAM | HyperQwen, DFlash2, 7-token draft, fast variant | 65,536 | Public W4A16 model, head preparation, pinned fast variant and DFlash2 |
| ninfer / standard / Ada | RTX 4090 24 GB | ninfer-4090, E8 KV, MTP3 | 229,376 | Public preconverted artifact, publisher SHA256 verified |
| ninfer / standard / Blackwell | RTX 5090, including 24 GB Laptop GPU | Neroued NInfer, NVFP4 KV, MTP3 | 229,376 | Public BF16 model converted on CPU with the matching v3 converter |
| ninfer / uncensored | RTX 5090, including 24 GB Laptop GPU | Neroued NInfer, NVFP4 KV, MTP3 | 229,376 | User-authorized gated OrcaRouter checkpoint converted on CPU |

`-GpuArchitecture Auto` reads `nvidia-smi`: an RTX 5090 selects Blackwell for standard NInfer; otherwise the Ada plan is shown. When planning on a different/no-GPU machine, pass `-GpuArchitecture Ada` or `Blackwell` explicitly. Install and Start always validate the actual selected GPU. `-GpuIndex 0` is the default. RTX 3090 NInfer is unsupported by these two pinned builds. Laptop cards must meet the VRAM minimum; a lower-VRAM 4090 Laptop is rejected.

**vllm / uncensored is unsupported:** OrcaRouter distributes BF16 body weights. HyperQwen's head preparation does not turn that body into a model that fits 24 GB. The installer reports this limitation and never silently substitutes another checkpoint.

32 GiB system RAM is required. Keep at least 65 GiB free on the installation drive for vLLM, 30 GiB for Ada NInfer, or 110 GiB for either Blackwell conversion. Docker's Linux disk needs a separate 30–40 GiB reserve (exact requirement is in Plan output); Windows drive free space does not prove the Docker VHD has that capacity. CPU conversion and Docker compilation can take substantial time. Upstream context/speed reports are not validation of this packaging or every supported GPU.

## Prerequisites and actions

Install Docker Desktop with the WSL2 Linux engine, a current NVIDIA driver, and Git for Windows yourself. Review Docker's applicable license terms yourself. The script neither installs these dependencies nor accepts licenses. The selected Docker context must be local, using a named pipe or Unix socket. Enable Docker's access to the selected Windows drive. Permission or build errors stop the script; it does not elevate, change Windows execution policy, delete existing files, or stop other processes.

Run in an authorized PowerShell session (PowerShell 5.1 syntax is supported; PowerShell 7 is recommended). Replace the sample path with a dedicated absolute local directory. If policy blocks the script, use your organization's approved signing/execution workflow; these examples contain no policy bypass.

```powershell
# Read-only JSON. Does not need Docker and never downloads or creates files.
.\setup\backend-install.ps1 -Action Plan -Backend vllm -InstallRoot D:\QwenStatus

# Build image, download and prepare the standard model. No model server starts.
.\setup\backend-install.ps1 -Action Install -Backend vllm -InstallRoot D:\QwenStatus

# Explicit start and status check. First compile/load can take several minutes.
.\setup\backend-install.ps1 -Action Start -Backend vllm -InstallRoot D:\QwenStatus
.\setup\backend-install.ps1 -Action Verify -Backend vllm -InstallRoot D:\QwenStatus
.\setup\backend-install.ps1 -Action Stop -Backend vllm -InstallRoot D:\QwenStatus

# The same lifecycle selects the appropriate standard NInfer GPU profile.
.\setup\backend-install.ps1 -Action Plan -Backend ninfer -InstallRoot D:\QwenStatus
.\setup\backend-install.ps1 -Action Install -Backend ninfer -InstallRoot D:\QwenStatus
```

Plan and Verify emit JSON (`-Json` is also accepted). Other actions write progress and a completion message, with exit code 0 only after the requested operation succeeds. Any failed prerequisite or command exits nonzero. Verify reports `installed`, `prepared`, `imageId`, `running`, and `healthy`; a stopped installation is a valid Verify result (`running:false, healthy:false`). A running container whose `/health` check fails returns code 2. Start success means the container was created and its ownership recorded, not that model loading has finished. Controllers must separately inspect Verify readiness.

## Gated OrcaRouter model

1. Personally sign in and accept access at [the model page](https://huggingface.co/orcarouter/Qwen3.8-27B-Uncensored).
2. Authenticate using Hugging Face's supported user login flow. Supply an existing local token file through `-HfTokenFile`; do not paste a token into an AI conversation, command argument, installation JSON, issue, or release.
3. Plan and install the Blackwell profile, then start it separately:

```powershell
.\setup\backend-install.ps1 -Action Plan -Backend ninfer -Model uncensored -InstallRoot D:\QwenStatus
.\setup\backend-install.ps1 -Action Install -Backend ninfer -Model uncensored -InstallRoot D:\QwenStatus -HfTokenFile "$env:USERPROFILE\.cache\huggingface\token"
.\setup\backend-install.ps1 -Action Start -Backend ninfer -Model uncensored -InstallRoot D:\QwenStatus
```

The token file is mounted read-only only in the short-lived preparation container, never copied into an image or saved in ownership metadata. Access denial stays an error. The prepared model can subsequently run without mounting credentials. The public default requires no token or model access agreement. A failed conversion that left an artifact without a matching checksum is rejected rather than overwritten; inspect it manually or use a fresh dedicated installation directory.

## Ownership, network and state

Only `127.0.0.1:18021` is published. The API model ID is `qwen3.8-27b`; the shared queue should use `http://127.0.0.1:18021/v1`. Compose has `restart: "no"`, no prepare dependency, and vLLM `PREPARE=0`. Starting never downloads weights. The installer refuses to start if port 18021 or any labeled Qwen Status model backend is already running. It never kills an existing process, stops another installation, runs `docker compose down`, or prunes Docker resources.

Files live under `<InstallRoot>\backends\<backend>-<model>\`: `source`, `models`, `cache`, `setup`, `Dockerfile.pinned`, `compose.json`, and `ownership.json`. The marker binds the normalized installation path, backend, model, manifest hash, source commit, built image ID, and started container ID. Stop verifies that exact container ID and its ownership labels before issuing `docker stop`; it leaves images and models in place. A changed source manifest or switched GPU architecture requires a separate install root rather than silently replacing the existing profile. Do not manually repurpose the owned directories or containers.

NInfer exposes llama.cpp-style metrics; clients that expect vLLM names must map `llamacpp:prompt_tokens_total`, `llamacpp:tokens_predicted_total`, `llamacpp:requests_processing`, and `llamacpp:requests_deferred` to the corresponding vLLM token/running/waiting counters. NInfer token counting uses `/v1/messages/count_tokens`; a vLLM `/tokenize` client requires its own adapter. The backend installer does not insert a second proxy port.

## Pins and verification limits

The machine-readable source of truth is [backends.json](backends.json). Source pins were checked through the GitHub commit API; model revisions through Hugging Face metadata; base image digests through the Docker Registry manifest API on 2026-09-24.

- [HyperQwen source](https://github.com/syv-ai/HyperQwen/tree/231592a7009d0d7c876189f7d1f72d8cc791c76c): `docker/entrypoint.sh`, `docker/prepare.sh`, `prepare/fetch_fast_variant.py`, `prepare/fetch_dflash2.py`, and `single-user/start_qwen.sh` establish the prepare/single interfaces. A wrapper supplies explicit HF revisions to the two upstream download functions.
- [Ada NInfer source](https://github.com/sergiuszm/ninfer-4090/tree/1bd56c9a1bdf457c6188391a9385d44d86e953aa): sm_89, preconverted artifact, E8 KV. Its older converter is deliberately not used for OrcaRouter.
- [Blackwell NInfer source](https://github.com/Neroued/ninfer/tree/9e163eee4b8acec21ab0ac765107b6a3f287b217): sm_120a, v3 `python -m tools.convert --recipe qwen3_8_27b --components text,mtp --proposal`. Its Dockerfile receives the GeForce CUDA forward-compat library removal documented by the Ada fork.

Source/model/base-image pins are reproducible inputs, not a bit-identical build promise: apt packages and transitive Python dependencies are not fully locked. Locally converted model checksums attest to the produced local files, not an independent publisher hash. No image build, model download, conversion, GPU launch, context-length run, or inference benchmark has been executed to validate this package. `backend-tests.ps1` checks script parsing and read-only plan contracts without executing Docker or downloading weights.
