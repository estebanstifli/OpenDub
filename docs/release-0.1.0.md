# OpenDub 0.1.0 — private preview

Renames the product/executable while retaining compatible project files and internal solution names.

Adds first-run model setup, sequential resumable verified downloads, portable data directories, source-video copying/relocation, basic/advanced settings, Metal translation detection, Auto dub with checkpoints, and export profiles with alternative source audio and selectable subtitles. Inspector editing saves automatically. English UI uses Languages/*.json.

The development build adds realtime WAV mixing for Studio/Segments, live mute/gain, synchronized muted video, and overlap decisions (limit, merge/regenerate, keep or cancel). Moving clips preserves their WAVs and source references. A complete mix is rendered only for export/offline render calls. See [audio lifecycle](audio-segment-lifecycle.md) for timing behavior and Windows measurements. Existing published release archives do not update automatically when source changes land.

Playback fixes disable source audio decoding instead of muting the shared Windows sound session. Native regression checks cover the actual output mute, volume and track selection as well as PCM decoding, including separate dub/background auditions of an explicitly supplied project. Segment text editors are 40% shorter (60 px, scrollable). Fresh empty projects are clean until edited; unchanged segment detail values do not create pending changes.

Regenerate and fit keeps candidate WAVs temporary through all synthesis/adaptation/stretch phases. Only completed segment results become active; cancelled or failed fitting preserves previous audio. Overlap review uses final audio and is scoped to the regenerated selection. Progress no longer reaches 100% at the initial TTS pass.

## Validation boundaries

Windows is the actively exercised development platform. CI runs managed/headless tests and synthetic FFmpeg workflows. Packaging creates Windows ZIP, Linux AppImage/tarball and macOS ARM64 DMG/tarball. Real-device playback and GPU acceptance tests are required before declaring Linux/macOS production-ready. Linux's pinned inference package currently uses CPU; macOS audio.cpp includes Metal and translation detects Metal. Windows bundles CUDA.

Setup verifies downloads and executable startup, not dubbing quality or peak model memory. Trailers may need speaker/reference review. Auto dub preserves checkpoints on errors and resumes after corrections. Missing clean background is never replaced with the original mixed track.

No signing credentials are stored here. The macOS script supports an installed Developer ID identity and notarytool keychain profile; preview builds remain unsigned without them. Windows is unsigned. macOS Intel and Linux ARM64 are outside this package matrix.

Before public launch: choose the source license, finish platform/device acceptance tests, provision signing/notarization, confirm third-party redistribution/source obligations and publish tested assets. Never include personal projects, media, models or credentials in Git.
