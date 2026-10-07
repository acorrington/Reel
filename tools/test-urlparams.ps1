$uaIos = 'com.google.ios.youtube/20.10.4 (iPhone16,2; U; CPU iOS 17_5_1 like Mac OS X;)'
$consent = 'CONSENT=YES+cb.20210328-17-p0.en+FX+678'

function Get-StreamUrl($videoId) {
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
    [System.IO.File]::WriteAllText("$env:TEMP\pu-body.json", $body, [System.Text.UTF8Encoding]::new($false))
    curl.exe -s 'https://www.youtube.com/youtubei/v1/player' -H "User-Agent: $uaIos" -H 'Content-Type: application/json' -H "Cookie: $consent" --data-binary "@$env:TEMP\pu-body.json" -o "$env:TEMP\pu-player.json"
    $j = [System.IO.File]::ReadAllText("$env:TEMP\pu-player.json") | ConvertFrom-Json
    $fmts = @($j.streamingData.formats) + @($j.streamingData.adaptiveFormats)
    # mimic plugin selection: avc1 video highest height
    $v = $fmts | Where-Object { $_.mimeType -like 'video/mp4*' -and $_.mimeType -like '*avc1*' -and $_.url } | Sort-Object { -$_.height } | Select-Object -First 1
    return @{ url = $v.url; itag = $v.itag; height = $v.height; mime = $v.mimeType }
}

function Show-Params($label, $u) {
    $uri = [Uri]$u
    "=== $label ==="
    "host: $($uri.Host)"
    $pairs = $uri.Query.TrimStart('?').Split('&')
    foreach ($p in $pairs) {
        $kv = $p.Split('=', 2)
        $val = if ($kv.Length -gt 1) { $kv[1] } else { '' }
        if ($val.Length -gt 60) { $val = $val.Substring(0, 60) + '...' }
        "  $($kv[0]) = $val"
    }
    ''
}

function T($label, $url, $from, $to, $drain) {
    try {
        $req = [System.Net.HttpWebRequest]::Create($url)
        $req.UserAgent = $uaIos
        $req.AddRange($from, $to)
        $req.Timeout = 30000
        $resp = $req.GetResponse()
        $s = $resp.GetResponseStream()
        $bytes = 0
        $buf = New-Object byte[] 65536
        while ($drain -and ($n = $s.Read($buf, 0, 65536)) -gt 0) { $bytes += $n }
        if (-not $drain) { $s.Read($buf, 0, 65536) | Out-Null }
        $cr = $resp.Headers['Content-Range']
        $resp.Close()
        "  $label -> 206 (drained=$bytes) cr=$cr"
    } catch [System.Net.WebException] {
        $r = $_.Exception.Response
        if ($r) { "  $label -> $([int]$r.StatusCode)"; $r.Close() } else { "  $label -> EXC $($_.Exception.Message)" }
    }
}

# resolve 1408 (failed at chunk 1) and 13 Going on 30 (failed at chunk 8)
$a = Get-StreamUrl 'lgLm4_fq6GY'   # 1408
"1408: itag=$($a.itag) height=$($a.height)"
$b = Get-StreamUrl '_pmFp2W65Fs'   # 13 Going on 30
"13Gon30: itag=$($b.itag) height=$($b.height)"

Show-Params '1408 itag' $a.url
Show-Params '13Gon30 itag' $b.url

'--- range tests on BOTH ---'
'1408:'
T 'probe [0-64K]        ' $a.url 0 65535 $true
T 'chunk [64K-4.26M]    ' $a.url 65536 4259839 $true
T 'chunk2 [4.26M-8.45M] ' $a.url 4259840 8454143 $true
'13Gon30:'
T 'probe [0-64K]        ' $b.url 0 65535 $true
T 'chunk [64K-4.26M]    ' $b.url 65536 4259839 $true
T 'chunk2 [4.26M-8.45M] ' $b.url 4259840 8454143 $true
T 'chunk3 [8.45M-12.6M] ' $b.url 8454144 12648447 $true
T 'chunk7 [25.2M-29.4M] ' $b.url 25231360 29425663 $true
