# Implementation status — 2026-10-06

Audio track preparation and shared mixing: import/configuration lists every audio stream with metadata and sample audition. A selected mixed/dialogue source feeds analysis; clean background/music/effects tracks are preserved independently, or BS-RoFormer estimates vocals plus background. Preparing audio preserves existing segments, translations and generated speech. Play dub/context and export share mix construction, with original-dialogue mute by default and gain/mute/temporary solo controls. No original mixed-audio fallback. Native multitrack tests cover six-channel backgrounds, ignored alternate mixes and preview/export consistency; UI tests cover roles and cancellation. Full suite: 83 passed, 3 installed-model opt-ins skipped; affected track/shell tests rerun after final adjustments (6 passed). A real eight-second Blade Runner excerpt produced vocals/instrumental through BS-RoFormer CUDA. Package: `artifacts/releases/DubbingApp-win-x64-audio-tracks-mix`. See [audio tracks](audio-tracks.md) for behavior and separation limits. This supersedes the voices-only preview behavior below.

Synchronized Play dub: builds a content-keyed video preview with all available generated segments on the original timeline and no source audio. One media clock handles video/audio seeking and pause/resume. FFmpeg regression verifies silent gaps (including missing/excluded segments), two correctly placed voices, unchanged video codec, cache reuse and invalidation after a WAV overwrite. Validation: 80 tests passed, including native playback tests; 3 installed-model opt-ins skipped.

Ollama validation/checkpoint update: exact ID/language schema constraints, corrective retry feedback and specific diagnostics; incremental translation checkpoints prevent a last-segment failure from discarding earlier work. Validation: 75 tests passed with media checks (7 opt-ins skipped), plus a real local qwen3:8b English-to-Spanish request with a long segment ID. Regression tests simulate failure/cancellation on segment 14 and verify only that segment is requested on resume. The original failing segment text was not available for exact reproduction.

IndexTTS native speed update: supportsSpeakingRate is enabled and mapped to options.duration_factor = 1 / speed; automatic fitting preserves translation and keeps the TTS model resident. Validation: 70 tests passed with media checks (7 opt-in tests skipped), plus the installed CUDA test with identical text/reference/seed at speeds 1.0 and 1.25 (3.831 s / 3.065 s).

All phases in the original plan have an implementation. This records implementation and validation separately; it does not claim that the real-model acceptance criteria have been verified.

Manual regeneration and residency: Regenerate TTS now preserves the current translation and skips duration fitting; Regenerate and fit keeps the explicit rewrite/retry workflow. Successful UI operations retain the active model between requests. Provider switches, cancellation/failure, Settings > Unload models and normal application close release memory. Ollama uses indefinite keep-alive with explicit unload. Validation: 68 tests passed (3 installed-model opt-ins skipped), including native playback/runtime checks, TTS-only versus fit call sequencing, Ollama keep-alive and UI operation residency. The close/unload UI regression also passed after the asynchronous close cleanup change.

IndexTTS language correction: the adapter now sends the validated dubbing target in `options.language`, as required by the pinned runtime, and versions its cache to invalidate earlier synthesis results on regeneration. Validation: 62 tests passed with media checks (7 opt-in tests skipped); the installed IndexTTS CUDA test separately passed with two Spanish requests, resident-model reuse and explicit unload. These checks verify the request contract and successful native synthesis, not subjective pronunciation quality.

