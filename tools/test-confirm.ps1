$consent = 'CONSENT=YES+cb.20210328-17-p0.en+FX+678'
$uaAndroid = 'com.google.android.youtube/20.10.3 (Linux; U; Android 14) gzip'

function Get-AndVisitor {
    $swRaw = (curl.exe -s 'https://www.youtube.com/sw.js_data' -H "User-Agent: $uaAndroid" -H 'Accept: application/json' -H "Cookie: $consent" --compressed) -join "`n"
    try { return ($swRaw.Substring($swRaw.IndexOf('[[')) | ConvertFrom-Json)[0][2][0][0][13] } catch { return $null }
}

function Get-AndPlayer($vid) {
    $ctx = @{ clientName = 'ANDROID'; clientVersion = '20.10.3'; androidSdkVersion = 35
        osName = 'Android'; osVersion = '14'; hl = 'en'; gl = 'US'; utcOffsetMinutes = 0
        userAgent = $uaAndroid }
    $v = Get-AndVisitor
    if ($v) { $ctx['visitorData'] = $v }
    $body = @{ videoId = $vid; contentCheckOk = $true; racyCheckOk = $true
        context = @{ client = $ctx } } | ConvertTo-Json -Depth 6 -Compress
    [System.IO.File]::WriteAllText("$env:TEMP\cf-body.json", $body, [System.Text.UTF8Encoding]::new($false))
    curl.exe -s 'https://www.youtube.com/youtubei/v1/player' -H "User-Agent: $uaAndroid" -H 'Content-Type: application/json' -H "Cookie: $consent" -H 'X-Goog-Api-Format-Version: 2' --data-binary "@$env:TEMP\cf-body.json" -o "$env:TEMP\cf-resp.json" | Out-Null
    return ([System.IO.File]::ReadAllText("$env:TEMP\cf-resp.json") | ConvertFrom-Json)
}

function Test-Range($label, $url, $from, $to) {
    try {
        $req = [System.Net.HttpWebRequest]::Create($url)
        $req.UserAgent = $uaAndroid
        $req.AddRange($from, $to)
        $req.Timeout = 30000
        $resp = $req.GetResponse()
        $cr = $resp.Headers['Content-Range']
        $s = $resp.GetResponseStream()
        $buf = New-Object byte[] 65536
        $read = 0
        while ($read -lt 1048576 -and ($n = $s.Read($buf, 0, 65536)) -gt 0) { $read += $n }
        $resp.Close()
        "  $label -> 206 cr=$cr"
    } catch [System.Net.WebException] {
        $r = $_.Exception.Response
        if ($r) { "  $label -> $([int]$r.StatusCode)"; $r.Close() } else { "  $label -> EXC" }
    }
}

foreach ($pair in @(@('jX1m45CwvJ8', '3:10toYuma'), @('_pmFp2W65Fs', '13Going'), @('UrIbxk7idYA', '300'))) {
    $j = Get-AndPlayer $pair[0]
    $fmts = @($j.streamingData.formats) + @($j.streamingData.adaptiveFormats)
    $prog = @($fmts | Where-Object { $_.audioQuality -and $_.url })
    "$($pair[1]): playability=$($j.playabilityStatus.status) directProgressive=$($prog.Count) itags=[$(($prog | ForEach-Object itag) -join ',')] heights=[$(($prog | ForEach-Object height) -join ',')]"
    foreach ($p in $prog) {
        $m = [regex]::Match($p.url, 'clen=(\d+)')
        $clen = if ($m.Success) { [long]$m.Groups[1].Value } else { 0 }
        if ($clen -gt 0) {
            Test-Range "itag$($p.itag) FULL [0-$($clen-1)]" $p.url 0 ($clen - 1)
        } else {
            Test-Range "itag$($p.itag) probe [0-65535] (no clen param)" $p.url 0 65535
        }
    }
}
