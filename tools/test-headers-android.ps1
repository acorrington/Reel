$consent = 'CONSENT=YES+cb.20210328-17-p0.en+FX+678'
$uaIos = 'com.google.ios.youtube/20.10.4 (iPhone16,2; U; CPU iOS 17_5_1 like Mac OS X;)'

function Get-PlayerFor($videoId, $ua, $ctx) {
    $swRaw = (curl.exe -s 'https://www.youtube.com/sw.js_data' -H "User-Agent: $ua" -H 'Accept: application/json' -H "Cookie: $consent" --compressed) -join "`n"
    $visitor = $null
    try { $visitor = ($swRaw.Substring($swRaw.IndexOf('[[')) | ConvertFrom-Json)[0][2][0][0][13] } catch {}
    if ($visitor) { $ctx['visitorData'] = $visitor }
    $body = @{ videoId = $videoId; contentCheckOk = $true; racyCheckOk = $true; context = @{ client = $ctx } } | ConvertTo-Json -Depth 6 -Compress
    [System.IO.File]::WriteAllText("$env:TEMP\m-body.json", $body, [System.Text.UTF8Encoding]::new($false))
    $code = curl.exe -s 'https://www.youtube.com/youtubei/v1/player' -H "User-Agent: $ua" -H 'Content-Type: application/json' -H "Cookie: $consent" --data-binary "@$env:TEMP\m-body.json" -o "$env:TEMP\m-resp.json" -w '%{http_code}'
    return @{ code = $code; json = [System.IO.File]::ReadAllText("$env:TEMP\m-resp.json") }
}

$iosCtx = @{ clientName = 'IOS'; clientVersion = '20.10.4'; deviceMake = 'Apple'; deviceModel = 'iPhone16,2'
    osName = 'iOS'; osVersion = '17.5.1.21F90'; platform = 'MOBILE'; hl = 'en'; gl = 'US'; utcOffsetMinutes = 0 }

function T($label, $url, $ua, $from, $to, $extraHeaders) {
    try {
        $req = [System.Net.HttpWebRequest]::Create($url)
        $req.UserAgent = $ua
        $req.AddRange($from, $to)
        if ($extraHeaders) { foreach ($k in $extraHeaders.Keys) { $req.Headers[$k] = $extraHeaders[$k] } }
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

'=== A) 1408 full range with yt-dlp-style browser headers ==='
$r = Get-PlayerFor 'lgLm4_fq6GY' $uaIos $iosCtx.Clone()
$j = $r.json | ConvertFrom-Json
$fmts = @($j.streamingData.formats) + @($j.streamingData.adaptiveFormats)
$video = $fmts | Where-Object { $_.itag -eq 135 -and $_.url } | Select-Object -First 1
$clen = 11399810
$browser = @{ 'Accept' = 'text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8'
    'Accept-Language' = 'en-us,en;q=0.5'
    'Sec-Fetch-Mode' = 'navigate'
    'Accept-Encoding' = 'identity'
    'Connection' = 'close' }
T 'plain UA+range [0-clen-1]        ' $video.url $uaIos 0 ($clen - 1) $null
T 'browser-headers [0-clen-1]       ' $video.url $uaIos 0 ($clen - 1) $browser
T 'browser-headers ChromeUA [0-clen]' $video.url 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36' 0 ($clen - 1) $browser

''
'=== B) progressive formats across library videos (IOS) ==='
foreach ($pair in @(@('jX1m45CwvJ8', '3:10'), @('_pmFp2W65Fs', '13Going'), @('UrIbxk7idYA', '300'), @('lgLm4_fq6GY', '1408'), @('0DpvqzPdXoc', '21'))) {
    $r = Get-PlayerFor $pair[0] $uaIos $iosCtx.Clone()
    $j = $r.json | ConvertFrom-Json
    $prog = @($j.streamingData.formats)
    $progUrls = @($prog | Where-Object url).Count
    "$($pair[1]): progressive formats=$($prog.Count) withUrl=$progUrls hls=$([bool]$j.streamingData.hlsManifestUrl)"
}

''
'=== C) ANDROID client variants for 1408 ==='
foreach ($ver in @('19.44.38', '20.10.3')) {
    $ctx = @{ clientName = 'ANDROID'; clientVersion = $ver; androidSdkVersion = 34
        osName = 'Android'; osVersion = '14'; hl = 'en'; gl = 'US' }
    $r = Get-PlayerFor 'lgLm4_fq6GY' 'com.google.android.youtube/' + $ver + ' (Linux; U; Android 14) gzip' $ctx
    $j = $null
    try { $j = $r.json | ConvertFrom-Json } catch {}
    $status = if ($j) { "$($j.playabilityStatus.status) $($j.playabilityStatus.reason)" } else { "PARSE FAIL: $($r.json.Substring(0, [Math]::Min(120, $r.json.Length)))" }
    $n = 0; $hls = $false
    if ($j -and $j.streamingData) { $n = @((@($j.streamingData.formats) + @($j.streamingData.adaptiveFormats)) | Where-Object url).Count; $hls = [bool]$j.streamingData.hlsManifestUrl }
    "ANDROID $ver -> HTTP $($r.code) playability=$status directUrls=$n hls=$hls"
}
