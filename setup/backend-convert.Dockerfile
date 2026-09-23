FROM python:3.12-slim-bookworm@sha256:392307d22300de8b5986851a12d9176dfc0fc073e65bf6523ebd7dcbeb23564e
RUN pip install --no-cache-dir torch==2.8.0 --index-url https://download.pytorch.org/whl/cpu \
    && pip install --no-cache-dir numpy==2.2.6 safetensors==0.6.2 huggingface_hub==0.34.4
WORKDIR /source
ENTRYPOINT ["python", "/setup/backend-prepare.py"]
