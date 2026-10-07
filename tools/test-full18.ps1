$consent = 'CONSENT=YES+cb.20210328-17-p0.en+FX+678'
$uaAndroid = 'com.google.android.youtube/20.10.3 (Linux; U; Android 14) gzip'

function Get-AndVisitor2 {
    $swRaw = (curl.exe -s 'https://www.youtube.com/sw.js_data' -H "User-Agent: $uaAndroid" -H 'Accept: application/json' -H "Cookie: $consent" --compressed) -join "`n"
    try { return ($swRaw.Substring($swRaw.IndexOf('[[')) | ConvertFrom-Json)[0][2][0][0][13] } catch { return $null }
}

function Get-AndPlayer2($vid) {
    $ctx = @{ clientName = 'ANDROID'; clientVersion = '20.10.3'; androidSdkVersion = 35
        osName = 'Android'; osVersion = '14'; hl = 'en'; gl = 'US'; utcOffsetMinutes = 0
        userAgent = $uaAndroid }
    $v = Get-AndVisitor2
    if ($v) { $ctx['visitorData'] = $v }
    $body = @{ videoId = $vid; contentCheckOk = $true; racyCheckOk = $true
        context = @{ client = $ctx } } | ConvertTo-Json -Depth 6 -Compress
    [System.IO.File]::WriteAllText("$env:TEMP\z-body.json", $body, [System.Text.UTF8Encoding]::new($false))
    curl.exe -s 'https://www.youtube.com/youtubei/v1/player' -H "User-Agent: $uaAndroid" -H 'Content-Type: application/json' -H "Cookie: $consent" -H 'X-Goog-Api-Format-Version: 2' --data-binary "@$env:TEMP\z-body.json" -o "$env:TEMP\z-resp.json" | Out-Null
    return ([System.IO.File]::ReadAllText("$env:TEMP\z-resp.json") | ConvertFrom-Json)
}

foreach ($pair in @(@('jX1m45CwvJ8', '3:10'), @('_pmFp2W65Fs', '13Going'), @('UrIbxk7idYA', '300'))) {
    $j = Get-AndPlayer2 $pair[0]
    $fmts = @($j.streamingData.formats) + @($j.streamingData.adaptiveFormats)
    $p18 = $fmts | Where-Object { $_.itag -eq 18 -and $_.url } | Select-Object -First 1
    if (-not $p18) { "$($pair[1]): NO itag18"; continue }
    $u = $p18.url
    $total = 0
    try {
        $req = [System.Net.HttpWebRequest]::Create($u)
        $req.UserAgent = $uaAndroid
        $req.AddRange(0, 65535)
        $resp = $req.GetResponse()
        $cr = $resp.Headers['Content-Range']
        $resp.Close()
        $total = [long]([regex]::Match($cr, '/(\d+)$').Groups[1].Value)
    } catch {
        "$($pair[1]): probe failed"
        continue
    }
    "$($pair[1]): itag18 total=$total"
    try {
        $req = [System.Net.HttpWebRequest]::Create($u)
        $req.UserAgent = $uaAndroid
        $req.AddRange(0, $total - 1)
        $req.Timeout = 90000
        $resp = $req.GetResponse()
        $s = $resp.GetResponseStream()
        $buf = New-Object byte[] 65536
        $drained = 0
        while (($n = $s.Read($buf, 0, 65536)) -gt 0) { $drained += $n }
        $resp.Close()
        "  itag18 FULL -> 206 DRAINED=$drained (expect $total)"
    } catch [System.Net.WebException] {
        $r = $_.Exception.Response
        if ($r) { "  itag18 FULL -> $([int]$r.StatusCode)"; $r.Close() } else { "  itag18 FULL -> EXC" }
    }
}
