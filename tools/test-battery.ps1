$consent = 'CONSENT=YES+cb.20210328-17-p0.en+FX+678'
$uaIos = 'com.google.ios.youtube/20.10.4 (iPhone16,2; U; CPU iOS 17_5_1 like Mac OS X;)'
$uaAndroid = 'com.google.android.youtube/20.10.3 (Linux; U; Android 14) gzip'
$exe = Join-Path $PSScriptRoot 'h2test\bin\Release\net10.0\h2test.exe'

function Get-Visitor($ua) {
    $swRaw = (curl.exe -s 'https://www.youtube.com/sw.js_data' -H "User-Agent: $ua" -H 'Accept: application/json' -H "Cookie: $consent" --compressed) -join "`n"
    try { return ($swRaw.Substring($swRaw.IndexOf('[[')) | ConvertFrom-Json)[0][2][0][0][13] } catch { return $null }
}
function Get-Player($videoId, $kind) {
    if ($kind -eq 'android') {
        $ua = $uaAndroid
        $ctx = @{ clientName = 'ANDROID'; clientVersion = '20.10.3'; androidSdkVersion = 35
            osName = 'Android'; osVersion = '14'; hl = 'en'; gl = 'US'; utcOffsetMinutes = 0; userAgent = $uaAndroid }
        $hdrs = @('-H', "X-Goog-Api-Format-Version: 2")
    } else {
        $ua = $uaIos
        $ctx = @{ clientName = 'IOS'; clientVersion = '20.10.4'; deviceMake = 'Apple'; deviceModel = 'iPhone16,2'
            osName = 'iOS'; osVersion = '17.5.1.21F90'; platform = 'MOBILE'; hl = 'en'; gl = 'US'; utcOffsetMinutes = 0 }
        $hdrs = @()
    }
    $v = Get-Visitor $ua
    if ($v) { $ctx['visitorData'] = $v }
    $body = @{ videoId = $videoId; contentCheckOk = $true; racyCheckOk = $true; context = @{ client = $ctx } } | ConvertTo-Json -Depth 6 -Compress
    [System.IO.File]::WriteAllText("$env:TEMP\bat-body.json", $body, [System.Text.UTF8Encoding]::new($false))
    $h = @('-H', "User-Agent: $ua", '-H', 'Content-Type: application/json', '-H', "Cookie: $consent") + $hdrs
    curl.exe -s 'https://www.youtube.com/youtubei/v1/player' @h --data-binary "@$env:TEMP\bat-body.json" -o "$env:TEMP\bat-resp.json" | Out-Null
    return ([System.IO.File]::ReadAllText("$env:TEMP\bat-resp.json") | ConvertFrom-Json)
}
function Get-Param($u, $name) { $m = [regex]::Match($u, "(?:[?&])$name=([^&]*)"); if ($m.Success) { $m.Groups[1].Value } else { $null } }
function Probe($label, $urlFile, $ua, $to) {
    $clen = [long](Get-Param ([System.IO.File]::ReadAllText($urlFile)) 'clen')
    if ($to -ge $clen) { $to = $clen - 1 }
    $r = & $exe $urlFile $ua 0 $to h1
    "$label  ($([Math]::Round($to/1MB,1))MB of $([Math]::Round($clen/1MB,1))MB): $r"
}

'=== 1) ANDROID itag18 progressive FULL range ==='
$j = Get-Player 'lgLm4_fq6GY' 'android'
$fmts = @($j.streamingData.formats) + @($j.streamingData.adaptiveFormats)
$p18 = $fmts | Where-Object { $_.itag -eq 18 -and $_.url } | Select-Object -First 1
if ($p18) {
    [System.IO.File]::WriteAllText("$env:TEMP\p18-url.txt", $p18.url)
    Probe 'itag18 full' "$env:TEMP\p18-url.txt" $uaAndroid 999999999
} else { 'itag18 not present/direct' }

''
'=== 2) calm recheck: 1408 IOS fresh FULL range ==='
$j = Get-Player 'lgLm4_fq6GY' 'ios'
$fmts = @($j.streamingData.formats) + @($j.streamingData.adaptiveFormats)
$v = $fmts | Where-Object { $_.itag -eq 135 -and $_.url } | Select-Object -First 1
[System.IO.File]::WriteAllText("$env:TEMP\i135-url.txt", $v.url)
Probe '1408-iOS full' "$env:TEMP\i135-url.txt" $uaIos 999999999
Probe '1408-iOS 3.8M' "$env:TEMP\i135-url.txt" $uaIos 3984588

''
'=== 3) 13Gon30 itag136 (does the big cap extend to 720p?) ==='
$j = Get-Player '_pmFp2W65Fs' 'ios'
$fmts = @($j.streamingData.formats) + @($j.streamingData.adaptiveFormats)
$v136 = $fmts | Where-Object { $_.itag -eq 136 -and $_.url } | Select-Object -First 1
if ($v136) {
    [System.IO.File]::WriteAllText("$env:TEMP\g136-url.txt", $v136.url)
    Probe '13Gon-136 full' "$env:TEMP\g136-url.txt" $uaIos 999999999
    Probe '13Gon-136 8M' "$env:TEMP\g136-url.txt" $uaIos 8912895
} else { 'itag136 not present' }

''
'=== 4) jX exact 4MiB boundary ==='
[System.IO.File]::WriteAllText("$env:TEMP\jx-url.txt", ([System.IO.File]::ReadAllText("$env:TEMP\h2-video-url.txt")))
Probe 'jX to=4194303 (4MiB-1)' "$env:TEMP\jx-url.txt" $uaIos 4194303
Probe 'jX to=4194304 (4MiB)' "$env:TEMP\jx-url.txt" $uaIos 4194304
