# GPU validation — October 2026

The README's hardware figures are recommendations for the pinned default models. These checks establish runtime/device compatibility on the machines below, not a universal memory minimum or a voice-quality benchmark.

## Windows and Linux CUDA

Development hardware: NVIDIA GeForce RTX 3070, 8 GiB VRAM. Linux checks run in Ubuntu 24.04 under WSL2, using the host NVIDIA driver. Models are the same pinned GGUF files used by OpenDub.

- Windows audio.cpp 0.9.0 enumerates `CUDA:0` on the RTX 3070.
- Linux llama.cpp b11429 translates with Qwen3 4B Q4_K_M and offloads **37/37 layers** to CUDA. With an 8192-token context, its log reports 2375.91 MiB of model buffers, 1152 MiB of KV cache and 85.01 MiB of CUDA compute buffers. A short translation took about 0.25 seconds after loading; cold loading is additional.
- The upstream Linux CUDA audio.cpp 0.9.0 server generated two short Spanish samples with IndexTTS2.5 Q8. The first request, including loading, took 38.75 seconds; the second reused the resident model and took 1.59 seconds. Output lengths were 3.83 and 4.67 seconds. Total GPU memory sampled every 0.5 seconds ranged from 1153 to 6275 MiB, **including the Windows desktop**. This is not a guaranteed peak for longer inputs.
- The server and CLI built by OpenDub's Dockerfile also passed using only the bundled CUDA libraries and the host driver. BS-RoFormer separated an 8-second stereo sample into vocals and instrumental in 7.48 seconds. IndexTTS generated two Spanish samples in 45.44 seconds (including loading) and 1.58 seconds (resident model), producing 4.03 and 4.31 seconds of audio. GPU memory peaked at 6394 MiB and fell to 1550 MiB after unloading models while keeping the server alive; the desktop baseline was 1292 MiB. CUDA context/library memory is additional to model memory.
- With a 20-second mono 16 kHz input, the same server completed Qwen3 ASR, Qwen3 forced alignment (47 words) and Nemotron diarization. These are execution checks, not an evaluation of transcription or speaker accuracy.
- The pinned Vulkan runtime starts in WSL, but this WSL environment exposes no Vulkan GPU. AMD/Intel Vulkan inference remains unverified on physical hardware.

OpenDub's Linux CUDA package builds both the server and the source-separation CLI from the same audio.cpp revision. It ships CUDA runtime libraries; the host must supply a compatible NVIDIA driver. The official Colab archive alone is insufficient for packaging because it contains no separation CLI and assumes external CUDA libraries.

## macOS Metal

The native macOS 14 ARM64 packaging job successfully builds and starts the application, bundled VLC and inference runtimes. audio.cpp reports `backends: cpu,metal` and enumerates `MTL:0`. llama.cpp enumerates `MTL0`. The hosted GPU is an **Apple Paravirtual device**, not a physical-Mac inference benchmark.

The first device probes took approximately 20 seconds for audio.cpp and 27 seconds for llama.cpp while initializing embedded shaders. OpenDub allows 60 seconds for Metal probing, recognizes the actual registry/device names, and passes the enumerated llama.cpp device identifier when starting translation. A compiled backend alone is not accepted as proof of an available GPU.

Full IndexTTS inference, voice quality and the recommended unified-memory configurations still need validation on physical Apple Silicon hardware. CPU fallback remains available when Auto finds no usable GPU; explicitly requested unavailable accelerators report an error.

## Reproducing release checks

`package.yml` runs tests, builds native packages and invokes each packaged application's `--self-check`. It also reports macOS GPU devices. Linux CUDA is built with `scripts/audio-linux-cuda.Dockerfile`; unchanged native artifacts may be reused through `cuda_run_id`. See [packaging](packaging.md) for the pinned runtime layout and library-loading rules.

Release notes and `build-info.json` identify the source revision of the downloadable binaries. Consult the release's successful packaging run when checking a particular build.
