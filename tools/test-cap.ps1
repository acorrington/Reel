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
    [System.IO.File]::WriteAllText("$env:TEMP\cap-body.json", $body, [System.Text.UTF8Encoding]::new($false))
    curl.exe -s 'https://www.youtube.com/youtubei/v1/player' -H "User-Agent: $uaIos" -H 'Content-Type: application/json' -H "Cookie: $consent" --data-binary "@$env:TEMP\cap-body.json" -o "$env:TEMP\cap-player.json"
    return [System.IO.File]::ReadAllText("$env:TEMP\cap-player.json") | ConvertFrom-Json
}

function T($label, $url, $from, $to) {
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

$j = Get-Player 'lgLm4_fq6GY'  # 1408
$fmts = @($j.streamingData.formats) + @($j.streamingData.adaptiveFormats)
$video = $fmts | Where-Object { $_.itag -in 133,134,135,136,137 -and $_.url } | Sort-Object { -$_.height } | Select-Object -First 1
$audio = $fmts | Where-Object { $_.mimeType -like 'audio/mp4*' -and $_.url } | Sort-Object { -$_.bitrate } | Select-Object -First 1
$prog = $fmts | Where-Object { $_.audioQuality -and $_.mimeType -like 'video/mp4*' -and $_.url } | Select-Object -First 1

function Get-Param($u, $name) {
    $m = [regex]::Match($u, "(?:[?&])$name=([^&]*)")
    if ($m.Success) { return $m.Groups[1].Value }
    return $null
}

foreach ($pair in @(@('video', $video), @('audio', $audio), @('progressive', $prog))) {
    $name = $pair[0]; $f = $pair[1]
    if (-not $f) { "$name : NOT PRESENT"; continue }
    $u = $f.url
    $clen = [long](Get-Param $u 'clen')
    $dur = [double](Get-Param $u 'dur')
    $icw = Get-Param $u 'initcwndbps'
    $itag = $f.itag
    $cap = $clen - $dur * 48587
    "$name (itag=$itag): clen=$clen dur=$dur initcwndbps=$icw -> predicted cap=$([long]$cap)"
    # boundary tests around predicted cap
    $below = [long]($cap * 0.85); $above = [long]($cap * 1.05)
    if ($below -gt 0 -and $below -lt $clen) { T "  [0-$below]  (85% of cap)   " $u 0 ([Math]::Min($below, $clen - 1)) }
    if ($above -lt $clen) { T "  [0-$above]  (105% of cap)  " $u 0 $above }
    # full within-file range if small enough
    if ($clen -le 3000000) { T "  [0-$($clen-1)] (full file) " $u 0 ($clen - 1) }
    ''
}
