$consent = 'CONSENT=YES+cb.20210328-17-p0.en+FX+678'
$uaAndroid = 'com.google.android.youtube/19.09.37 (Linux; U; Android 11) gzip'

function Get-Visitor($ua) {
    $swRaw = (curl.exe -s 'https://www.youtube.com/sw.js_data' -H "User-Agent: $ua" -H 'Accept: application/json' -H "Cookie: $consent" --compressed) -join "`n"
    try { return ($swRaw.Substring($swRaw.IndexOf('[[')) | ConvertFrom-Json)[0][2][0][0][13] } catch { return $null }
}

function Try-Android($label, $visitor, $extraHeaders) {
    $ctx = @{ clientName = 'ANDROID'; clientVersion = '19.09.37'; androidSdkVersion = 30
        osName = 'Android'; osVersion = '11'; hl = 'en'; gl = 'US'; utcOffsetMinutes = 0 }
    if ($visitor) { $ctx['visitorData'] = $visitor }
    $body = @{ videoId = 'lgLm4_fq6GY'; contentCheckOk = $true; racyCheckOk = $true
        context = @{ client = $ctx } } | ConvertTo-Json -Depth 6 -Compress
    [System.IO.File]::WriteAllText("$env:TEMP\and-body.json", $body, [System.Text.UTF8Encoding]::new($false))
    $headerArgs = @('-H', "User-Agent: $uaAndroid", '-H', 'Content-Type: application/json', '-H', "Cookie: $consent")
    foreach ($k in $extraHeaders) { $headerArgs += @('-H', $k) }
    $out = "$env:TEMP\and-resp.json"
    $code = curl.exe -s 'https://www.youtube.com/youtubei/v1/player' @headerArgs --data-binary "@$env:TEMP\and-body.json" -o $out -w '%{http_code}'
    $txt = [System.IO.File]::ReadAllText($out)
    $status = '?'
    $n = 0
    $hls = $false
    $dash = $false
    try {
        $j = $txt | ConvertFrom-Json
        if ($j.error) { $status = "ERR: $($j.error.message)" }
        else {
            $status = $j.playabilityStatus.status
            if ($j.streamingData) {
                $fmts = @($j.streamingData.formats) + @($j.streamingData.adaptiveFormats)
                $n = @($fmts | Where-Object url).Count
                $hls = [bool]$j.streamingData.hlsManifestUrl
                $dash = [bool]$j.streamingData.dashManifestUrl
            }
        }
    } catch { $status = "PARSE: $($txt.Substring(0, [Math]::Min(80, $txt.Length)))" }
    "$label -> HTTP $code | $status | direct=$n hls=$hls dash=$dash"
}

$vAndroid = Get-Visitor $uaAndroid
$vIos = Get-Visitor 'com.google.ios.youtube/20.10.4 (iPhone16,2; U; CPU iOS 17_5_1 like Mac OS X;)'
"visitor android: $([bool]$vAndroid)  ios: $([bool]$vIos)"

Try-Android 'no visitor     ' $null @()
Try-Android 'android visitor' $vAndroid @()
Try-Android 'ios visitor    ' $vIos @()
Try-Android 'no-vis + hdrs  ' $null @('X-Youtube-Client-Name: 3', 'X-Youtube-Client-Version: 19.09.37', 'X-Goog-Api-Format-Version: 1')
