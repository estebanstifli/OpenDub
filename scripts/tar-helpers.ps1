# .NET tar handling also works on Windows hosts without symbolic-link privileges.
function Expand-PortableTar([string]$Archive, [string]$Destination) {
    $root = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Destination)) + [IO.Path]::DirectorySeparatorChar
    $inputFile = [IO.File]::OpenRead($Archive)
    $gzip = [IO.Compression.GZipStream]::new($inputFile, [IO.Compression.CompressionMode]::Decompress)
    $reader = [System.Formats.Tar.TarReader]::new($gzip)
    $links = [Collections.Generic.List[object]]::new()
    try {
        while ($entry = $reader.GetNextEntry()) {
            $path = [IO.Path]::GetFullPath((Join-Path $root $entry.Name))
            if (!$path.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { throw 'Archive path escapes destination.' }
            if ($entry.EntryType -eq 'Directory') { [IO.Directory]::CreateDirectory($path) | Out-Null; continue }
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path)) | Out-Null
            if ($entry.EntryType -in @('SymbolicLink','HardLink')) {
                $base = if ($entry.EntryType -eq 'HardLink') { $root } else { [IO.Path]::GetDirectoryName($path) }
                $target = [IO.Path]::GetFullPath((Join-Path $base $entry.LinkName))
                if (!$target.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { throw 'Archive link escapes destination.' }
                $links.Add(@{path=$path;target=$target}); continue
            }
            if ($entry.EntryType -in @('RegularFile','V7RegularFile')) { $entry.ExtractToFile($path, $true) }
        }
        while ($links.Count -gt 0) {
            $copied = 0
            foreach ($link in @($links.ToArray())) {
                if ([IO.File]::Exists($link.target)) { [IO.File]::Copy($link.target, $link.path, $true); $links.Remove($link) | Out-Null; $copied++ }
            }
            if ($copied -eq 0) { throw 'Unresolved native library links.' }
        }
    } finally { $reader.Dispose(); $gzip.Dispose(); $inputFile.Dispose() }
}

function Compress-PortableTar([string]$Directory, [string]$Archive) {
    $root = [IO.Path]::GetFullPath($Directory)
    $prefix = [IO.Path]::GetFileName($root)
    $output = [IO.File]::Create($Archive)
    $gzip = [IO.Compression.GZipStream]::new($output, [IO.Compression.CompressionLevel]::Fastest)
    $writer = [System.Formats.Tar.TarWriter]::new($gzip)
    try {
        foreach ($item in Get-ChildItem -LiteralPath $root -Recurse -Force) {
            $name = $prefix + '/' + [IO.Path]::GetRelativePath($root, $item.FullName).Replace('\','/')
            $type = if ($item.PSIsContainer) { [System.Formats.Tar.TarEntryType]::Directory } else { [System.Formats.Tar.TarEntryType]::RegularFile }
            $entry = [System.Formats.Tar.PaxTarEntry]::new($type, $name)
            $executable = $item.PSIsContainer -or $item.Name -eq 'Dubbing.App' -or $item.Name -match '^(audiocpp_|llama-|ffmpeg$|ffprobe$)' -or $item.Extension -eq '.sh'
            $entry.Mode = [IO.UnixFileMode]$(if ($executable) { 493 } else { 420 })
            if (!$item.PSIsContainer) { $entry.DataStream = [IO.File]::OpenRead($item.FullName) }
            try { $writer.WriteEntry($entry) } finally { if ($entry.DataStream) { $entry.DataStream.Dispose() } }
        }
    } finally { $writer.Dispose(); $gzip.Dispose(); $output.Dispose() }
}
