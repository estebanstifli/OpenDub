# OpenDub 0.1.0

Local AI video dubbing for Windows, Linux and macOS Apple Silicon. English UI with JSON localization, editable dialogue, speaker references, synchronized preview and multitrack export.

## Download and first run

Download the package for your system from [Releases](https://github.com/estebanstifli/OpenDub/releases). The repository and releases are private during the 0.1.0 preview.

- **Windows x64:** extract the ZIP into a writable folder and open `OpenDub.exe`. CPU and NVIDIA CUDA engines are included. CUDA requires a compatible NVIDIA driver.
- **Linux x64:** make the AppImage executable and launch it. Builds target Ubuntu 24.04 or newer compatible desktop environments. The AppImage includes VLC; the development tarball can require system VLC and desktop libraries.
- **macOS Apple Silicon:** open the DMG and drag `OpenDub.app` into Applications. VLC is included. Preview builds are unsigned unless built with a Developer ID identity; Gatekeeper can block them. Public signing and notarization remain a release prerequisite.

The **Prepare OpenDub** assistant downloads the six pinned models sequentially (about 8 GiB), verifies SHA-256 and checks native executables. Cancelled downloads retain partial files and resume on retry. Choose an existing model folder to reuse verified files. Downloads require internet; dubbing runs locally. Verification does not guarantee enough GPU memory for every workload.

## Workflow

Use **Auto dub** for video → analysis → translation → speech and fitting → export. Choose languages, dialogue track and export folder, then Start. Checkpoints are saved: fix reported segments in Studio and run Auto dub again to resume. With a project open, Auto dub resumes it. Import another video to start another job. It uses the project engines, defaults to llama.cpp and Qwen3 4B, and exports generated dialogue with available background.

For manual work: Import video, review Studio/Segments, Translate, Generate speech, Export. Translation and speech support All or ranges such as `3-5,9,12-15`. Overwrite explicitly replaces results, including manual translations. Inspector changes save after a short pause; invalid times are not saved. Regenerate speech after changing text or speaker settings. Moving a clip preserves its audio; changing its length invalidates duration fitting without synthesizing new speech.

**Play** mixes individual WAVs in small PCM blocks while showing the original video muted. Regenerating a segment does not rebuild a movie-length preview. Mute and volume apply during playback. Overlapping dialogue prompts to limit excess audio (which can cut speech), merge texts and regenerate the combined voice, or keep both voices. Following segments never shift automatically.

**Settings** contains language, folders, source copying, defaults and setup. **Advanced** provides engine/model management, backend selection, paths, diagnostics and tuning. Translation supports auto/cpu/cuda/metal, subject to runtime and hardware availability.

## Portability

Windows includes `portable.mode`: settings/models live in `data/`, projects in `projects/`. Copy these folders when updating to another release directory. Removing the marker switches to user-folder storage. Linux/macOS default to user storage because AppImage mounts and signed application bundles must not be modified. Set `OPENDUB_PORTABLE_DIR` to a writable external folder for portable storage on any platform.

Imports copy the source video into the project by default. JSON stores a relative source path when inside the project. Existing projects remain readable; reopening a project whose external video moved prompts for the matching source. External voice references and custom executable paths must remain accessible. Nonportable first launch imports legacy DubbingApp settings when available, retaining originals. Internal solution names and `.dubproj` remain compatible.

## Export

- **Recommended MP4:** H.264 video, AAC audio and optional selectable subtitles.
- **Preserve video quality (MKV):** copies the original video stream.
- **Editing package:** dialogue WAV, available background WAV, mix WAV and SRT.
- **Audio only:** mixed WAV and SRT.

Video profiles can retain original audio as alternative tracks. Export uses timeline mute/gain settings, validates audio/timing and requires an empty destination. Files are prepared before publication; previous exports are preserved. WAV, SRT and render metadata accompany video.

Export lists pending segments and timing mismatches, with progress for mixing, encoding and publishing. An explicit timing override can export WAVs at their existing starts, allowing silence/overlap; it never bypasses stale or externally modified audio checks. See [segment audio lifecycle](docs/audio-segment-lifecycle.md).

## Build and test

Use the .NET SDK in `global.json`:

```sh
dotnet restore DubbingApp.sln --locked-mode
dotnet test DubbingApp.sln -c Release
dotnet run --project src/Dubbing.App
```

PowerShell 7: `scripts/package.ps1 -Runtime win-x64 -AudioCuda -Archive`. Linux AppImage and macOS DMG require native build hosts. CI builds/tests all three targets and uploads artifacts. AI tests are opt-in; CI never downloads model weights. Set `FFMPEG_TESTS=1` and provide FFmpeg/ffprobe for synthetic media integration tests.

See [packaging](docs/packaging.md), [release notes](docs/release-0.1.0.md) and [third-party notices](THIRD-PARTY-NOTICES.md). Application source licensing will be decided before public launch; dependency and model licenses are separate.
