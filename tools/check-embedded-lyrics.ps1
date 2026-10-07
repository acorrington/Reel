# Scans MP3 files for embedded ID3v2 lyrics (USLT / SYLT frames).
# Walks frame headers (v2.3/v2.4) so embedded cover art can't false-positive.
param([string[]]$Roots)

function Get-EmbeddedLyricsFrame {
    param([string]$Path, [switch]$WantSample)
    $result = @{ Has = $false; Sample = $null; Error = $null }
    $fs = $null
    try {
        $fs = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
        $hdr = New-Object byte[] 10
        $got = 0
        while ($got -lt 10) { $n = $fs.Read($hdr, $got, 10 - $got); if ($n -le 0) { break }; $got += $n }
        if ($got -lt 10) { $result.Error = 'short header'; return $result }
        if ($hdr[0] -ne 0x49 -or $hdr[1] -ne 0x44 -or $hdr[2] -ne 0x33) { return $result }  # no ID3v2 tag
        $ver = $hdr[3]
        $flags = $hdr[5]
        if ($ver -gt 4) { $result.Error = "id3v2.$ver unsupported"; return $result }
        $tagSize = (($hdr[6] -band 0x7F) -shl 21) -bor (($hdr[7] -band 0x7F) -shl 14) -bor (($hdr[8] -band 0x7F) -shl 7) -bor ($hdr[9] -band 0x7F)
        $tagEnd = 10 + $tagSize
        if ($flags -band 0x10) { $tagEnd += 10 }  # footer
        $pos = 10
        if ($flags -band 0x40) {  # extended header
            $eh = New-Object byte[] 4
            $fs.Seek(10, 'Begin') | Out-Null
            if ($fs.Read($eh, 0, 4) -lt 4) { $result.Error = 'short ext hdr'; return $result }
            if ($ver -ge 4) {
                $ehSize = (($eh[0] -band 0x7F) -shl 21) -bor (($eh[1] -band 0x7F) -shl 14) -bor (($eh[2] -band 0x7F) -shl 7) -bor ($eh[3] -band 0x7F)
                $pos = 10 + $ehSize
            } else {
                $ehSize = (($eh[0] -shl 24) -bor ($eh[1] -shl 16) -bor ($eh[2] -shl 8) -bor $eh[3]) + 4
                $pos = 10 + $ehSize
            }
        }
        $fh = New-Object byte[] 10
        while ($pos + 10 -le $tagEnd) {
            $fs.Seek($pos, 'Begin') | Out-Null
            if ($fs.Read($fh, 0, 10) -lt 10) { break }
            $id = [System.Text.Encoding]::ASCII.GetString($fh, 0, 4)
            if ($id[0] -eq 0) { break }  # padding reached
            $fsize = if ($ver -ge 4) {
                (($fh[4] -band 0x7F) -shl 21) -bor (($fh[5] -band 0x7F) -shl 14) -bor (($fh[6] -band 0x7F) -shl 7) -bor ($fh[7] -band 0x7F)
            } else {
                ($fh[4] -shl 24) -bor ($fh[5] -shl 16) -bor ($fh[6] -shl 8) -bor $fh[7]
            }
            if ($fsize -le 0 -or $pos + 10 + $fsize -gt $tagEnd + 64) { break }  # corrupt
            if ($id -eq 'USLT' -or $id -eq 'SYLT') {
                $result.Has = $true
                if ($WantSample) {
                    $len = [Math]::Min($fsize, 400)
                    $body = New-Object byte[] $len
                    $fs.Seek($pos + 10, 'Begin') | Out-Null
                    $fs.Read($body, 0, $len) | Out-Null
                    if ($id -eq 'USLT' -and $len -gt 4) {
                        $enc = $body[0]
                        $txtBytes = $body[4..($len - 1)]   # after encoding + lang(3); description terminator skipped (best-effort)
                        $txt = switch ($enc) {
                            0 { [System.Text.Encoding]::GetEncoding(28591).GetString($txtBytes) }
                            1 { [System.Text.Encoding]::Unicode.GetString($txtBytes) }
                            2 { [System.Text.Encoding]::BigEndianUnicode.GetString($txtBytes) }
                            default { [System.Text.Encoding]::UTF8.GetString($txtBytes) }
                        }
                        $txt = ($txt -replace "\0+", '').Trim()
                        if ($txt.Length -gt 300) { $txt = $txt.Substring(0, 300) + '…' }
                        $result.Sample = $txt
                    }
                }
                return $result
            }
            $pos += 10 + $fsize
        }
        return $result
    } catch {
        $result.Error = $_.Exception.Message
        return $result
    } finally {
        if ($fs) { $fs.Close() }
    }
}

foreach ($root in $Roots) {
    "=== $root ==="
    if (-not (Test-Path $root)) { "  path not found"; continue }
    $mp3s = Get-ChildItem $root -Recurse -File -Filter '*.mp3' -ErrorAction SilentlyContinue
    "  mp3 files: $($mp3s.Count)"
    $withLyrics = @()
    $errors = 0
    $done = 0
    foreach ($f in $mp3s) {
        $done++
        $r = Get-EmbeddedLyricsFrame -Path $f.FullName
        if ($r.Has) { $withLyrics += $f.FullName }
        if ($r.Error) { $errors++ }
        if ($done % 500 -eq 0) { "  ...$done/$($mp3s.Count) (lyrics so far: $($withLyrics.Count))" }
    }
    "  WITH embedded lyrics: $($withLyrics.Count)"
    "  scan errors: $errors"
    $withLyrics | Select-Object -First 10 | ForEach-Object { "    LYR: $_" }
    if ($withLyrics.Count -gt 10) { "    ... and $($withLyrics.Count - 10) more" }
    if ($withLyrics.Count -gt 0) {
        $s = Get-EmbeddedLyricsFrame -Path $withLyrics[0] -WantSample
        if ($s.Sample) { "  sample from first hit:"; "    $($s.Sample.Substring(0, [Math]::Min(200, $s.Sample.Length)))" }
    }
    $lrc = Get-ChildItem $root -Recurse -File -Filter '*.lrc' -ErrorAction SilentlyContinue
    "  sidecar .lrc files: $($lrc.Count)"
    ''
}
