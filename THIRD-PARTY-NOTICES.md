# Third-party components

Studio icons: Lucide (ISC), including applicable Feather notices (MIT). The bundled SVG subset is pinned to commit `ccb4b193d2680ad2eceb966a129d6bb9c8967014` from https://github.com/lucide-icons/lucide. Copyright and license notices ship in `Assets/Icons/LICENSE.txt`; the source revision is in `Assets/Icons/REVISION.txt`.

The application uses Avalonia (MIT), CommunityToolkit.Mvvm (MIT), .NET (MIT), LibVLCSharp (LGPL-2.1-or-later), and native LibVLC (LGPL-2.1-or-later; individual plugins can have different terms). NuGet lockfiles contain exact versions and hashes. Native VLC source: https://code.videolan.org/videolan/vlc and https://code.videolan.org/videolan/libvlcsharp.

Bundled audio.cpp 0.9.0: https://github.com/0xShug0/audio.cpp/tree/v0.9.0 (MIT). Bundled llama.cpp b11429: https://github.com/ggml-org/llama.cpp/tree/b11429 (MIT). Their release archives include licenses.

The optional Windows CUDA package uses the audio.cpp v0.9.0 CUDA 13.3 binary and CUDA runtime archives from the same official release. Exact archive sizes and SHA-256 digests are recorded under `accelerators` in `runtimes/versions.json`. NVIDIA CUDA libraries are third-party proprietary components, separate from audio.cpp's MIT code.

FFmpeg binaries are separate executables. Builds may include GPL components. The package retains their LICENSE.txt and BUILD-SOURCES.txt alongside the executables. Sources/build instructions are provided by https://github.com/eugeneware/ffmpeg-static/tree/b6.1.1 and https://github.com/descriptinc/ffmpeg-ffprobe-static/tree/b6.1.2-rc.1; upstream: https://ffmpeg.org/download.html#get-sources. Preserve these notices and satisfy the applicable source-distribution terms before redistributing binaries.

Model weights are downloaded separately, never bundled or downloaded by tests. Each model has its own license, linked in the Models catalog; runtime licenses do not grant rights to model weights or third-party voices.

The optional translation CUDA runtime uses llama.cpp b11429 Windows CUDA 12.4 binaries and CUDA DLLs from the same official llama.cpp release. Sizes and official SHA-256 digests are pinned in runtimes/versions.json. NVIDIA libraries retain their proprietary licensing independently of llama.cpp.

macOS includes VLC 3.0.24 from https://download.videolan.org/vlc/3.0.24/macosx/. Sources: https://download.videolan.org/vlc/3.0.24/. Linux AppImage bundles Ubuntu VLC and dependencies; retain distribution copyright notices and provide corresponding sources before public redistribution. linuxdeploy is a build-time tool.
