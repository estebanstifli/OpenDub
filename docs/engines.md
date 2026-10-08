# Engine providers

Core exposes interchangeable interfaces for transcription, alignment, diarization, translation, TTS and separation. Engine selection uses persistent IDs and real capability metadata; segment TTS overrides speaker preference, which overrides project selection. Two tone-based mock TTS engines exercise substitution.

## Pinned implementations

- **audio.cpp 0.9.0** — commit `795c45fbde0a7d29c93b22199728ff5caaec02e5`.
- **llama.cpp v0.6.0 / b11429** — commit `d81235049384534c167caea52b85a694f6103d14`.
- Native release asset URLs and hashes are in `runtimes/versions.json`.
- Model revisions, sizes and SHA-256 values are in `engine-catalog/`. Their licenses and sources are displayed in Models.

audio.cpp launches on an allocated loopback port with lazy loading and a maximum of one resident model. Transcription/diarization use `/v1/audio/transcriptions/details`; alignment uses `/v1/tasks/run`; TTS uses `/v1/audio/speech`. Timestamps in sample units are converted to TimeSpan. BS-RoFormer uses the verified CLI separation task and named WAV outputs. A shared runtime coordinator serializes accelerator jobs and awaits release of the previous provider's model memory before switching. audio.cpp uses its unload endpoint and keeps the idle server alive; llama.cpp stops its owned server. Successful UI operations retain model residency across manual requests, saving, playback and navigation. Switching providers, failure, cancellation, explicit Unload models, or application exit releases it. The one-resident-model policy reserves VRAM for the next engine.

llama-server uses structured chat-completion JSON. The adapter checks segment ID, nonempty text and target language, retries malformed responses/server errors, and sends neighboring dialogue, glossary and target duration. The UI selects an installed local GGUF explicitly.

## Transcription and alignment

Import transcribes 60-second audio blocks with Qwen3-ASR, explicitly setting `audio_chunk_mode=fixed`, `audio_chunk_seconds=60`, and `max_tokens=2048` in the server request. The final block uses the remaining duration. Each transcript is paired with exactly the same audio range for forced alignment; `clamp_timestamps_to_audio=true` and an application-side bounds check keep word timestamps inside that range. These analysis blocks are independent of the final dialogue segments, which are still grouped by speaker, pauses and sentence punctuation, then normalized. Transcription and alignment cache versions change with this policy; existing projects are not rebuilt merely by opening them. A native CUDA smoke test covers 60 + 5 seconds of actual speech and alignment across the block boundary.

## TTS capabilities

