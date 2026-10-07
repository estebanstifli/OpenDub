# Maintainer tool: pinned upstream metadata, no model downloads.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$upstream = Join-Path $root 'artifacts/upstream/audio.cpp/model_specs'
$definitions = @(
    @('qwen3_asr', 'qwen3_asr_0_6b_q8_0', 'Transcription'),
    @('qwen3_forced_aligner', 'qwen3_forced_aligner_0_6b_q8_0', 'Alignment'),
    @('nemotron_3_diar', 'nemotron_3_diar_q8_0', 'Diarization'),
    @('index_tts2', 'index_tts2_5_q8_0', 'Tts'),
    @('bs_roformer', 'bs_roformer_q8_0', 'Separation')
)
$revisions = @{
    'audio-cpp/audio.cpp-gguf' = 'e36610ac69b5262e914a52635324050bee8f1ad2'
    'audio-cpp/Nemotron-3-Diarization-GGUF' = '647d39feaa0e91dca5ce355a95403837b76dff56'
}
$catalog = foreach ($definition in $definitions) {
    $spec = Get-Content -Raw -LiteralPath (Join-Path $upstream ($definition[0] + '.json')) | ConvertFrom-Json
    $package = $spec.packages | Where-Object id -EQ $definition[1]
    $repo = $spec.package_defaults.download.repo
    $revision = $revisions[$repo]
    $files = @(foreach ($remotePath in $package.files) {
        $parent = ($remotePath -split '/')[0..([Math]::Max(0, ($remotePath -split '/').Length - 2))] -join '/'
        $treeUrl = if ($remotePath.Contains('/')) { "https://huggingface.co/api/models/$repo/tree/$revision/$parent" } else { "https://huggingface.co/api/models/$repo/tree/$revision" }
        $entries = Invoke-RestMethod $treeUrl
        $entry = $entries | Where-Object path -EQ $remotePath
        if (-not $entry.lfs.oid) { throw "Missing SHA-256 for $remotePath" }
        $fileName = if ($package.strip_prefix) { $remotePath.Substring($package.strip_prefix.Length + 1) } else { $remotePath }
        @{ path = $package.target_directory + '/' + $fileName; url = "https://huggingface.co/$repo/resolve/$revision/$remotePath"; size = $entry.size; sha256 = $entry.lfs.oid }
    })
    $capabilities = @{}
    if ($definition[2] -eq 'Alignment') { $capabilities.supportsWordTimestamps = $true }
    if ($definition[2] -eq 'Diarization') { $capabilities.maxSpeakers = 8 }
    $mappings = @{}
    if ($definition[2] -eq 'Tts') {
        $capabilities = @{ supportsVoiceCloning = $true; supportsEmotionReferenceAudio = $true; supportsEmotionLabels = $true; supportsEmotionVector = $true; supportsEmotionInstruction = $true; supportsSeed = $true }
        $mappings = @{ emotionAudio = 'audio'; emotionIntensity = 'emotion_alpha'; emotionText = 'emotion_text'; emotionTextEnabled = 'use_emotion_text'; emotionVector = 'emotion_vector' }
    }
    $languageNames = @{}
    if ($definition[0].StartsWith('qwen3_')) {
        foreach ($language in $spec.languages) {
            try { $languageNames[$language] = [Globalization.CultureInfo]::GetCultureInfo($language).EnglishName.Split('(')[0].Trim() } catch { }
        }
        $languageNames['zh'] = 'Chinese'
    }
    @{ id = $package.id; provider = 'audio.cpp'; displayName = $package.display_name; family = $spec.family; task = $definition[2];
        entryPoint = $files[0].path; version = "0.9.0/$revision"; source = "https://huggingface.co/$repo"; license = 'See source model license';
        languages = @($spec.languages); capabilities = $capabilities; fieldMappings = $mappings; languageNames = $languageNames; files = $files }
}
$catalog | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $root 'engine-catalog/audio-cpp/models.json') -Encoding utf8
$repo = 'Qwen/Qwen3-4B-GGUF'; $revision = 'bc640142c66e1fdd12af0bd68f40445458f3869b'
$entries = Invoke-RestMethod "https://huggingface.co/api/models/$repo/tree/$revision"
$entry = $entries | Where-Object { $_.path -match '(?i)q4_k_m.gguf$' } | Select-Object -First 1
if (-not $entry) { throw 'Translation GGUF not found.' }
@(@{ id = 'qwen3-4b-q4km'; provider = 'llama.cpp'; displayName = 'Qwen3 4B Q4_K_M'; family = 'gguf'; task = 'Translation';
    entryPoint = 'translation/' + $entry.path; version = $revision; license = 'Apache-2.0'; source = "https://huggingface.co/$repo";
    files = @(@{ path = 'translation/' + $entry.path; url = "https://huggingface.co/$repo/resolve/$revision/$($entry.path)"; size = $entry.size; sha256 = $entry.lfs.oid })
}) | ConvertTo-Json -Depth 15 -AsArray | Set-Content -LiteralPath (Join-Path $root 'engine-catalog/llama-cpp/models.json') -Encoding utf8
