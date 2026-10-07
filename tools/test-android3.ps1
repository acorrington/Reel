$consent = 'CONSENT=YES+cb.20210328-17-p0.en+FX+678'

function Get-Visitor($ua) {
    $swRaw = (curl.exe -s 'https://www.youtube.com/sw.js_data' -H "User-Agent: $ua" -H 'Accept: application/json' -H "Cookie: $consent" --compressed) -join "`n"
    try { return ($swRaw.Substring($swRaw.IndexOf('[[')) | ConvertFrom-Json)[0][2][0][0][13] } catch { return $null }
}

function Try-Android($label, $version, $sdk, $withUserAgentField, $apiFormatHeader) {
    $ua = "com.google.android.youtube/$version (Linux; U; Android 14) gzip"
    $ctx = @{ clientName = 'ANDROID'; clientVersion = $version; androidSdkVersion = $sdk
        osName = 'Android'; osVersion = '14'; hl = 'en'; gl = 'US'; utcOffsetMinutes = 0 }
    $v = Get-Visitor $ua
    if ($v) { $ctx['visitorData'] = $v }
    if ($withUserAgentField) { $ctx['userAgent'] = $ua }
    $body = @{ videoId = 'lgLm4_fq6GY'; contentCheckOk = $true; racyCheckOk = $true
        context = @{ client = $ctx } } | ConvertTo-Json -Depth 6 -Compress
    [System.IO.File]::WriteAllText("$env:TEMP\a3-body.json", $body, [System.Text.UTF8Encoding]::new($false))
    $h = @('-H', "User-Agent: $ua", '-H', 'Content-Type: application/json', '-H', "Cookie: $consent")
    if ($apiFormatHeader) { $h += @('-H', 'X-Goog-Api-Format-Version: 2') }
    $out = "$env:TEMP\a3-resp.json"
    $code = curl.exe -s 'https://www.youtube.com/youtubei/v1/player' @h --data-binary "@$env:TEMP\a3-body.json" -o $out -w '%{http_code}'
    $txt = [System.IO.File]::ReadAllText($out)
    $status = '?'
    $n = 0; $hls = $false; $dash = $false
    try {
        $j = $txt | ConvertFrom-Json
        if ($j.error) { $status = "ERR: $($j.error.message)" }
        else {
            $status = $j.playabilityStatus.status
            if ($j.streamingData) {
                $n = @((@($j.streamingData.formats) + @($j.streamingData.adaptiveFormats)) | Where-Object url).Count
                $hls = [bool]$j.streamingData.hlsManifestUrl
                $dash = [bool]$j.streamingData.dashManifestUrl
            }
        }
    } catch { $status = "PARSE: $($txt.Substring(0, [Math]::Min(100, $txt.Length)))" }
    "$label -> HTTP $code | $status | direct=$n hls=$hls dash=$dash"
    return ($txt)
}

Try-Android 'v19.09+uaField+hdr' '19.09.37' 30 $true $true | Out-Null
Try-Android 'v20.10.3+uaField   ' '20.10.3' 35 $true $true | Out-Null
Try-Android 'v19.44.38+uaField  ' '19.44.38' 34 $true $true | Out-Null
$last = Try-Android 'v20.10.3+uaField+api' '20.10.3' 35 $true $true
$last | Set-Content "$env:TEMP\a3-last.txt"
