# Studio workspace

The left sidebar switches between Studio, Speakers, Engines, Models and Settings. Its top button collapses it to icons; tooltips remain available. New/Open/Import/Save are at the bottom.

The video preview sits above Play, Pause and Stop. The timeline immediately below shows original audio (or separated vocals), separated background when available, and dialogue segments. The white cursor follows playback. Click or drag anywhere on the timeline to seek; Left/Right move by 0.1 seconds, Shift+Left/Right by one second. Zoom controls change timeline scale without changing time. Fit returns to the whole recording. The segment inspector remains on the right. At smaller window heights or with an expanded activity panel, the studio workspace scrolls vertically.

Opening/importing a project loads its source video and displays a paused frame. Seeking while paused decodes the requested frame. As playback crosses a dialogue segment, selection follows in the segment list and inspector without seeking back to the segment's start. The list scrolls to keep the active selection visible.

Preview uses VLC memory callbacks and an Avalonia bitmap, rather than a native D3D11 child window. The picture stays inside the layout when switching tabs, scrolling or resizing. Preview decoding uses CPU and is limited to 1280 pixels on its longest side; this does not change exported video quality or CUDA inference. Generated audio and context previews are reopened on every play action so a regenerated file cannot remain cached in the player.

Drag the divider above Activity to resize the log and workspace. Activity records operation start/finish, stage progress, segment identifiers, local process/model requests and errors. Follow scrolls to new entries; Clear empties the view. The in-memory view retains the latest 1,200 entries. Cancel stops the current cancellable operation.

## Dialogue normalization

New imports and reanalysis normalize segments after transcription, optional alignment and speaker assignment, before translation and speech generation. Engines → Dialogue normalization controls automatic grouping and the optional paragraph mode. Studio → Normalize applies it to the current project's eligible segments.

Sentence mode combines nearby fragments from the same identified speaker. It respects punctuation once the group reaches roughly three seconds, allows gaps up to 2.5 seconds and bounds merged spans to 18 seconds. Paragraph mode allows gaps up to three seconds and spans up to 30 seconds. Original word timestamps are retained. Alignment restores punctuation from the transcript when tokens match exactly.

Grouping preserves boundaries at speaker changes, unidentified speakers, overlaps and long pauses. Existing excluded segments, locked translations, generated audio and incompatible voice/emotion overrides stay separate. Merged segments need translation and TTS again; obsolete unlocked translations are cleared. Brief replies and protected segments can therefore remain short. This is deterministic grouping, not a language-model rewrite or correction of recognition errors.

For an older project with already-generated short clips, Normalize preserves those clips. To rebuild segmentation from analysis, enable automatic normalization and use Engines → Reanalyze. Reanalysis can replace segment-level edits when the new boundaries differ, so save a project copy first if those edits must be retained.

## Language and icons

English is the default interface language. Visible labels and new studio controls use the `Languages/en.json` catalog, with other JSON catalogs falling back to English for missing keys; see [localization.md](localization.md).

The studio uses bundled Lucide SVG icons. The pinned source revision and ISC/Feather notices ship under `Assets/Icons` and are listed in the third-party notices.

Play prepares a cached MKV preview using the timeline's three gain/mute controls: original dialogue, music/effects and dubbed dialogue. The original and background lanes display waveforms; the dubbed lane displays timed segment blocks, preferring translated text. Track controls remain fixed while the timeline scrolls and zooms. There is one Play/Pause/Stop transport. Speaker buttons and volume changes automatically resume the updated mix when already playing, after a short preparation. A paused player stays paused. Preview and export use the same settings. See [audio tracks](audio-tracks.md) for full-mix fallback and preparation details.
