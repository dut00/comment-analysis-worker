# Running Basal on Windows

How the author runs the decision model (Basal 2.0 mini) for Development and compose on a Windows laptop with integrated graphics, so on the CPU.

## Preparing the environment

```sh
uv venv --python 3.12
source .venv/Scripts/activate
# uv pip install torch==2.11.0 --index-url https://download.pytorch.org/whl/cu128 # CUDA (an NVIDIA GPU)
uv pip install torch==2.11.0 --index-url https://download.pytorch.org/whl/cpu
uv pip install "basal[gguf] @ https://github.com/rkinas/basal/archive/refs/tags/v2.2.0.tar.gz"
```

In PowerShell, activate the environment with `.venv\Scripts\Activate.ps1`.

## Downloading the model

The model downloaded with LM Studio is incomplete: the folder has only `basal-2.0-mini-Q8_0.gguf` and `generation_config.json`. Download the remaining files from [Remek/basal-2.0-mini-GGUF](https://huggingface.co/Remek/basal-2.0-mini-GGUF/tree/main) into the same folder (`C:\Users\Kuba\.lmstudio\models\Remek\basal-2.0-mini-GGUF`):

- `basal.json`, `CALIBRATION.json`, `CALIBRATION.llamacpp.json`, `CALIBRATION.ollama.json`
- `tokenizer.json`, `tokenizer_config.json`, `chat_template.jinja`
- `Modelfile.Q8_0`, `README.md`, `SHA256SUMS`

The Q5_K_M build (`basal-2.0-mini-Q5_K_M.gguf`, `Modelfile.Q5_K_M`) is not needed.

## Running the model

```powershell
basal-serve --model "C:\Users\Kuba\.lmstudio\models\Remek\basal-2.0-mini-GGUF" --mode gguf --gguf "C:\Users\Kuba\.lmstudio\models\Remek\basal-2.0-mini-GGUF\basal-2.0-mini-Q8_0.gguf" --name basal-2.0-mini --port 1234
```

- `--name basal-2.0-mini` is required: the server reports this name in every response, and the worker treats any other name as another model (the comments wait). Without it, `basal-serve` on Windows reports the whole `--model` path.
- See the README ("Decision model") for how to check the server, the expected latency and the limits.