| Phase | Implementation |
|---|---|
| 0–3 | .NET/Avalonia solution, DI, contracts, capabilities, JSON project storage, atomic writes, FFmpeg, mock pipeline and CI |
| 4 | Pinned audio.cpp host, loopback HTTP, CLI separation, health/model listing, version checks, backend selection and shared runtime lease |
| 5 | Qwen3-ASR adapter, sample-offset normalization, language normalization, transcript cache |
| 6 | Qwen3 forced alignment, common word timings, cache |
| 7 | Nemotron 3 speaker turns, overlap-based word assignment, sentence/pause/speaker segmentation |
| 8 | llama.cpp structured translation, context, glossary, duration instructions, validation and bounded retries |
| 9 | IndexTTS 2.5 voice reference, separate emotion reference, instruction/label/vector controls, seed, WAV measurement |
| 10 | Automatic isolated-segment reference selection, preview, replacement and external reference import |
| 11 | Optional BS-RoFormer separation and cached stems |
| 12 | Bounded speech retries, unlocked translation adaptation, supported speed controls, subtle atempo, duration indicator |
| 13 | Integrated VLC preview, segment editor, waveform/timeline, speaker editing/merge, capability-driven controls, autosave |
| 14 | Positioned voice mixing, batched long-video rendering, background stems, peak protection, MKV/MP4, SRT and metadata |
| 15 | Model catalog, explicit downloads, sizes/licenses/sources, SHA-256 verification and removal |
| 16 | Self-contained Windows/Linux and macOS .app packaging, pinned native tools, checksums, CI packaging workflow |

## Verification

62 automated tests passed locally: 12 Core, 17 Application and 33 integration/UI/media/native tests. The complete run used FFmpeg/ffprobe and audio.cpp 0.9.0. Three installed-model inference tests remain opt-in in the main suite. A separate installed IndexTTS CUDA test previously passed (two consecutive Spanish generations, resident-model reuse and explicit unload). Build completed with zero warnings and errors.

Manual voice/video fix (2026-10-06): imported/manually selected speaker audio now overrides the source-voice default, while source emotional conditioning is retained. A regression sends distinct reference WAVs through the actual TTS adapter and verifies payload files, changed cache keys and legacy project compatibility. Generated media always reloads for Play dub. Native VideoView was replaced with a frame-callback Avalonia image; a native/headless integration test verifies rendered pixels, paused seeking, detach/reattach and replay with no native video window handle. Parsed display dimensions prevent H.264 padded decoder sizes from changing the picture aspect ratio. Package: `artifacts/releases/DubbingApp-win-x64-voice-video-fix`. No user project was modified and no new perceptual voice-similarity claim is made.

Playback/residency update (2026-10-06): source video loads paused when opening/importing; paused seeking decodes the requested frame. Playback selects the matching segment in the list/inspector without recursive seeking. Native LibVLC tests cover initial frame rendering, paused seek and resume. The initial TTS pass completes before duration rewrites; translation adjustments and subsequent TTS requests are grouped into phases. Ollama stays resident during its phase and explicitly unloads at switches/completion. audio.cpp unloads models through its API while retaining the idle server. The CUDA 13.3 variant is bundled alongside CPU in `DubbingApp-win-x64-playback-cuda`, with auto-discovery on NVIDIA systems. Actual IndexTTS on RTX 3070: one server startup, first HTTP request 26.7 s, second 1.7 s, unload confirmed. Texts differ; these times demonstrate reuse, not a controlled speed comparison. Evidence: `artifacts/validation/cuda-residency/index-runtime-log.txt`. The original user project was not modified.

Studio redesign (2026-10-06): collapsible sidebar, bundled Lucide icons, video transport, original/vocal and background waveform lanes, draggable white playhead, keyboard seek, zoom/fit, segment inspector and a resizable bounded activity log. UI tests exercise cursor dragging, keyboard seek, zoom, navigation, sidebar collapse, activity messages and splitter resizing. Headless screenshots were inspected at 1520×1060 and 1100×820. Post-analysis sentence/paragraph normalization preserves timestamps, speaker boundaries, locked translations, exclusions and generated clips. A read-only check of the user's existing Blade Runner project grouped 21 segments into 17 eligible segments without changing the source project. The new package is `artifacts/releases/DubbingApp-win-x64-studio`; see `docs/studio.md` for controls and grouping limits. No new installed-model inference was run for this UI update.

