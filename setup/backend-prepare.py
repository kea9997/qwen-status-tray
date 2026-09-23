"""Container-only preparation. Does not start a server; all model revisions are pinned."""
import hashlib
import json
import os
from pathlib import Path
import runpy
import subprocess
import sys
import urllib.request


def validate_safetensors_model(directory):
    directory = Path(directory)
    for name in ("config.json", "tokenizer.json", "tokenizer_config.json", "model.safetensors.index.json"):
        if not (directory / name).is_file():
            raise RuntimeError(f"Prepared model is missing {name}")
    weight_map = json.loads((directory / "model.safetensors.index.json").read_text())["weight_map"]
    for name in set(weight_map.values()):
        candidate = (directory / name).resolve()
        if not candidate.is_relative_to(directory.resolve()) or not candidate.is_file():
            raise RuntimeError(f"Invalid or absent model shard: {name}")
    return weight_map


def prepare_vllm(config):
    import huggingface_hub

    os.chdir("/app")
    pins = {config[key]["repository"]: config[key]["revision"] for key in ("model", "fastVariant", "drafter")}
    original = huggingface_hub.snapshot_download

    def pinned_download(repo_id, *args, **kwargs):
        if repo_id not in pins:
            raise RuntimeError(f"Refusing unpinned model download: {repo_id}")
        kwargs["revision"] = pins[repo_id]
        return original(repo_id, *args, **kwargs)

    huggingface_hub.snapshot_download = pinned_download
    base = Path("/app/models/Qwen3.8-27B-W4A16-AutoRound")
    complete = base / ".qwenstatus-source-complete"
    base.mkdir(parents=True, exist_ok=True)
    # Do not overwrite in-place requantized files when resuming a completed download.
    if not complete.is_file():
        pinned_download(config["model"]["repository"], local_dir=str(base))
        validate_safetensors_model(base)
        complete.write_text(config["model"]["revision"] + "\n")
    if complete.read_text().strip() != config["model"]["revision"]:
        raise RuntimeError("Existing model revision differs; use a separate installation root.")
    weights = validate_safetensors_model(base)
    steps = [
        ("lm_head.weight_packed" not in weights, "prepare/quant_lm_head.py", []),
        (not any(k.endswith("embed_tokens.weight_packed") for k in weights), "prepare/quant_embed.py", []),
        ("mtp.layers.0.mlp.down_proj.weight_packed" not in weights, "prepare/quant_mtp.py", []),
        ("mtp.draft_lm_head.weight_packed" not in weights or not (base / "mtp_draft_vocab_ids.pt").is_file(),
         "prepare/build_draft_vocab.py", ["--ids", "prepare/draft_vocab_ids.json"]),
    ]
    for needed, script, extra in steps:
        if needed:
            subprocess.run([sys.executable, script, str(base), *extra], check=True)
    # runpy keeps the revision-enforcing snapshot_download wrapper active.
    fast = str(base) + "-fast"
    drafter = "/app/models/Qwen3.8-27B-DFlash2-W4A16"
    for script, args in (("prepare/fetch_fast_variant.py", [str(base), fast]),
                         ("prepare/fetch_dflash2.py", [drafter])):
        sys.argv = [script, *args]
        runpy.run_path(script, run_name="__main__")
    validate_safetensors_model(fast)
    for name in ("config.json", "model.safetensors"):
        if not (Path(drafter) / name).is_file():
            raise RuntimeError(f"DFlash2 download incomplete: {name}")
    # --no-server still requires a CUDA device. Preparation deliberately has no GPU.
    # The serving entrypoint performs full GPU/model verification at explicit Start.
    subprocess.run(["bash", "verify.sh", "--install"], check=True)


