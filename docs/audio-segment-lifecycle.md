# Segment audio and export lifecycle

OpenDub places audio on an absolute timeline. It does not concatenate the WAVs in sequence. A segment has a start, end, target duration, translation, speaker/voice settings, generated speech metadata and optionally duration-fitted speech metadata.

## Creating and selecting WAVs

1. TTS receives the translation, target language, voice reference and emotion configuration. Source references prefer the separated vocals when available. The cache key incorporates synthesis inputs and reference fingerprints.
2. Generated WAVs live under `generated/tts/`. Filenames use cache keys and, for explicit regeneration, a unique suffix; they are not named by the displayed segment number.
3. The project records the active WAV path, measured duration, cache key and SHA-256 fingerprint. Progress is saved per segment.
4. Duration fitting can retry native speech speed, or rewrite an unlocked translation when the engine has no speed control, within configured retry limits. Small residual differences can be time-stretched into a separate `-fit.wav`. Fitting is not a guarantee of an exact duration.
5. Playback/export use the fitted WAV if the duration-fit stage is current; otherwise they use the generated WAV. Original generated speech remains separate from fitted speech.

Explicit regeneration writes a fresh WAV and only switches the active project reference after successful synthesis. Earlier WAVs are not immediately deleted and are not an exposed version-history feature. A failed regeneration retains the previous audio reference, but pending edits can still require regeneration before export. Duration-adaptation synthesis also uses unique filenames to avoid overwriting shared cached audio.

Very short segments can be expanded before synthesis where neighboring free time allows. This can change the segment's time range. If there is no free space, the range is retained and the reference context is expanded separately.

## Dynamic playback

Studio and Segments Play use the same realtime mixer. The original video's audio decoding is disabled (`:no-audio`); its VLC player must not use device mute, which can silence both players through their shared Windows audio session. A second VLC player, explicitly unmuted at 100% device volume, consumes a seekable virtual RF64 WAV generated in small PCM blocks in memory from the individual active WAV files. Track mute/gain lives in this PCM mix. No complete preview WAV or MKV is written. The audio clock drives the playhead; the video clock is corrected when drift exceeds 200 ms, at most once per second. Both players pause, resume and seek together.

Preview is stereo, 48 kHz PCM. Mono/stereo PCM16/24/32 and float32 WAVs are read directly; other formats are converted once per file into `cache/pcm-clips/`, then reused. Source handles and decoded blocks are bounded (32 files, four 4096-frame blocks per file). Seeking reads blocks near the new cursor instead of decoding the preceding film. Resampling and gain apply while mixing. Preview clamps peaks to 0.95; final export uses FFmpeg's limiter, so overloaded mixes can sound different. Lower gains to avoid clipping.

Each Play refreshes a lightweight clip list and file metadata (path, length, modification time), without hashing every WAV or rebuilding the film. Successful regeneration selects a new unique file for that segment. Track mute/gain changes update the stream and flush VLC's short decoder queue. Saved placement edits update playback too unless an overlap needs a decision. Background conversions can make a first Play slower; subsequent Plays reuse them. External editors are not watched continuously: Stop and Play refresh metadata, and strict export still verifies SHA-256.

The old `DubPreviewService` remains available for offline preview callers/tests, but Studio Play no longer calls it.

## Timing and overlaps

A shorter WAV leaves silence. A longer WAV can exceed its slot. Later segments never move automatically. Playback and render stop at the video end.

Moving a segment preserves its generated/fitted WAV and pins the source reference to its previous source range. Changing its length marks duration fitting stale; preview selects the generated WAV until refitting. Neither edit reruns TTS by itself. Changing text, voice or emotion still marks synthesis stale. Generation uses the pinned source reference separately from the destination time slot.

After editing/generation and before manual playback/export, unresolved overlaps between active segments prompt for a decision:

- **Do not overlap:** limits the first segment to the next start and caps its audible WAV to its slot. This explicitly cuts excess speech; it does not speed it up. The checkbox in segment details can remove the cap later.
- **Merge segments:** joins source and translated text, keeps the first speaker/settings, removes the second segment and regenerates joint speech after the remaining collisions are resolved. Incomplete translations require completion before synthesis. Old files remain on disk but are no longer selected. If a later overlap dialog is cancelled, already merged segments remain saved and can be regenerated manually. The newly measured voice is checked for overlaps again.
- **Keep overlap:** allows both voices. Approval is saved for those segment bounds and audio fingerprints; edits or regeneration can require a new decision.
- **Cancel:** stops the current action. Decisions already applied remain saved.

