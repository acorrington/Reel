$ErrorActionPreference = 'Stop'
$uaVr = 'com.google.android.apps.youtube.vr.oculus/1.60.19 (Linux; U; Android 12L; Quest 3 Build/SQ3A.220605.009.A1) gzip'

# 1) visitorData from saved raw sw.js_data
$raw = [System.IO.File]::ReadAllText("$env:TEMP\sw-raw.txt")
$start = $raw.IndexOf('[[')
if ($start -lt 0) { throw 'no JSON start' }
$json = $raw.Substring($start)
$doc = $json | ConvertFrom-Json
$visitor = $doc[0][2][0][0][13]
if (-not $visitor) { throw 'visitorData not found at [0][2][0][0][13]' }
"visitorData OK: $($visitor.Substring(0,40))... (len=$($visitor.Length))"

# 2) InnerTube player request (no api key, like decompiled code; plus visitorData)
$body = @{
    videoId        = 'jX1m45CwvJ8'
    contentCheckOk = $true
    context        = @{
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
[System.IO.File]::WriteAllText("$env:TEMP\yt-player-body.json", $body, [System.Text.UTF8Encoding]::new($false))

$resp = curl.exe -s 'https://www.youtube.com/youtubei/v1/player' `
  -H "User-Agent: $uaVr" `
  -H 'Content-Type: application/json' `
  -H 'X-Youtube-Client-Name: 28' `
  -H 'X-Youtube-Client-Version: 1.60.19' `
  -H 'Cookie: CONSENT=YES+cb.20210328-17-p0.en+FX+678' `
  --data-binary "@$env:TEMP\yt-player-body.json" `
  -o "$env:TEMP\yt-player-resp.json" -w 'HTTP:%{http_code}'
''
$respText = [System.IO.File]::ReadAllText("$env:TEMP\yt-player-resp.json")
$j = $respText | ConvertFrom-Json
"playability: $($j.playabilityStatus.status) $($j.playabilityStatus.reason)"
$sd = $j.streamingData
if ($sd) {
    $fmts = @($sd.formats) + @($sd.adaptiveFormats)
    "total formats: $($fmts.Count)"
    $withUrl = @($fmts | Where-Object { $_.url })
    "with direct url: $($withUrl.Count)"
    $withCipher = @($fmts | Where-Object { $_.signatureCipher -or $_.cipher })
    "with cipher only: $($withCipher.Count)"
    "--- best mp4 video (direct url) ---"
    @($fmts | Where-Object { $_.mimeType -like 'video/mp4*' -and $_.url } | Sort-Object -Property height -Descending | Select-Object -First 3) |
        ForEach-Object { "  {0}p itag={1}" -f $_.height, $_.itag }
    "--- best audio/mp4 (direct url) ---"
    @($fmts | Where-Object { $_.mimeType -like 'audio/mp4*' -and $_.url } | Sort-Object -Property bitrate -Descending | Select-Object -First 3) |
        ForEach-Object { "  {0}kbps itag={1}" -f [int]($_.bitrate / 1000), $_.itag }
} else {
    "NO streamingData. First 400 chars:"
    $respText.Substring(0, [Math]::Min(400, $respText.Length))
}
