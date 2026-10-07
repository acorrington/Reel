$uaIos = 'com.google.ios.youtube/20.10.4 (iPhone16,2; U; CPU iOS 17_5_1 like Mac OS X;)'

# reuse the player response from test-403-ab (URLs stay valid for hours)
$j = [System.IO.File]::ReadAllText("$env:TEMP\ab-player.json") | ConvertFrom-Json
$fmts = @($j.streamingData.formats) + @($j.streamingData.adaptiveFormats)
$url1 = ($fmts | Where-Object { $_.itag -eq 136 -and $_.url } | Select-Object -First 1).url

function Get-Chunk($url, $from, $to, $drain) {
    try {
        $req = [System.Net.HttpWebRequest]::Create($url)
        $req.UserAgent = $uaIos
        $req.AddRange($from, $to)
        $req.Timeout = 30000
        $resp = $req.GetResponse()
        $s = $resp.GetResponseStream()
        $bytes = 0
        if ($drain) {
            $buf = New-Object byte[] 65536
            while (($n = $s.Read($buf, 0, 65536)) -gt 0) { $bytes += $n }
        } else {
            $buf = New-Object byte[] 65536
            $s.Read($buf, 0, 65536) | Out-Null
        }
        $total = $resp.Headers['Content-Range']
        $resp.Close()
        return @{ ok = $true; code = 206; bytes = $bytes; cr = $total }
    } catch [System.Net.WebException] {
        $r = $_.Exception.Response
        if ($r) { $c = [int]$r.StatusCode; $r.Close(); return @{ ok = $false; code = $c } }
        return @{ ok = $false; code = -1 }
    }
}

# step 1: 1MB chunks until failure — record request count and cumulative bytes
$offset = 0; $cumulative = 0; $n = 0
while ($offset -lt 11093766) {
    $end = [Math]::Min($offset + 1048575, 11093765)
    $r = Get-Chunk $url1 $offset $end $true
    $n++
    if (-not $r.ok) {
        "FAILED at request #$n offset=$offset cumulative=$cumulative code=$($r.code)"
        $failOffset = $offset
        break
    }
    $cumulative += $r.bytes
    $offset = $end + 1
}
if ($offset -ge 11093766) { "completed without failure ($n requests, $cumulative bytes)" }

# step 2: immediate retry, SAME url, SAME failing range
$r2 = Get-Chunk $url1 $failOffset ([Math]::Min($failOffset + 1048575, 11093765)) $false
"immediate retry same url: code=$($r2.code)"

# step 3: wait 6s, retry same url
Start-Sleep -Seconds 6
$r3 = Get-Chunk $url1 $failOffset ([Math]::Min($failOffset + 1048575, 11093765)) $false
"after 6s wait same url: code=$($r3.code)"

# step 4: FRESH url, same offset
$consent = 'CONSENT=YES+cb.20210328-17-p0.en+FX+678'
$swRaw = (curl.exe -s 'https://www.youtube.com/sw.js_data' -H "User-Agent: $uaIos" -H 'Accept: application/json' -H "Cookie: $consent" --compressed) -join "`n"
$visitor = ($swRaw.Substring($swRaw.IndexOf('[[')) | ConvertFrom-Json)[0][2][0][0][13]
$body = @{
    videoId = 'jX1m45CwvJ8'; contentCheckOk = $true
    context = @{ client = @{
        clientName = 'IOS'; clientVersion = '20.10.4'
        deviceMake = 'Apple'; deviceModel = 'iPhone16,2'
        osName = 'iOS'; osVersion = '17.5.1.21F90'
        platform = 'MOBILE'; hl = 'en'; gl = 'US'
        utcOffsetMinutes = 0; visitorData = $visitor } }
} | ConvertTo-Json -Depth 6 -Compress
[System.IO.File]::WriteAllText("$env:TEMP\budget-body.json", $body, [System.Text.UTF8Encoding]::new($false))
curl.exe -s 'https://www.youtube.com/youtubei/v1/player' -H "User-Agent: $uaIos" -H 'Content-Type: application/json' -H "Cookie: $consent" --data-binary "@$env:TEMP\budget-body.json" -o "$env:TEMP\budget-player.json"
$j2 = [System.IO.File]::ReadAllText("$env:TEMP\budget-player.json") | ConvertFrom-Json
$fmts2 = @($j2.streamingData.formats) + @($j2.streamingData.adaptiveFormats)
$url2 = ($fmts2 | Where-Object { $_.itag -eq 136 -and $_.url } | Select-Object -First 1).url
if ($url2 -eq $url1) { 'fresh call returned SAME url (query-identical)' } else { 'fresh call returned a DIFFERENT url' }
$r4 = Get-Chunk $url2 $failOffset ([Math]::Min($failOffset + 1048575, 11093765)) $false
"fresh url same offset: code=$($r4.code)"

# step 5: fresh url, offset 0 probe (sanity)
$r5 = Get-Chunk $url2 0 65535 $false
"fresh url probe [0-64K]: code=$($r5.code)"
