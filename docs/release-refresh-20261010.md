# OpenDub 0.1.0 — refreshed early preview (2026-10-10)

All three application packages are rebuilt from commit **c53998470c169a8c79e5957f93e673741e074425**. Each includes `build-info.json` identifying its exact source revision. The original `v0.1.0` tag is retained; use the attached `OpenDub-source-c539984.zip` for the source matching these refreshed binaries.

## Included improvements

- Studio and Segments editing, waveform-based Create/Split, Delete and Merge tools, overlap review and recent projects.
- Incremental multitrack preview, per-track mute/volume, synchronized video and segment audio, and regenerate-and-fit improvements.
- Translation and speech dialogs with segment ranges, overwrite controls, progress, logs and background operation; editable translation prompts.
- Export MP4, MKV, editing stems, or MP3/WAV/FLAC/M4A/OGG, for the entire timeline or a selected period.
- Duration mismatches allowed by default in Export and Auto dub; stale or invalid audio is still checked.
- Project header links to the last exported file and its folder, including previous standard-folder exports.
- MIT license for OpenDub code, upstream acknowledgements, included license texts and build provenance.

## Downloads

- Windows x64: extract the ZIP into a writable folder, then open `OpenDub.exe`. CPU and NVIDIA CUDA runtimes included; compatible drivers required.
- Linux x64: make the AppImage executable. Ubuntu 24.04 or a compatible newer desktop; current inference runtime uses CPU.
- macOS Apple Silicon: open the DMG and copy OpenDub.app to Applications. This preview is unsigned and not notarized; Gatekeeper may block it.

The setup assistant downloads the AI models separately (approximately 8 GiB). Existing users should preserve their `data/` and `projects/` folders or point Settings at their existing folders. Release packages contain no user projects, personal settings or model weights.

See `SHA256SUMS` for download verification. Packaging runs automated tests and startup checks on each platform; this does not claim end-to-end GPU/voice-quality validation on every device.

## Licensing

OpenDub source is MIT. Third-party runtimes, libraries and model weights retain their separate licenses. audio.cpp 0.9.0 is Apache-2.0; IndexTTS is subject to the bilibili Model Use License. Packages include `LICENSE`, `THIRD-PARTY-NOTICES.md`, `licenses/` and redistribution/source references in `docs/licensing.md`.

FFmpeg/VLC builds include LGPL/GPL components. Their corresponding-source obligations are separate from attribution. The licensing notes document source locations and the limits of the current review, including the unresolved upstream provenance of the BS-RoFormer ep368 checkpoint. No claim is made that an acknowledgement table alone establishes complete redistribution compliance.

[Watch the demo](https://www.youtube.com/watch?v=iYkr_BjH9cE) · [README](https://github.com/estebanstifli/OpenDub#readme)