Limit and Keep can apply to remaining collisions; merges are individual because they combine text and voice identities. Equal-start collisions require Merge or Keep because limiting would create a zero-length slot.

## Final render

Export physically mixes the files. Each selected WAV is delayed to its absolute start using sample-accurate offsets. FFmpeg applies the explicit clip limit, resamples to the project format, applies track gains, sums voices/background, limits peaks and produces an audio track the length of the video. More than 24 inputs are mixed in batches, then combined. Partial batch files are removed afterward. Export can retain more than two channels when the project supports them; preview is always stereo.

## Common events

| Event | Behavior |
| --- | --- |
| Regenerate segment 2 with unchanged time range and WAV duration | Only segment 2's active WAV changes. Segments 1 and 3 keep their audio and starts. Next Play refreshes the clip list; there is no full-film preview render. |
| Regenerate segment 2 with shorter/longer WAV | Its start stays fixed. Silence or overlap is possible; overlaps require a decision. Fitting can adjust it; strict export rejects a difference above tolerance unless explicitly allowed. |
| Edit translated text, speaker, emotion or TTS options | Marks affected speech stale. Existing audio may remain audible in preview, but export requires regeneration. |
| Change a speaker reference | Invalidates the speaker's affected segments. It does not synthesize replacements immediately. |
| Change segment start/end | Does not shift following segments or synthesize new speech. Retains the WAV and source reference. Length changes invalidate fitting; overlaps require a decision. |
| Edit a WAV in another program | No automatic project import or refitting. Strict export detects a changed fingerprint of the active WAV and requires regeneration. |
| Edit raw WAV while a fitted WAV is active | Playback/export continue selecting the fitted file. They do not rebuild it from an externally edited raw file. |
| Mute/change track gain | Updates playback in place, without regenerating TTS or a complete mix. Export honors the mixer. Muting the dub track means missing dub speech is not required for that mix. |
| Exclude a segment | Omits its dubbed voice and subtitles; other start times and background tracks remain in place. |
| Existing cache matches the request and file fingerprint | Normal generation may reuse it. Explicit Regenerate/Overwrite requests fresh synthesis. |
| Cancel/fail during a batch | Completed segment checkpoints remain. Incomplete synthesis/export is not published as a successful result. |
| Regenerate after exporting | Previously exported MP4/MKV files remain unchanged. Export again into another empty folder. |

## Export checks and progress

The Export dialog shows missing/outdated speech and duration-mismatch segment numbers. Validation checks the original source fingerprint, required audio existence, stale stages, active WAV fingerprints and duration tolerance. Default tolerance is 5% unless changed in the project.

`Allow duration mismatches (speech may overlap)` is an explicit override only for timing. It renders existing WAVs at their original starts; it does not stretch, add clip limits, shift following segments, or bypass missing/stale/externally modified audio checks. Any previously chosen **Do not overlap** limits are still honored. Timing errors remain visible in the project after this export.

Progress is shown from the dialog opening and remains visible after Start. Export reports source validation, audio validation/mixing, video encoding and output publication. Batch mixes share one progress range, and MP4 progress uses the probed video duration. Cancel and Continue in background remain available during processing.

MP4 uses H.264/AAC with even-dimension padding when required and fast-start metadata. MKV can copy the video stream. Original audio can be added as alternative tracks and subtitles embedded as a selectable track. The final mix, SRT and render metadata accompany the output. Results are prepared in temporary storage and published into an empty destination; prior exports are preserved.

Source files: `RealtimeAudioService`, `TimelineAudioStream`, `PcmWaveSource`, `MainWindow.Realtime`, `SegmentCollisions`, `GenerateSpeechService`, `DurationFitService`, `ProjectAudioMix`, `ExportVideoService` and `FFmpegMediaEngine`.

## Windows validation, 2026-10-07

Read-only measurements on the existing 12:40 project: 115 segments, 112 active clips; preparing the clip list took 35 ms initially and 2.4 ms warm. Mixing 30 seconds took 1.29 seconds of computation; 100 random seek/block reads took 124 ms. These are local mixer measurements, not guaranteed Play-button latency or GPU synthesis times. No user project was saved. Native VLC tests cover decoding audible PCM, seek/live mute, muted video, paused seek, resume, frame presentation and drift correction. Linux/macOS native playback still requires testing on those hosts.
