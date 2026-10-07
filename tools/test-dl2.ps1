$uaIos = 'com.google.ios.youtube/20.10.4 (iPhone16,2; U; CPU iOS 17_5_1 like Mac OS X;)'
$uaVr = 'com.google.android.apps.youtube.vr.oculus/1.60.19 (Linux; U; Android 12L; Quest 3 Build/SQ3A.220605.009.A1) gzip'

# fresh visitorData
$swRaw = curl.exe -s 'https://www.youtube.com/sw.js_data' -H "User-Agent: $uaIos" -H 'Accept: application/json' -H 'Cookie: CONSENT=YES+cb.20210328-17-p0.en+FX+678' --compressed
$swTxt = $swRaw -join "`n"
$visitor = ($swTxt.Substring($swTxt.IndexOf('[[')) | ConvertFrom-Json)[0][2][0][0][13]
"visitor: $($visitor.Substring(0,24))..."

# fresh IOS player call
$body = @{
    videoId        = 'jX1m45CwvJ8'
    contentCheckOk = $true
    context        = @{ client = @{
        clientName = 'IOS'; clientVersion = '20.10.4'
        deviceMake = 'Apple'; deviceModel = 'iPhone16,2'
        osName = 'iOS'; osVersion = '17.5.1.21F90'
        platform = 'MOBILE'; hl = 'en'; gl = 'US'
        utcOffsetMinutes = 0; visitorData = $visitor
    } }
} | ConvertTo-Json -Depth 6 -Compress
[System.IO.File]::WriteAllText("$env:TEMP\ios-body.json", $body, [System.Text.UTF8Encoding]::new($false))
curl.exe -s 'https://www.youtube.com/youtubei/v1/player' -H "User-Agent: $uaIos" -H 'Content-Type: application/json' `
  -H 'Cookie: CONSENT=YES+cb.20210328-17-p0.en+FX+678' --data-binary "@$env:TEMP\ios-body.json" -o "$env:TEMP\ios-fresh.json" -w 'player HTTP %{http_code}' | Out-Host

$j = [System.IO.File]::ReadAllText("$env:TEMP\ios-fresh.json") | ConvertFrom-Json
$fmts = @($j.streamingData.formats) + @($j.streamingData.adaptiveFormats)
$v = $fmts | Where-Object { $_.itag -eq 136 } | Select-Object -First 1
$a = $fmts | Where-Object { $_.itag -eq 140 } | Select-Object -First 1
$u = $v.url
"playability: $($j.playabilityStatus.status)"
if ($u -match 'expire=(\d+)') { $exp = [int]$Matches[1]; "expire epoch: $exp (now: $([DateTimeOffset]::UtcNow.ToUnixTimeSeconds()), diff: $($exp - [DateTimeOffset]::UtcNow.ToUnixTimeSeconds())s)" }

# Test A: plain GET, IOS UA, first 1MB via -Range, PS native stack
function Try-Dl($label, $url, $headers) {
    try {
        $req = [System.Net.HttpWebRequest]::Create($url)
        $req.UserAgent = $headers['User-Agent']
        $req.AddRange(0, 1048575)
        foreach ($hk in $headers.Keys) { if ($hk -ne 'User-Agent') { $req.Headers[$hk] = $headers[$hk] } }
        $req.Timeout = 30000
        $resp = $req.GetResponse()
        $s = $resp.GetResponseStream()
        $buf = New-Object byte[] 65536
        $read = $s.Read($buf, 0, 65536)
        "$label -> OK status=$([int]$resp.StatusCode) ct=$($resp.ContentType) firstRead=$read magic=$([System.Text.Encoding]::ASCII.GetString($buf[4..7]))"
        $resp.Close()
    } catch [System.Net.WebException] {
        $r = $_.Exception.Response
        if ($r) { "$label -> FAIL $([int]$r.StatusCode) $($r.StatusCode)" } else { "$label -> FAIL $($_.Exception.Message)" }
    } catch { "$label -> ERR $($_.Exception.Message)" }
}

Try-Dl 'range+iosUA'      $u @{ 'User-Agent' = $uaIos }
Try-Dl 'range+vrUA'       $u @{ 'User-Agent' = $uaVr }
Try-Dl 'range+iosUA+visit' $u @{ 'User-Agent' = $uaIos; 'X-Goog-Visitor-Id' = $visitor }
