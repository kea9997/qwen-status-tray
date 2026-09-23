# Isolated Hermes installation

`hermes-install.ps1` installs the official Nous Research Hermes Python source and its **core + messaging** dependencies. It prepares model `qwen3.8-27b` at `http://127.0.0.1:18022/v1`. It does not include Hermes Desktop, Node/browser automation, voice models, the Qwen model, or the Qwen queue. Those components have separate installers and lifecycles.

## Windows

Run these commands from an ordinary PowerShell session. Replace both paths with your own absolute paths; do not add `-ExecutionPolicy Bypass`. If organizational policy blocks the script or a downloaded executable, obtain an approved installation route from your administrator.

```powershell
& 'D:\QwenStatus\setup\hermes-install.ps1' -Action Plan -InstallRoot 'D:\QwenSuite'
& 'D:\QwenStatus\setup\hermes-install.ps1' -Action Install -InstallRoot 'D:\QwenSuite'
& 'D:\QwenStatus\setup\hermes-install.ps1' -Action Verify -InstallRoot 'D:\QwenSuite'
```

`Plan` is offline and read-only. `Install` downloads and executes the checksum-pinned official uv and PortableGit binaries, downloads official Hermes source at a pinned commit, installs private Python 3.11 through uv, and syncs the upstream `uv.lock` with the `messaging` extra. Package installation executes upstream build code with the current user's permissions. It does not request administrator rights or install a service. Network access to GitHub, Python's uv-managed distribution, and upstream package indexes is required.

Everything created by the installer belongs under `InstallRoot\hermes`. An existing `hermes` directory is refused, even after a partial installation. Examine `hermes\install.log` and `hermes\install-result.json` after failure; choose a fresh installation root for a retry or remove only the reviewed partial directory yourself. The installer never deletes or repairs an existing installation. Python/venv paths are tied to their installation location; reinstall at a new root instead of moving the directory.

No shell profile, user PATH, registry registration, existing Hermes data, server, scheduled task, or login credential is changed. Download/build caches and temporary files are directed into the installation. Environment adjustments exist only for the installer process and are restored on return.

## Runtime contract

Successful installation writes `hermes\install-result.json` with `status: "installed"`, the source revision, a source-archive SHA256 receipt, and these absolute paths in its `environment` object:

| Environment variable | Relative to InstallRoot |
| --- | --- |
| `QWEN_HERMES_PYTHON` | `hermes\venv\Scripts\python.exe` |
| `QWEN_HERMES_ROOT` | `hermes\source` |
| `QWEN_HERMES_HOME` | `hermes\profiles\qwen` |
| `QWEN_HERMES_GIT_BASH` | `hermes\git\bin\bash.exe` |

The gateway must pass these paths explicitly. Map HOME to `HERMES_HOME` and GIT_BASH to `HERMES_GIT_BASH_PATH`, set the process working directory to ROOT, and invoke PYTHON with arguments `-m`, `hermes_cli.main`. Use an argument array with shell execution disabled. Clear inherited `HERMES_PROFILE`, `HERMES_CONFIG`, and `HERMES_ENV` so another installation cannot override the dedicated home. Do not pass `--profile` pointing elsewhere. The `profiles\qwen` layout prevents Hermes CLI from selecting an unrelated sticky active profile.

For a manual interactive CLI session, use a fresh PowerShell window:

```powershell
$record = Get-Content -LiteralPath 'D:\QwenSuite\hermes\install-result.json' -Raw | ConvertFrom-Json
if ($record.status -ne 'installed') { throw 'Installation is incomplete.' }
$env:HERMES_HOME = $record.environment.QWEN_HERMES_HOME
$env:HERMES_GIT_BASH_PATH = $record.environment.QWEN_HERMES_GIT_BASH
$env:HERMES_PROFILE = $null
$env:HERMES_CONFIG = $null
$env:HERMES_ENV = $null
Set-Location -LiteralPath $record.environment.QWEN_HERMES_ROOT
& $record.environment.QWEN_HERMES_PYTHON -m hermes_cli.main
```

