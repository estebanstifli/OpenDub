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

## Mixing and preview

Each selected WAV is delayed to its segment start using sample-accurate offsets. FFmpeg resamples to the project format, applies track gains, sums voices/background, protects peaks and produces an audio track the length of the video. More than 24 inputs are mixed in batches, then combined. Partial batch files are removed afterward.

The end of a segment is a target for fitting and validation, not an unconditional hard cut. A shorter WAV leaves silence on that voice track. A longer WAV may overlap later segments. Later starts never move automatically. Audio beyond the end of the video is truncated by the full-project render.

Preview creates a cached MKV containing the original video stream and the current audio mix. Its cache identity includes active WAV contents, paths, offsets, gains and the source video. Regeneration and application edits invalidate the selected preview; the next Play rebuilds/reuses the appropriate mix. A preview already loaded in the player is not a live view of files edited externally. Stop and Play rebuilds the preview selection.

## Common events

| Event | Behavior |
| --- | --- |
| Regenerate segment 2 with unchanged time range and WAV duration | Only segment 2's active WAV changes. Segments 1 and 3 keep their audio and starts. Preview/mix is rebuilt; TTS for 1 and 3 is unnecessary. |
| Regenerate segment 2 with shorter/longer WAV | Its start stays fixed. Silence or overlap is possible. Fitting can adjust it; strict export rejects a difference above the project tolerance. |
| Edit translated text, speaker, emotion or TTS options | Marks affected speech stale. Existing audio may remain audible in preview, but export requires regeneration. |
| Change a speaker reference | Invalidates the speaker's affected segments. It does not synthesize replacements immediately. |
| Change segment start/end | Does not shift following segments. Marks speech/fitting downstream work stale; regenerate to match the new slot/reference. |
| Edit a WAV in another program | No automatic project import or refitting. Strict export detects a changed fingerprint of the active WAV and requires regeneration. |
| Edit raw WAV while a fitted WAV is active | Playback/export continue selecting the fitted file. They do not rebuild it from an externally edited raw file. |
| Mute/change track gain | Changes the mix and preview, without regenerating TTS. Export honors the mixer. Muting the dub track means missing dub speech is not required for that mix. |
| Exclude a segment | Omits its dubbed voice and subtitles; other start times and background tracks remain in place. |
| Existing cache matches the request and file fingerprint | Normal generation may reuse it. Explicit Regenerate/Overwrite requests fresh synthesis. |
| Cancel/fail during a batch | Completed segment checkpoints remain. Incomplete synthesis/export is not published as a successful result. |
| Regenerate after exporting | Previously exported MP4/MKV files remain unchanged. Export again into another empty folder. |

## Export checks and progress

The Export dialog shows missing/outdated speech and duration-mismatch segment numbers. Validation checks the original source fingerprint, required audio existence, stale stages, active WAV fingerprints and duration tolerance. Default tolerance is 5% unless changed in the project.

`Allow duration mismatches (speech may overlap)` is an explicit override only for timing. It renders existing WAVs at their original starts; it does not stretch, cut to segment boundaries, shift following segments, or bypass missing/stale/externally modified audio checks. Timing errors remain visible in the project after this export.

Progress is shown from the dialog opening and remains visible after Start. Export reports source validation, audio validation/mixing, video encoding and output publication. Batch mixes share one progress range, and MP4 progress uses the probed video duration. Cancel and Continue in background remain available during processing.

MP4 uses H.264/AAC with even-dimension padding when required and fast-start metadata. MKV can copy the video stream. Original audio can be added as alternative tracks and subtitles embedded as a selectable track. The final mix, SRT and render metadata accompany the output. Results are prepared in temporary storage and published into an empty destination; prior exports are preserved.

Source files: `GenerateSpeechService`, `DurationFitService`, `ProjectAudioMix`, `DubPreviewService`, `ExportVideoService` and `FFmpegMediaEngine`.