Choose **Ollama (local)** as the translation engine to list installed local chat models from `127.0.0.1:11434`. Select a model in the dropdown; the selection is saved per project and also used for duration-driven rewrites. Refresh rescans the installation. Embedding-only models and cloud-backed aliases are excluded. Ollama must already be running; the app never downloads a model or stops the Ollama service. Requests use structured JSON and keep the chosen model resident between operations (`keep_alive=-1`). When another provider needs the accelerator, or on explicit unload / application exit, the app unloads only the Ollama model it used with `keep_alive=0`. After an abnormal application termination, Ollama may retain the model until it is manually unloaded or evicted. The service stays running. API contracts: [list models](https://docs.ollama.com/api/tags), [chat](https://docs.ollama.com/api/chat).

IndexTTS 2.5 uses an identity reference (`voice_ref`) and a separate emotional audio reference (`audio`). The adapter clips the requested ranges before submitting them. Emotion alpha, labels, text instructions, eight-value vectors and seed are mapped in the provider. Native speaking rate is exposed through the Speed control (0.5–2.0): the adapter sends options.duration_factor = 1 / speed. The typed Speed control takes precedence over an advanced duration_factor. No top-level speed is sent to the server for IndexTTS. Absolute target seconds are not a native request field.

**Use each source segment as voice reference** is enabled by default. Together with **Transfer source emotion**, it sends the original segment to both reference inputs while synthesizing the translated text in the target language. The source is the separated vocals when available, otherwise the original master audio (analysis audio is a fallback). Automatic emotion strength is 1.0; explicit segment emotion controls still override automation. Disable the source voice option to use the fixed speaker reference, which can help with very short, noisy or overlapping segments. Voice identity, crying and other delivery traits are conditioning targets, not guarantees of exact reproduction.

An imported or manually chosen speaker reference takes precedence over that source-voice default. Its identity audio and the source emotion audio remain separate. Automatic speaker references are marked as such and continue to follow the project setting. For legacy projects, references without a stored fingerprint are recognized as manual (the older import workflow did not store one); new projects store the origin explicitly. Activity identifies the selected voice reference and emotion mode for each segment. After upgrading, regenerate previously affected segments once.

The pipeline preserves locked translations. Engines with native speaking-rate support, including IndexTTS, adjust synthesis speed without rewriting text. Engines without it can rewrite unlocked text. Only a configured small atempo correction is allowed afterward. The renderer checks duration, dirty state and output hashes.

## Model installation

Short-reference protection: IndexTTS/audio.cpp 0.9.0 was observed aborting inside GGML on an 80 ms reference. Source references now include at least one second of surrounding audio where the source length permits; segment timing is unchanged. Explicit references shorter than one second are rejected locally as a conservative safety threshold. Runtime connection failures include available native exit diagnostics. `Exclude from dubbing (music / non-speech)` keeps a segment in the project while omitting translation, TTS, mixing and subtitles. `Exclude segments without speakers` is a manual bulk action, not an automatic claim that unassigned segments contain no speech; review exclusions and re-enable any real dialogue.

Official audio.cpp Windows portable binaries do not include a native model manager. The default manager therefore streams the pinned HTTPS file manifest, verifies size/SHA-256 and promotes each successful file atomically. Interrupted files are removed; already verified files are reused. If a native manager path is configured, it receives pinned provider specs in `runtime-specs/`. Models can also come from another HTTPS provider by extending the catalog/provider.

Qwen transcription is requested in 15-second audio chunks. Each transcript retains its original chunk boundaries; forced alignment receives only that chunk and its matching text, and word times are shifted back to the video timeline. This avoids exceeding the shared Qwen audio encoder's position limit on long recordings. Temporary clips are removed on completion, failure or cancellation. The pipeline uses a new cache key so older whole-recording transcripts are reprocessed automatically.

Default tests use compact response fixtures and generated audio and never download weights. An opt-in long-recording inference test uses an already running server through `DUBBING_SMOKE_ENDPOINT` and a local WAV through `DUBBING_SMOKE_AUDIO`, with `FFMPEG_TESTS=1`. Broader real-model acceptance is still required.

Primary implementation references: [audio.cpp 0.9.0](https://github.com/0xShug0/audio.cpp/tree/v0.9.0), [server runtime](https://github.com/0xShug0/audio.cpp/blob/v0.9.0/app/server/runtime.cpp), [IndexTTS request options](https://github.com/0xShug0/audio.cpp/blob/v0.9.0/src/models/index_tts2/request.cpp), [llama server](https://github.com/ggml-org/llama.cpp/tree/b11429/tools/server).

## Batched TTS and GPU diagnostics

Generation groups segments by TTS engine. With IndexTTS/native speed fitting, each selected segment completes initial synthesis, any required retries, permitted final stretch and project checkpoint before the next starts. The individual Regenerate and fit action uses this same path. Selection and overwrite rules still apply. Already fitting segments do not repeat inference, and small deviations within Maximum time stretch go straight to FFmpeg even if the stricter duration tolerance is exceeded.

When the selection includes an engine without native speed whose unlocked translations may need rewriting, the operation retains grouped initial TTS, translation adjustments and corrected TTS passes. This avoids alternating LLM and TTS residency for each segment. Retry limits still apply. Mixed voice-engine overrides require switches between engine groups.

Source/reference file fingerprints are reused within an operation, keyed by full path, size and last modification time; a new operation recalculates them. Reference cuts are cached in the operation's scratch directory, keyed by source identity/metadata/fingerprint, range and model family. Voice and emotion can share the same cut; retries reuse it. Cancellation/failure removes scratch references along with candidates. Logs show reference preparation, initial generation, retry durations, final adjustment, per-segment total and cache reuse. TTS metrics now include fitting time instead of only initial synthesis.

The optional Windows CUDA 13.3 runtime and CUDA dependencies are pinned in `runtimes/versions.json`. Build with `scripts/package.ps1 -AudioCuda`; CPU binaries remain available alongside `audio-cuda`. With Backend=auto and an NVIDIA GPU detected by nvidia-smi, audio providers select that CUDA build. Explicit executable paths override auto-discovery; Backend=cpu selects the CPU build. The old CPU-only package could not accelerate audio.cpp even when auto was selected.

Activity reports selected backend/device/threads, NVIDIA memory information, native CUDA initialization, first load versus resident reuse, provider switches, HTTP elapsed time and explicit unload. CLI version probes are cached and do not appear as inference-server restarts. Native legacy-model-spec warnings mean the pinned runtime supplies the current validation schema for older GGUF metadata; they do not by themselves indicate an inference failure.
# IndexTTS output language

IndexTTS2.5 uses the dubbing target language explicitly for every synthesis and duration retry. The audio.cpp v0.9.0 speech endpoint requires `options.language` (for example `{"options":{"language":"es"}}`): its IndexTTS parser does not read the top-level transcript language. Supported dubbing codes are `zh`, `en`, `ja`, `es`, `ar`; uppercase and regional forms are normalized. Advanced `lang`/`language` overrides cannot replace the dubbing target. Automatic or unsupported languages fail before inference. The selected language appears in progress logs.

The adapter cache version includes `explicit-language-v1`, so regeneration cannot reuse speech cached before this fix. Existing audio is preserved until regenerated.

Manual **Regenerate TTS** synthesizes the current translation without invoking duration fitting or a translation engine. It leaves DurationFit dirty for later adjustment. **Regenerate and fit** explicitly enables the existing duration rewrite/retry flow. Full project generation continues to include duration fitting. Logs show actual versus target duration and whether a rewrite changed the text.

IndexTTS native duration control was verified against the installed CUDA runtime using identical Spanish text, reference and seed: speed 1.0 produced 3.831 s; speed 1.25 (duration_factor 0.8) produced 3.065 s. This is a relative multiplier, not an exact-seconds or lip-sync guarantee. Automatic fitting retains its bounded speed range (0.75–1.3), retry budget and duration-tolerance checks. Regenerate TTS uses the chosen Speed without automatic fitting; Regenerate and fit measures and retries native synthesis.

Ollama translation reliability: the response schema constrains segmentId and language to the exact requested values and requires nonempty text. Responses are still validated locally; invalid output receives explicit corrective feedback in the bounded three-attempt conversation. Logs distinguish metadata mismatch, empty content, malformed JSON and output-token truncation. Translation and full generation checkpoint each completed segment to the current project directory (or keep it in the session for unsaved projects). A later failure/cancellation leaves previous translations available; retry skips completed clean segments.
