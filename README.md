<div align="center">

# OpenDub

**Local AI video dubbing. Your voices, your timeline, your control.**

[![Watch the OpenDub demo](https://img.youtube.com/vi/iYkr_BjH9cE/hqdefault.jpg)](https://www.youtube.com/watch?v=iYkr_BjH9cE)

**[▶ Watch the demo](https://www.youtube.com/watch?v=iYkr_BjH9cE)**

[![Build and test](https://github.com/estebanstifli/OpenDub/actions/workflows/build.yml/badge.svg)](https://github.com/estebanstifli/OpenDub/actions/workflows/build.yml)
![Preview](https://img.shields.io/badge/version-0.1.0_preview-blue)
![Platforms](https://img.shields.io/badge/platforms-Windows%20%7C%20Linux%20%7C%20macOS-64748b)

[Download](https://github.com/estebanstifli/OpenDub/releases) · [Getting started](#getting-started) · [Features](#features) · [Documentation](#documentation) · [Report an issue](https://github.com/estebanstifli/OpenDub/issues)

</div>

OpenDub is a desktop studio for translating and dubbing videos with local AI. Separate dialogue from music and effects, identify speakers, translate their lines, and generate new speech using voice and emotion references. Review everything against the video before exporting.

Run the complete workflow with **Auto dub**, or take control of individual segments in **Studio** and **Segments**. Once the models are downloaded, the default dubbing workflow runs on your computer without a cloud inference service.

> **Early preview:** OpenDub is under active development. Windows is the most extensively exercised platform; Linux and macOS packages are available for testing. This README describes the current `main` branch. The downloadable 0.1.0 preview predates some recent improvements; consult the release notes for each package.

## Download

| Platform | 0.1.0 preview | Start the app |
| --- | --- | --- |
| Windows x64 | [Download ZIP](https://github.com/estebanstifli/OpenDub/releases/download/v0.1.0/OpenDub-0.1.0-win-x64.zip) | Extract into a writable folder and open `OpenDub.exe`. |
| Linux x64 | [Download AppImage](https://github.com/estebanstifli/OpenDub/releases/download/v0.1.0/OpenDub-0.1.0-linux-x64.AppImage) | Make it executable, then launch it. Targets Ubuntu 24.04 or a compatible newer desktop. |
| macOS Apple Silicon | [Download DMG](https://github.com/estebanstifli/OpenDub/releases/download/v0.1.0/OpenDub-0.1.0-osx-arm64.dmg) | Open the DMG and copy `OpenDub.app` to Applications. |

Packages include the application runtime, inference tools, FFmpeg and VLC. **No separate .NET SDK, Python environment or compiler is needed to run a package.** AI model weights are downloaded separately. Checksums are available on the [release page](https://github.com/estebanstifli/OpenDub/releases/tag/v0.1.0).

Windows packages include CPU and NVIDIA CUDA runtimes; CUDA requires a compatible NVIDIA driver. The current Linux inference package uses CPU. macOS acceleration depends on the bundled runtime and model. The macOS preview is unsigned and not notarized, so Gatekeeper may block it.

## Getting started

1. **Prepare the models.** On first launch, the setup assistant downloads six pinned models sequentially, shows progress and verifies SHA-256 checksums. Allow approximately **8 GiB** for weights, plus space for the app, media and generated audio. Cancelled downloads can resume; an existing model folder can be reused.
2. **Import a video.** Choose the dialogue audio track. Enable separation when speech, music and effects are mixed together, or use an available background track.
3. **Review the dialogue.** Inspect transcription, speaker assignments and timings. Choose the output language and each speaker's voice reference.
4. **Translate and generate speech.** Process everything or a selection such as `3-5,9,12-15`. Preview the result and regenerate any line that needs work.
5. **Export.** Save a dubbed video, an audio file or an editing package.

Prefer a single flow? **Auto dub** runs analysis, translation, speech generation, duration fitting and export, saving checkpoints along the way. With a project open, it resumes that project. Import another video to start a new job.

## Features

### A studio built around dialogue

- **Video and waveform timeline:** synchronized playback, a draggable playhead, zoom, and automatic selection of the current segment.
- **Independent audio controls:** mute and adjust the original voice, dubbed speech, and available music/effects tracks.
- **Side-by-side text editing:** original and translated dialogue in the Segments workspace, with video preview and segment actions.
- **Precise segment tools:** create, split, delete or merge neighbouring segments; drag waveform boundaries and optionally transcribe newly selected audio.
- **Visible occupied ranges:** existing segments appear in the Create editor, and overlapping new selections are blocked. Adjacent segments may share a boundary.
- **Overlap review:** compare original and dubbed waveforms, adjust timings or text, merge and regenerate, trim excess audio, or keep the overlap.
- **Recent projects and activity logs:** reopen the last ten projects and follow progress in resizable logs or operation dialogs.

### Local AI with editable results

- **Speaker-specific voice references:** select a detected segment, adjust its reference range, or provide an external recording.
- **Voice and emotion conditioning:** use the original speech as an emotion reference while choosing the speaker reference for voice identity.
- **Local translation choices:** bundled llama.cpp with Qwen3 4B Q4_K_M, or models available through a local Ollama installation.
- **Editable translation prompts:** add guidance such as regional Spanish or a preferred style before starting a translation pass.
- **Selective processing:** choose all segments or numbered ranges, with an option to overwrite existing translations or speech.
- **Duration fitting:** bounded synthesis retries and permitted time stretching help speech fit its slot. Exact timing and voice similarity are not guaranteed.
- **Simple and advanced settings:** everyday preferences up front; model, runtime, backend and diagnostic controls available in Advanced.

The current IndexTTS2.5 integration supports dubbed speech in **Chinese (`zh`), English (`en`), Japanese (`ja`), Spanish (`es`) and Arabic (`ar`)**. Transcription has its own language support. The interface defaults to English and uses JSON localization files, with Spanish included.

### Fast iteration without rebuilding the whole soundtrack

During playback, OpenDub mixes individual segment WAVs in small PCM blocks alongside the muted source video. Regenerating one segment does **not** rebuild a movie-length preview WAV. A full soundtrack is rendered for export.

Text edits are saved automatically after a short pause. Changing a translation or speaker requires new speech; moving a segment keeps its audio, while changing its length invalidates duration fitting. Following segments never shift automatically. See the [audio segment lifecycle](docs/audio-segment-lifecycle.md) for details.

## Export

| Output | What you get |
| --- | --- |
| **Recommended MP4** | H.264 video, AAC audio and optional selectable subtitles. |
| **Preserve video quality (MKV)** | Original video stream copied for full-length exports. |
| **Audio only** | MP3, WAV, FLAC, M4A or OGG. MP3 defaults to 192 kb/s stereo; WAV uses 24-bit PCM. |
| **Editing package** | Dialogue WAV, available background WAV, mixed WAV and SRT subtitles. |

Export the **whole project** or a **time range**, using start/end times such as `00:00:00` to `00:10:05`. Video, audio and subtitles are cropped together and start at zero in the result. Video range exports are re-encoded for accurate cuts, including MKV.

Exports use the timeline's mute and volume settings. Video profiles can retain original audio as alternative tracks. Progress covers validation, mixing, encoding and publication into an empty destination folder.

**Duration mismatches are allowed by default in Export and Auto dub.** Speech keeps its start position and may overlap the next segment within the exported range; stricter timing remains optional. Missing, stale or externally modified audio still requires attention.

After export, two highlighted links below the project name open the **exported file** and its **folder**. The destination is remembered when reopening a project; older exports can be discovered in the project's standard output folders.

## AI pipeline

| Stage | Default model / tool | Purpose |
| --- | --- | --- |
| Audio preparation | FFmpeg | Inspect tracks, extract audio and prepare media. |
| Optional source separation | BS-RoFormer | Separate dialogue/vocals from background audio. |
| Transcription | Qwen3-ASR 0.6B | Recognize speech. |
| Speaker diarization | Nemotron 3 Diarization | Assign speaker turns. |
| Forced alignment | Qwen3 Forced Aligner 0.6B | Align recognized words with audio timing. |
| Translation | Qwen3 4B Q4_K_M via llama.cpp | Translate dialogue; local Ollama is also supported. |
| Speech generation | IndexTTS2.5 | Generate speech with voice and emotion references. |
| Timing and export | OpenDub + FFmpeg | Fit durations, mix tracks and encode output. |

Audio models run through **audio.cpp**. During a generation batch, the engine can keep its model resident across requests; process transitions release model memory. Logs expose runtime/backend selection and available GPU diagnostics. Acceleration depends on the selected runtime, model and hardware.

Separation, transcription and diarization can make mistakes, particularly with trailers, loud effects, music and simultaneous speakers. Review detected dialogue and references before a final render. Separation may leave traces of the original voice or remove some background detail; duration fitting is not lip synchronization.

## Projects and portability

- **Windows portable mode:** `portable.mode` keeps settings and models in `data/`, and projects in `projects/` by default. The project folder is configurable in Settings.
- **Linux and macOS:** default to user storage so read-only AppImages and application bundles remain untouched.
- **Portable storage on any platform:** set `OPENDUB_PORTABLE_DIR` to a writable folder.
- **Self-contained projects:** imported source videos are copied into the project by default, with relative paths for internal media. External references and custom runtime paths must remain accessible.

When updating a portable installation, preserve your `data/` and `projects/` folders. Project directories use the `.dubproj` extension. Internal solution and namespace names still use `DubbingApp` / `Dubbing` for compatibility.

## Build from source

Install the .NET SDK specified in [`global.json`](global.json), then:

```sh
git clone https://github.com/estebanstifli/OpenDub.git
cd OpenDub
dotnet restore DubbingApp.sln --locked-mode
dotnet test DubbingApp.sln -c Release
dotnet run --project src/Dubbing.App
```

Native tools and models are also needed for real dubbing; see [packaging and runtime setup](docs/packaging.md). To build a Windows portable archive with PowerShell 7:

```powershell
./scripts/package.ps1 -Runtime win-x64 -AudioCuda -Archive
```

Linux AppImage and macOS DMG packaging use native build hosts. GitHub Actions builds/tests all three targets and produces package artifacts. Default tests do not download model weights. For synthetic media integration tests, set `FFMPEG_TESTS=1` and provide `DUBBING_FFMPEG` and `DUBBING_FFPROBE`. Real AI and playback tests are opt-in.

## Documentation

- [Architecture](docs/architecture.md) and [project format](docs/project-format.md)
- [Studio](docs/studio.md), [audio tracks](docs/audio-tracks.md) and [transcript review](docs/transcript-review.md)
- [Segment audio lifecycle](docs/audio-segment-lifecycle.md)
- [AI engines](docs/engines.md) and [localization](docs/localization.md)
- [Packaging](docs/packaging.md) and [0.1.0 release notes](docs/release-0.1.0.md)

## Feedback

[Open an issue](https://github.com/estebanstifli/OpenDub/issues) for bugs or suggestions. For a reproducible bug, include your app version, operating system, engine/model, selected backend, steps and the relevant log excerpt. Remove private paths or dialogue from anything you share.

## License and credits

An application source license has **not yet been selected**. Public repository visibility does not mean the project is released under an open-source license. Dependencies and model weights have separate licenses; see [third-party notices](THIRD-PARTY-NOTICES.md) and the model catalog.

Built with Avalonia, .NET, audio.cpp, llama.cpp, FFmpeg, LibVLC and Lucide icons, with AI models from their respective authors.
