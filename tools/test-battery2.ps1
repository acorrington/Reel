$consent = 'CONSENT=YES+cb.20210328-17-p0.en+FX+678'
$uaIos = 'com.google.ios.youtube/20.10.4 (iPhone16,2; U; CPU iOS 17_5_1 like Mac OS X;)'
$uaAndroid = 'com.google.android.youtube/20.10.3 (Linux; U; Android 14) gzip'

function Get-Visitor($ua) {
    $swRaw = (curl.exe -s 'https://www.youtube.com/sw.js_data' -H "User-Agent: $ua" -H 'Accept: application/json' -H "Cookie: $consent" --compressed) -join "`n"
    try { return ($swRaw.Substring($swRaw.IndexOf('[[')) | ConvertFrom-Json)[0][2][0][0][13] } catch { return $null }
}
function Get-Player($videoId, $kind) {
    if ($kind -eq 'android') {
        $ua = $uaAndroid
        $ctx = @{ clientName = 'ANDROID'; clientVersion = '20.10.3'; androidSdkVersion = 35
            osName = 'Android'; osVersion = '14'; hl = 'en'; gl = 'US'; utcOffsetMinutes = 0; userAgent = $uaAndroid }
        $hdrs = @('-H', 'X-Goog-Api-Format-Version: 2')
    } else {
        $ua = $uaIos
        $ctx = @{ clientName = 'IOS'; clientVersion = '20.10.4'; deviceMake = 'Apple'; deviceModel = 'iPhone16,2'
            osName = 'iOS'; osVersion = '17.5.1.21F90'; platform = 'MOBILE'; hl = 'en'; gl = 'US'; utcOffsetMinutes = 0 }
        $hdrs = @()
    }
    $v = Get-Visitor $ua
    if ($v) { $ctx['visitorData'] = $v }
    $body = @{ videoId = $videoId; contentCheckOk = $true; racyCheckOk = $true; context = @{ client = $ctx } } | ConvertTo-Json -Depth 6 -Compress
    [System.IO.File]::WriteAllText("$env:TEMP\bb-body.json", $body, [System.Text.UTF8Encoding]::new($false))
    $h = @('-H', "User-Agent: $ua", '-H', 'Content-Type: application/json', '-H', "Cookie: $consent") + $hdrs
    curl.exe -s 'https://www.youtube.com/youtubei/v1/player' @h --data-binary "@$env:TEMP\bb-body.json" -o "$env:TEMP\bb-resp.json" | Out-Null
    return ([System.IO.File]::ReadAllText("$env:TEMP\bb-resp.json") | ConvertFrom-Json)
}

function T($label, $url, $ua, $from, $to, $browserHeaders, $drainLimit) {
    try {
        $req = [System.Net.HttpWebRequest]::Create($url)
        $req.UserAgent = $ua
        if ($to -ge 0) { $req.AddRange($from, $to) } else { $req.AddRange($from) } # -1 = open range
        if ($browserHeaders) {
            $req.Accept = 'text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8'
            $req.Headers.Add('Accept-Language', 'en-us,en;q=0.5')
            $req.Headers.Add('Sec-Fetch-Mode', 'navigate')
            $req.AcceptEncoding = 'identity'
            $req.KeepAlive = $false   # Connection: close
        }
        $req.Timeout = 30000
        $resp = $req.GetResponse()
        $cr = $resp.Headers['Content-Range']
        $s = $resp.GetResponseStream()
        $buf = New-Object byte[] 65536
        $read = 0
        while ($read -lt $drainLimit -and ($n = $s.Read($buf, 0, 65536)) -gt 0) { $read += $n }
        $resp.Close()
        "  $label -> 206 read=$read cr=$cr"
    } catch [System.Net.WebException] {
        $r = $_.Exception.Response
        if ($r) { "  $label -> $([int]$r.StatusCode)"; $r.Close() } else { "  $label -> EXC $($_.Exception.Message)" }
    }
}

'=== A) browser headers on full range (the test that crashed before) ==='
$j = Get-Player 'lgLm4_fq6GY' 'ios'
$fmts = @($j.streamingData.formats) + @($j.streamingData.adaptiveFormats)
$v = $fmts | Where-Object { $_.itag -eq 135 -and $_.url } | Select-Object -First 1
$clen = 11399810
T 'plain  [0-clen-1]        ' $v.url $uaIos 0 ($clen - 1) $false 1048576
T 'browser [0-clen-1]       ' $v.url $uaIos 0 ($clen - 1) $true 1048576
T 'browser [0-8388607]      ' $v.url $uaIos 0 8388607 $true 1048576

''
'=== B) ANDROID itag18 progressive: probe-derived size + range tests ==='
$j = Get-Player 'lgLm4_fq6GY' 'android'
$fmts = @($j.streamingData.formats) + @($j.streamingData.adaptiveFormats)
$p18 = $fmts | Where-Object { $_.itag -eq 18 -and $_.url } | Select-Object -First 1
if ($p18) {
    $u18 = $p18.url
    "itag18 url head: $($u18.Substring(0, [Math]::Min(120, $u18.Length)))"
    T 'itag18 probe [0-65535]   ' $u18 $uaAndroid 0 65535 $false 4096
    T 'itag18 [0-2097151] 2MB   ' $u18 $uaAndroid 0 2097151 $false 65536
    T 'itag18 [0-4194303] 4MB   ' $u18 $uaAndroid 0 4194303 $false 65536
    T 'itag18 [0-8388607] 8MB   ' $u18 $uaAndroid 0 8388607 $false 65536
} else { 'itag18 not available' }
