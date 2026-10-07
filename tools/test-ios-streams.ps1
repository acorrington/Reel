$j = [System.IO.File]::ReadAllText("$env:TEMP\yt-m-ios-out.json") | ConvertFrom-Json
$fmts = @($j.streamingData.formats) + @($j.streamingData.adaptiveFormats)
"videoDetails: $($j.videoDetails.title) | $($j.videoDetails.author) | $($j.videoDetails.lengthSeconds)s"
"status: $($j.playabilityStatus.status)"
''
'--- all mp4 video streams ---'
@($fmts | Where-Object { $_.mimeType -like 'video/mp4*' } | Sort-Object -Property height -Descending) | ForEach-Object {
    "  {0}p itag={1} type={2} direct={3}" -f $_.height, $_.itag, $_.mimeType, [bool]$_.url
}
'--- audio streams ---'
@($fmts | Where-Object { $_.mimeType -like 'audio/*' } | Sort-Object -Property bitrate -Descending) | ForEach-Object {
    "  {0}kbps itag={1} type={2} direct={3}" -f [int]($_.bitrate/1000), $_.itag, $_.mimeType, [bool]$_.url
}

# pick best mp4 video + best m4a audio
$bestVideo = $fmts | Where-Object { $_.mimeType -like 'video/mp4*' -and $_.url } | Sort-Object -Property height -Descending | Select-Object -First 1
$bestAudio = $fmts | Where-Object { $_.mimeType -like 'audio/mp4*' -and $_.url } | Sort-Object -Property bitrate -Descending | Select-Object -First 1
''
"bestVideo: $($bestVideo.height)p itag=$($bestVideo.itag)"
"bestAudio: $([int]($bestAudio.bitrate/1000))kbps itag=$($bestAudio.itag)"

# test real download: first 2MB of each stream (range request) via curl config file
$uaVr = 'com.google.android.apps.youtube.vr.oculus/1.60.19 (Linux; U; Android 12L; Quest 3 Build/SQ3A.220605.009.A1) gzip'
$vidOk = $null
$uaChrome = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36'
foreach ($pair in @(@('video', $bestVideo.url), @('audio', $bestAudio.url))) {
    $name = $pair[0]; $u = $pair[1]
    $cfg = "url = `"$u`"`nheader = `"User-Agent: $uaVr`"`nrange = 0-2097151`""
    [System.IO.File]::WriteAllText("$env:TEMP\curl-$name.cfg", $cfg)
    $out = curl.exe -s -K "$env:TEMP\curl-$name.cfg" -o "$env:TEMP\dl-$name.bin" -w '%{http_code} %{size_download}'
    "download $name -> $out"
    $head = [System.IO.File]::ReadAllBytes("$env:TEMP\dl-$name.bin")[0..3]
    "  magic: $([System.Text.Encoding]::ASCII.GetString($head))"
}
