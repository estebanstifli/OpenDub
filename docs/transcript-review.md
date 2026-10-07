# Repetitive transcription review

Import excludes suspected ASR loops from dubbing when a segment has no detected speaker and at least 20 word tokens, of which 80% or more are the same token. It preserves the original text and timing, marking the reason on the segment. This is a conservative heuristic, not a voice-activity detector or proof that the audio contains no speech. Short laughter and ordinary dialogue without a speaker remain untouched.

For existing projects, open Engines and choose **Review repetitive transcription**. It does not reimport or retranscribe. Locked text, existing translations and generated speech are preserved. Review the excluded segments against the original video. Uncheck **Exclude from dubbing** and apply the segment to restore it if needed. Short artifacts such as an isolated “The” still require manual review.

The separation model can leave sound effects in the vocal stem. A vocal stem is therefore not guaranteed to contain only dialogue. This guard prevents obvious repeated-text hallucinations from becoming translated/TTS dialogue; it does not improve acoustic separation itself.
