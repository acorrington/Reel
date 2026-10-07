$j = [System.IO.File]::ReadAllText("$env:TEMP\yt-m-ios-out.json") | ConvertFrom-Json
$fmts = @($j.streamingData.formats) + @($j.streamingData.adaptiveFormats)
# prefer H.264 video (avc1) over AV1, and AAC audio (mp4a)
$bestVideo = $fmts | Where-Object { $_.mimeType -like 'video/mp4*' -and $_.mimeType -like '*avc1*' -and $_.url } | Sort-Object -Property height -Descending | Select-Object -First 1
$bestAudio = $fmts | Where-Object { $_.mimeType -like 'audio/mp4*' -and $_.mimeType -like '*mp4a.40.2*' -and $_.url } | Sort-Object -Property bitrate -Descending | Select-Object -First 1
"video: $($bestVideo.height)p itag=$($bestVideo.itag)"
"audio: $([int]($bestAudio.bitrate/1000))kbps itag=$($bestAudio.itag)"

$uaIos = 'com.google.ios.youtube/20.10.4 (iPhone16,2; U; CPU iOS 17_5_1 like Mac OS X;)'

foreach ($pair in @(@('video', $bestVideo.url), @('audio', $bestAudio.url))) {
    $name = $pair[0]; $u = $pair[1]
    # Attempt 1: IOS UA + range
    $cfg = "url = `"$u`"`nheader = `"User-Agent: $uaIos`"`nrange = 0-1048575`""
    [System.IO.File]::WriteAllText("$env:TEMP\curl-$name.cfg", $cfg)
    $out = curl.exe -s -K "$env:TEMP\curl-$name.cfg" -o "$env:TEMP\dl-$name.bin" -w '%{http_code} %{size_download} %{content_type}'
    "$name (iosUA+range): $out"
    if ($out -like '206*' -or $out -like '200*') { continue }
    # Attempt 2: IOS UA, no range (download whole, cap time)
    $cfg2 = "url = `"$u`"`nheader = `"User-Agent: $uaIos`""
    [System.IO.File]::WriteAllText("$env:TEMP\curl-$name.cfg", $cfg2)
    $out = curl.exe -s -K "$env:TEMP\curl-$name.cfg" -o "$env:TEMP\dl-$name.bin" -w '%{http_code} %{size_download}' --max-time 30
    "$name (iosUA full): $out"
}
# report magic bytes
foreach ($name in @('video','audio')) {
    $p = "$env:TEMP\dl-$name.bin"
    if ((Test-Path $p) -and (Get-Item $p).Length -gt 0) {
        $b = [System.IO.File]::ReadAllBytes($p)
        $magic = [System.Text.Encoding]::ASCII.GetString($b[4..7])
        "  dl-$name : size=$($b.Length) brand=$magic"
    }
}
