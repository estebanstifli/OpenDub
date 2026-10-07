# OpenDub 0.1.0 — private preview

Renames the product/executable while retaining compatible project files and internal solution names.

Adds first-run model setup, sequential resumable verified downloads, portable data directories, source-video copying/relocation, basic/advanced settings, Metal translation detection, Auto dub with checkpoints, and export profiles with alternative source audio and selectable subtitles. Inspector editing saves automatically. English UI uses Languages/*.json.

## Validation boundaries

Windows is the actively exercised development platform. CI runs managed/headless tests and synthetic FFmpeg workflows. Packaging creates Windows ZIP, Linux AppImage/tarball and macOS ARM64 DMG/tarball. Real-device playback and GPU acceptance tests are required before declaring Linux/macOS production-ready. Linux's pinned inference package currently uses CPU; macOS audio.cpp includes Metal and translation detects Metal. Windows bundles CUDA.

Setup verifies downloads and executable startup, not dubbing quality or peak model memory. Trailers may need speaker/reference review. Auto dub preserves checkpoints on errors and resumes after corrections. Missing clean background is never replaced with the original mixed track.

No signing credentials are stored here. The macOS script supports an installed Developer ID identity and notarytool keychain profile; preview builds remain unsigned without them. Windows is unsigned. macOS Intel and Linux ARM64 are outside this package matrix.

Before public launch: choose the source license, finish platform/device acceptance tests, provision signing/notarization, confirm third-party redistribution/source obligations and publish tested assets. Never include personal projects, media, models or credentials in Git.