def digest_file(path):
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(8 * 1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def prepare_ninfer(config, model_directory=Path("/models")):
    model = config["model"]
    target = Path(model_directory) / model["filename"]
    expected = model["sha256"]
    if target.exists():
        if digest_file(target) != expected:
            raise RuntimeError("Existing model checksum differs; refusing to replace it.")
        return
    partial = target.with_suffix(target.suffix + ".partial")
    if partial.exists() and digest_file(partial) == expected:
        partial.replace(target)
        return
    offset = partial.stat().st_size if partial.exists() else 0
    url = f'https://huggingface.co/{model["repository"]}/resolve/{model["revision"]}/{model["filename"]}'
    request = urllib.request.Request(url, headers={"Range": f"bytes={offset}-"} if offset else {})
    with urllib.request.urlopen(request, timeout=120) as response:
        append = offset > 0 and response.status == 206
        if append and not response.headers.get("Content-Range", "").startswith(f"bytes {offset}-"):
            raise RuntimeError("Download server returned an unexpected byte range.")
        with partial.open("ab" if append else "wb") as output:
            while True:
                chunk = response.read(8 * 1024 * 1024)
                if not chunk:
                    break
                output.write(chunk)
    if digest_file(partial) != expected:
        raise RuntimeError("Downloaded model SHA256 does not match the pinned publisher checksum.")
    partial.replace(target)


def prepare_converted(config, gated):
    from huggingface_hub import HfApi, snapshot_download

    token = False
    if gated:
        token_file = Path("/run/secrets/hf_token")
        if not token_file.is_file():
            raise RuntimeError("Hugging Face authentication is required. Accept the model terms yourself and provide -HfTokenFile.")
        token = token_file.read_text().strip()
        if not token:
            raise RuntimeError("The supplied Hugging Face token file is empty.")
        # Authentication/access is explicit; credentials never enter the image or metadata.
        HfApi().whoami(token=token)
    model = config["model"]
    source = Path("/models/source-checkpoint")
    snapshot_download(model["repository"], revision=model["revision"], local_dir=str(source), token=token)
    del token
    validate_safetensors_model(source)
    template = source / "chat_template.jinja"
    if not template.is_file():
        raise RuntimeError("The selected checkpoint has no chat template.")
    output = Path("/models") / model["filename"]
    checksum = Path(str(output) + ".sha256")
    if output.exists():
        if checksum.is_file():
            entries = checksum.read_text().splitlines()
            verified = bool(entries)
            names = []
            for entry in entries:
                expected, separator, filename = entry.partition("  ")
                part = (output.parent / filename).resolve()
                names.append(filename)
                verified = verified and bool(separator) and part.is_relative_to(output.parent.resolve()) and part.is_file()
                if not verified or digest_file(part) != expected:
                    verified = False
                    break
            if verified and output.name in names:
                return
        raise RuntimeError("Conversion output exists without a matching checksum. Inspect it manually; no existing artifact is overwritten.")
    os.chdir("/source")
    subprocess.run([sys.executable, "-m", "tools.convert", "--model", str(source),
                    "--recipe", "qwen3_8_27b", "--components", "text,mtp", "--proposal",
                    "--resource", f"chat_template.jinja={template}",
                    "--name", ("qwen3.8-27b-orcarouter-uncensored" if gated else "qwen3.8-27b"), "--device", "cpu",
                    "--out", str(output)], check=True)
    if not output.is_file() or not Path(str(output) + ".conversion.json").is_file():
        raise RuntimeError("Converter did not produce its required artifact and report.")
    report = json.loads(Path(str(output) + ".conversion.json").read_text())
    checksums = []
    for item in report["files"]:
        part = Path(item["path"]).resolve()
        if not part.is_relative_to(output.parent.resolve()) or not part.is_file():
            raise RuntimeError("Converter report points to an invalid or missing artifact part.")
        checksums.append(digest_file(part) + "  " + part.name)
    checksum.write_text("\n".join(checksums) + "\n")


def main():
    if len(sys.argv) != 2 or sys.argv[1] not in ("vllm", "ninfer", "ninfer-uncensored", "ninfer-blackwell"):
        raise SystemExit("Usage: backend-prepare.py vllm|ninfer|ninfer-uncensored|ninfer-blackwell (inside the preparation container)")
    backend = sys.argv[1]
    manifest = json.loads(Path("/setup/backends.json").read_text(encoding="utf-8-sig"))
    if backend == "ninfer-uncensored":
        prepare_converted(manifest["ninferUncensored"], gated=True)
    elif backend == "ninfer-blackwell":
        prepare_converted(manifest["ninferBlackwell"], gated=False)
    else:
        (prepare_vllm if backend == "vllm" else prepare_ninfer)(manifest[backend])
    print(f"{backend}: model preparation complete; no server was started.", flush=True)


if __name__ == "__main__":
    main()
