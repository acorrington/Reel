$uaIos = 'com.google.ios.youtube/20.10.4 (iPhone16,2; U; CPU iOS 17_5_1 like Mac OS X;)'
$consent = 'CONSENT=YES+cb.20210328-17-p0.en+FX+678'

function Get-Player($videoId) {
    $swRaw = (curl.exe -s 'https://www.youtube.com/sw.js_data' -H "User-Agent: $uaIos" -H 'Accept: application/json' -H "Cookie: $consent" --compressed) -join "`n"
    $visitor = ($swRaw.Substring($swRaw.IndexOf('[[')) | ConvertFrom-Json)[0][2][0][0][13]
    $body = @{
        videoId = $videoId; contentCheckOk = $true
        context = @{ client = @{
            clientName = 'IOS'; clientVersion = '20.10.4'
            deviceMake = 'Apple'; deviceModel = 'iPhone16,2'
            osName = 'iOS'; osVersion = '17.5.1.21F90'
            platform = 'MOBILE'; hl = 'en'; gl = 'US'
            utcOffsetMinutes = 0; visitorData = $visitor } }
    } | ConvertTo-Json -Depth 6 -Compress
    [System.IO.File]::WriteAllText("$env:TEMP\c2-body.json", $body, [System.Text.UTF8Encoding]::new($false))
    curl.exe -s 'https://www.youtube.com/youtubei/v1/player' -H "User-Agent: $uaIos" -H 'Content-Type: application/json' -H "Cookie: $consent" --data-binary "@$env:TEMP\c2-body.json" -o "$env:TEMP\c2-player.json"
    return [System.IO.File]::ReadAllText("$env:TEMP\c2-player.json") | ConvertFrom-Json
}

function T($label, $url, $from, $to) {
    if ($to -le $from -or $to -lt 0) { "  $label -> SKIP (bad range)"; return }
    try {
        $req = [System.Net.HttpWebRequest]::Create($url)
        $req.UserAgent = $uaIos
        $req.AddRange($from, $to)
        $req.Timeout = 30000
        $resp = $req.GetResponse()
        $cr = $resp.Headers['Content-Range']
        $s = $resp.GetResponseStream(); $buf = New-Object byte[] 65536
        while ($s.Read($buf, 0, 65536) -gt 0) {}
        $resp.Close()
        "  $label -> 206 cr=$cr"
    } catch [System.Net.WebException] {
        $r = $_.Exception.Response
        if ($r) { "  $label -> $([int]$r.StatusCode)"; $r.Close() } else { "  $label -> EXC" }
    }
}

$j = Get-Player 'lgLm4_fq6GY'
$fmts = @($j.streamingData.formats) + @($j.streamingData.adaptiveFormats)
$video = $fmts | Where-Object { $_.itag -in 133,134,135,136,137 -and $_.url } | Sort-Object { -$_.height } | Select-Object -First 1
$audio = $fmts | Where-Object { $_.mimeType -like 'audio/mp4*' -and $_.url } | Sort-Object { -$_.bitrate } | Select-Object -First 1

"--- 1408 VIDEO itag=$($video.itag) clen=11399810 (binary-search cap between 2.31M and 4.26M) ---"
T '[0-2621439]  2.62M' $video.url 0 2621439
T '[0-3145727]  3.15M' $video.url 0 3145727
T '[0-3670015]  3.67M' $video.url 0 3670015
T '[0-3932159]  3.93M' $video.url 0 3932159

"--- 1408 AUDIO itag=$($audio.itag) clen=3066820 dur=189.45 ---"
T '[0-65535]      64K' $audio.url 0 65535
T '[0-1048575]    1MB' $audio.url 0 1048575
T '[0-2097151]    2MB' $audio.url 0 2097151
T '[0-3066819]  full ' $audio.url 0 3066819

# time-dependence: the OLD ab url (jX itag136, from 12:08) failed [4194304-5242879] at 12:15 — retest NOW
$jAb = [System.IO.File]::ReadAllText("$env:TEMP\ab-player.json") | ConvertFrom-Json
$fmtsAb = @($jAb.streamingData.formats) + @($jAb.streamingData.adaptiveFormats)
$ab = $fmtsAb | Where-Object { $_.itag -eq 136 -and $_.url } | Select-Object -First 1
"--- OLD ab url (jX itag136) retest of previously-failed range, now several minutes later ---"
T '[4194304-5242879] (was 403)' $ab.url 4194304 5242879
T '[0-4194303] (control, was 206)' $ab.url 0 4194303
