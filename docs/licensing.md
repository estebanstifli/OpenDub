# Licensing and redistribution

Reviewed against upstream repositories on 2026-10-10. OpenDub's original code is MIT; third-party material is not relicensed.

## Notices that must travel with distributions

- MIT / ISC: preserve the original copyright and permission texts. Copies for direct dependencies are in `licenses/`.
- Apache-2.0: include the license and applicable upstream NOTICE/attributions; identify modifications if changing upstream files. The pinned audio.cpp 0.9.0 LICENSE is Apache-2.0 (not MIT). Its root has no separate NOTICE file; retain notices included in its runtime archive.
- LGPL/GPL: retain license and copyright texts and provide the corresponding source for the binaries distributed, including relevant patches and build information. Preserve the user's ability to replace/relink LGPL libraries and debug modifications. OpenDub dynamically loads LibVLC and invokes FFmpeg as a separate executable; it imposes no restriction on replacing these components.
- IndexTTS: its bilibili agreement requires retention of the agreement and original notices, downstream compliance, the derivative-work disclaimer reproduced in THIRD-PARTY-NOTICES.md, and compliance with its usage restrictions. Commercial-scale use above its specified revenue/user thresholds requires separate permission. Do not describe IndexTTS as MIT or universally unrestricted.
- Nemotron: OpenMDW-1.1 requires preserving the agreement and applicable origin notices when distributing model materials.
- CUDA libraries: NVIDIA proprietary redistribution terms apply independently of the inference engine license. Keep their upstream notices; OpenDub MIT does not cover NVIDIA binaries.

## Source locations and build provenance

- Inference versions, archive hashes and download URLs: `runtimes/versions.json`.
- audio.cpp: https://github.com/0xShug0/audio.cpp/tree/v0.9.0
- llama.cpp: https://github.com/ggml-org/llama.cpp/tree/b11429
- LibVLCSharp 3.9.5: https://github.com/videolan/libvlcsharp/tree/3.9.5
- Windows VLC 3.0.23 sources: https://download.videolan.org/pub/videolan/vlc/3.0.23/
- macOS VLC 3.0.24 sources: https://download.videolan.org/pub/videolan/vlc/3.0.24/
- Ubuntu VLC and bundled libraries: their exact binary/source package versions and copyright files are recorded in the Linux package under `licenses/ubuntu/`. Corresponding Ubuntu source packages are available through Ubuntu's package archive / `apt-get source` for those versions.
- FFmpeg executable and build metadata: `runtimes/<platform>/ffmpeg/BUILD-SOURCES.txt`; the build scripts and upstream sources are linked by https://github.com/eugeneware/ffmpeg-static/tree/b6.1.1 and https://github.com/descriptinc/ffmpeg-ffprobe-static/tree/b6.1.2-rc.1. The Windows FFmpeg build identifies https://github.com/FFmpeg/FFmpeg/commit/e38092ef93 and GPLv3.
- NuGet versions and dependency hashes: tracked `packages.lock.json` files. Runtime .NET third-party notices are included in `licenses/`.

## Limits of this review

An acknowledgement table alone does not discharge GPL/LGPL corresponding-source obligations. Source/build links above document provenance, but are not a claim that every upstream binary and transitive dependency has been independently reproduced or that a complete corresponding-source archive has been assembled. Redistributors must supply the exact applicable source, dependencies and build materials, not just a generic upstream homepage.

The BS-RoFormer ep368 original checkpoint provenance is unresolved in audio.cpp's own pinned model-license inventory. We retain that uncertainty rather than assigning it the implementation's MIT license. Model weights are downloaded separately, not included in release archives. Media and voice references require their own rights and are outside OpenDub's license.
