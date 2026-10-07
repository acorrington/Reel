$uaIos = 'com.google.ios.youtube/20.10.4 (iPhone16,2; U; CPU iOS 17_5_1 like Mac OS X;)'
$consent = 'CONSENT=YES+cb.20210328-17-p0.en+FX+678'

# fresh visitorData
$swRaw = (curl.exe -s 'https://www.youtube.com/sw.js_data' -H "User-Agent: $uaIos" -H 'Accept: application/json' -H "Cookie: $consent" --compressed) -join "`n"
$visitor = ($swRaw.Substring($swRaw.IndexOf('[[')) | ConvertFrom-Json)[0][2][0][0][13]

# fresh player response
$body = @{
    videoId = 'jX1m45CwvJ8'; contentCheckOk = $true
    context = @{ client = @{
        clientName = 'IOS'; clientVersion = '20.10.4'
        deviceMake = 'Apple'; deviceModel = 'iPhone16,2'
        osName = 'iOS'; osVersion = '17.5.1.21F90'
        platform = 'MOBILE'; hl = 'en'; gl = 'US'
        utcOffsetMinutes = 0; visitorData = $visitor } }
} | ConvertTo-Json -Depth 6 -Compress
[System.IO.File]::WriteAllText("$env:TEMP\ab-body.json", $body, [System.Text.UTF8Encoding]::new($false))
curl.exe -s 'https://www.youtube.com/youtubei/v1/player' -H "User-Agent: $uaIos" -H 'Content-Type: application/json' -H "Cookie: $consent" --data-binary "@$env:TEMP\ab-body.json" -o "$env:TEMP\ab-player.json"
$j = [System.IO.File]::ReadAllText("$env:TEMP\ab-player.json") | ConvertFrom-Json
$fmts = @($j.streamingData.formats) + @($j.streamingData.adaptiveFormats)
$v = ($fmts | Where-Object { $_.itag -eq 136 -and $_.url }) | Select-Object -First 1
"playability: $($j.playabilityStatus.status); video url present: $([bool]$v.url)"

function Try-Req($label, $url, $useRange) {
    try {
        $req = [System.Net.HttpWebRequest]::Create($url)
        $req.UserAgent = $uaIos
        if ($useRange) { $req.AddRange(0) }   # Range: bytes=0-
        $req.Timeout = 30000
        $resp = $req.GetResponse()
        $s = $resp.GetResponseStream()
        $buf = New-Object byte[] 8192
        $read = $s.Read($buf, 0, 8192)
        $len = $resp.ContentLength
        $resp.Close()
        "$label -> OK $([int]$resp.StatusCode) contentLen=$len firstRead=$read magic=$([System.Text.Encoding]::ASCII.GetString($buf[4..7]))"
    } catch [System.Net.WebException] {
        $r = $_.Exception.Response
        if ($r) {
            $code = [int]$r.StatusCode
            $bodyTxt = ''
            try {
                $es = $r.GetResponseStream()
                $sr = New-Object System.IO.StreamReader($es)
                $bodyTxt = $sr.ReadToEnd()
                if ($bodyTxt.Length -gt 300) { $bodyTxt = $bodyTxt.Substring(0, 300) }
            } catch {}
            "$label -> FAIL $code body: $bodyTxt"
        } else {
            "$label -> EXC $($_.Exception.Message)"
        }
    }
}

Try-Req 'A plain-GET   ' $v.url $false
Try-Req 'B range-0-    ' $v.url $true