Short-reference regression (2026-10-06): reproduced against the open project's original audio via the pinned CLI. The 80 ms reference at 38.48 s aborted with `GGML_ASSERT(nb10 == sizeof(float)) failed`. The first 240 ms reference at 3.52 s successfully generated audio, confirming that its saved Error status was duration fit rather than the transport failure. A one-second contextual reference around dialogue at 57.8 s generated successfully. Logs and WAVs are in `artifacts/validation/short-reference`. Added source-reference context, explicit short-reference validation, runtime exit diagnostics, empty emotional-reference validation, and reversible segment exclusion throughout translation/TTS/render/subtitles. The user's project was not modified. The latest build for this fix is `artifacts/releases/DubbingApp-win-x64-reference-fix`.

Ollama and source-reference update (2026-10-06): the translation engine selector now offers local Ollama, discovers installed chat models and persists the selection per project, including duration rewrites. A separate real inference test passed with installed `qwen3:8b` translating English into Spanish. UI tests verify refresh, selection, save and reopen. IndexTTS now defaults to each original segment for voice identity and automatic emotional conditioning, preferring separated vocals over master audio. A pipeline test verifies identical voice/emotion audio clips, original time boundaries, Spanish text/language and full automatic emotion strength. Exact vocal similarity and emotional realism still require listening acceptance.

Long-audio regression (2026-10-06): the previous pipeline sent the complete transcript and recording to Qwen forced alignment, which exceeded the shared Qwen audio encoder's position limit. ASR now produces 15-second chunks; alignment uses each matching audio/text pair and restores global word offsets. Old transcript/alignment caches are invalidated. Tests cover chunk lengths, text pairing, global offsets and cancellation cleanup. A separate installed-model test passed on the user's 159.66-second recording with Qwen3 ASR and forced alignment: 11 chunks, 51 aligned words, approximately 3 minutes 37 seconds. Recognition quality still needs manual review, especially music/silence hallucinations and proper names. This test did not modify the user's project.

The latest Windows build is in `artifacts/releases/DubbingApp-win-x64`, including both the long-audio correction and Ollama/source-reference update. The earlier `DubbingApp-win-x64-long-audio` folder is preserved for the app that was already open. Close the previous app and use `Run-DubbingApp.ps1` or the latest executable. Existing projects and installed models are reused. For an import that previously failed on alignment, use Engines → Reanalyze.

Local Windows tests cover project round trips, mock replacement, incremental voice cache and corruption detection, model download integrity, local runtime ownership/cancellation, structured provider payloads, reference clipping, locked translation preservation, stretch, waveform, subtitle export, MP4/H.264 and MKV playback/decode, and the Avalonia editor.

Native tests launch audio.cpp 0.9.0 with management enabled and no installed models, check health/model listing, and stop it. LibVLC loads and parses a generated WAV. llama-server b11429 version has been checked. These are runtime/contract checks, not inference checks.

## Remaining acceptance checks

An additional real IndexTTS 2.5 test completed successfully using seconds 60–65 of the user's original master recording for both voice and emotion, with Spanish target text. It produced a non-silent WAV in approximately four minutes on this machine. The sample is saved at `artifacts/validation/index-source-spanish.wav`. This verifies inference and audio output, not perceptual fidelity or crying transfer.

- Run ASR, alignment, diarization, translation, TTS and separation with installed weights and representative recordings; evaluate speaker identity, pronunciation, emotion and fit.
- Test configured GPU builds and device selection on actual hardware.
- Run the Linux/macOS packages and remote CI. Integrated preview there requires a native VLC installation.
- Sign/notarize distribution builds where required. Packages are currently unsigned.

## Practical limits

Automatic reference ranking uses duration, transcript confidence and overlap exclusion; it does not estimate acoustic noise quality. Timeline drawing is intentionally simple. Manual segment edits use Apply/Save; invalid drafts report validation errors. Subtitles are exported as a separate SRT. Export maps the first video stream and the new audio track, not every original stream.

Models are not downloaded implicitly. IndexTTS exposes native speaking rate through duration_factor, but not an absolute duration in seconds. Its timing adaptation uses bounded native speed retries followed by small time stretch; segments that still fail tolerance require review.

ASR/diarization/alignment outputs and successful TTS segments are checkpointed/cached. Translation results persist with the project after a completed translation operation; there is no per-token or in-flight inference resume.