The generated profile specifies the local queue explicitly for the main model, auxiliary tasks, and delegation, with a public, nonsecret API-key placeholder. Unsupported local-model capabilities require the user's own later configuration. No account, token, history, or configuration is copied from the distributing computer. Never package the resulting `hermes\profiles`, downloaded caches, logs, or installation result when redistributing the application.

## Gates and manual steps

1. Start the Qwen queue separately when you choose. To check its advertised model without sending a prompt, run `Verify` with `-CheckEndpoint`. It only GETs `/v1/models` and never starts a server or sends an inference request. WSL must be able to reach that same loopback endpoint.
2. Open the CLI yourself and review any first-run terms, telemetry choices, or permission prompts. The installer records no implicit agreement and does not launch Hermes to answer prompts.
3. Configure external providers, tools, or accounts yourself if needed. Their private settings belong only in the installed dedicated profile.
4. For Telegram, obtain and enter your own bot token and allowed-user settings using the same dedicated environment and the CLI arguments `gateway setup`. Start `gateway run` only after reviewing that configuration. The installer includes messaging dependencies but neither configures nor starts Telegram.

`Verify` checks source/venv/bash/config/lockfile presence and installed package metadata using Python's standard library. It does not import Hermes, read credential contents, execute a model request, or prove full runtime health. It exits 2 for incomplete installation, or when explicitly requested endpoint verification fails. Runtime and inference success remain separate gates.

## WSL / Linux

The same script can be run **inside WSL using an already installed PowerShell 7**, as an ordinary Linux user, with a new absolute Linux path:

```powershell
& '/mnt/d/QwenStatus/setup/hermes-install.ps1' -Action Plan -InstallRoot '/home/alice/qwen-suite'
& '/mnt/d/QwenStatus/setup/hermes-install.ps1' -Action Install -InstallRoot '/home/alice/qwen-suite'
```

This route needs `/bin/bash` and `tar`; it never installs WSL, PowerShell, distribution packages, or systemd services. Python becomes `hermes/venv/bin/python`, and the bash contract is `/bin/bash`. Prefer the Linux filesystem for the venv. The Windows gateway currently accepts native Windows Python `.exe` paths, so the WSL variant is for a manually launched Linux CLI and is **not integrated into that Windows gateway**.

For a Windows queue, `127.0.0.1` from WSL only works when networking is configured to make the Windows loopback endpoint reachable, such as a suitable mirrored-networking setup. The installer does not change WSL/network/firewall settings or expose the queue on the LAN. Use native Windows installation for the bundled Windows gateway.

## Pinned inputs and validation status

- Hermes official commit: `62ac203abe6373a3e54cb78a851326b099a88f14`.
- uv `0.12.18`; Git for Windows PortableGit `2.55.0.5`. The script verifies the SHA256 release-asset digests recorded from their official GitHub release metadata for x64 and ARM64.
- Python request: `3.11`, resolved by that pinned uv release; the actual installed runtime remains inspectable inside `hermes/python`. Hermes dependencies use the pinned source's frozen `uv.lock`; no smaller dependency fallback silently replaces the requested messaging install.
- The source archive is addressed by immutable commit, obtained over HTTPS, and its downloaded SHA256 is recorded as a receipt. This is not an independently preverified source-archive checksum.
- Development validation is limited to PowerShell 5.1/7 AST/static checks and a read-only PowerShell 7 Plan check. The distributing computer's Windows PowerShell 5.1 execution policy blocked `-File`; that policy was not bypassed or changed. A full download/install or Python/bootstrap execution test was intentionally not performed. The recipient's installation logs and subsequent verification are required acceptance evidence.

Official references: [Hermes installation](https://hermes-agent.nousresearch.com/docs/getting-started/installation), [native Windows guide](https://hermes-agent.nousresearch.com/docs/user-guide/windows-native), [Hermes source at the pinned revision](https://github.com/NousResearch/hermes-agent/tree/62ac203abe6373a3e54cb78a851326b099a88f14), [uv CLI reference](https://docs.astral.sh/uv/reference/cli/), [uv release](https://github.com/astral-sh/uv/releases/tag/0.12.18), [PortableGit release](https://github.com/git-for-windows/git/releases/tag/v2.55.0.windows.5).
