$ErrorActionPreference = 'Stop'
$uaVr = 'com.google.android.apps.youtube.vr.oculus/1.60.19 (Linux; U; Android 12L; Quest 3 Build/SQ3A.220605.009.A1) gzip'

# 1) visitorData from sw.js_data
$sw = curl.exe -s 'https://www.youtube.com/sw.js_data' `
  -H "User-Agent: $uaVr" `
  -H 'Accept: application/json' `
  -H 'Cookie: CONSENT=YES+cb.20210328-17-p0.en+FX+678'
$sw = $sw.TrimStart([char]0x29, [char]0x5d, [char]0x27, [char]0x66)  # strip )]}"' prefix chars if present
try {
    $doc = $sw | ConvertFrom-Json
    $visitor = $doc[0][2][0][0][13]
    "visitorData: $($visitor.Substring(0, [Math]::Min(60, $visitor.Length)))..."
} catch {
    "visitorData parse FAILED: $_"
    $visitor = $null
}

# 2) InnerTube player request
$body = @{
    videoId       = 'jX1m45CwvJ8'   # 3:10 to Yuma official trailer
    contentCheckOk = $true
    context       = @{
        client = @{
            clientName       = 'ANDROID_VR'
            clientVersion    = '1.60.19'
            deviceMake       = 'Oculus'
            deviceModel      = 'Quest 3'
            osName           = 'Android'
            osVersion        = '12L'
            platform         = 'MOBILE'
            hl               = 'en'
            gl               = 'US'
            utcOffsetMinutes = 0
            visitorData      = $visitor
        }
    }
} | ConvertTo-Json -Depth 6 -Compress
[System.IO.File]::WriteAllText("$env:TEMP\yt-player-body.json", $body)

$resp = curl.exe -s 'https://www.youtube.com/youtubei/v1/player?key=AIzaSyA8eiZmM1FaDVjRy-ds2MAqdkv2EEgdwLg' `
  -H "User-Agent: $uaVr" `
  -H 'Content-Type: application/json' `
  -H 'Cookie: CONSENT=YES+cb.20210328-17-p0.en+FX+678' `
  --data-binary "@$env:TEMP\yt-player-body.json"
[System.IO.File]::WriteAllText("$env:TEMP\yt-player-resp.json", $resp)

$j = $resp | ConvertFrom-Json
"playability: $($j.playabilityStatus.status) $($j.playabilityStatus.reason)"
$sd = $j.streamingData
if ($sd) {
    $fmts = @($sd.formats) + @($sd.adaptiveFormats)
    "total formats: $($fmts.Count)"
    $withUrl = @($fmts | Where-Object { $_.url })
    "formats with direct url: $($withUrl.Count)"
    $withCipher = @($fmts | Where-Object { $_.signatureCipher -or $_.cipher })
    "formats with cipher only: $($withCipher.Count)"
    $vids = @($fmts | Where-Object { $_.mimeType -like 'video/mp4*' -and $_.url } | Sort-Object { -$_.height } | Select-Object -First 3)
    "--- best mp4 video ---"
    $vids | ForEach-Object { "  {0}p itag={1} url_len={2}" -f $_.height, $_.itag, $_.url.Length }
    $auds = @($fmts | Where-Object { $_.mimeType -like 'audio/mp4*' -and $_.url } | Sort-Object { -$_.bitrate } | Select-Object -First 3)
    "--- best aac audio ---"
    $auds | ForEach-Object { "  {0}kbps itag={1} url_len={2}" -f [int]($_.bitrate / 1000), $_.itag, $_.url.Length }
} else {
    "NO streamingData"
}
