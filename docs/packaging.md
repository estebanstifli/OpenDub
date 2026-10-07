# OpenDub packaging

`./scripts/package.ps1 -Runtime win-x64 -AudioCuda -Archive` builds the portable Windows ZIP with pinned CPU and CUDA engines. Use linux-x64 or osx-arm64 on native hosts for AppImage/DMG. End users do not need .NET, Python or compilers.

runtimes/versions.json pins inference tools and FFmpeg by size/SHA-256. VLC comes from the Windows NuGet dependency, the pinned macOS VLC 3.0.24 DMG, and Ubuntu packages bundled by SHA-256-pinned linuxdeploy. Linux distribution libraries are not byte-for-byte pinned; CI apt logs identify package versions. Models are separate downloads.

Windows portable mode stores configuration/models in data/ and projects alongside. Linux/macOS use user storage, or OPENDUB_PORTABLE_DIR for external portable storage. Do not write into signed app bundles or read-only AppImage mounts.

Linux packaging targets Ubuntu 24.04 and needs libvlc-dev, vlc-plugin-base, vlc-plugin-video-output, libfuse2t64, patchelf and desktop libraries; CI installs them. The pinned audio runtime requires glibc 2.38 or newer. AppRun configures bundled VLC and library paths; each inference subprocess prefers its own adjacent ggml libraries to avoid ABI collisions. Host GPU drivers and compatible glibc remain necessary. Development tarballs may require system VLC.

macOS bundles VLC.app under Contents/Frameworks. OPENDUB_SIGN_IDENTITY optionally specifies an installed Developer ID; OPENDUB_NOTARY_PROFILE names an existing notarytool keychain profile. Without them output is an unsigned private preview. Public signing needs clean-Mac validation.

The workflow produces build artifacts. Private releases are assembled after verification. Preserve third-party notices and fulfill corresponding source obligations before public redistribution. This repo contains no signing keys or download tokens.
