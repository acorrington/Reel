$consent = 'CONSENT=YES+cb.20210328-17-p0.en+FX+678'

function Get-PlayerRaw($videoId, $client) {
    $swRaw = (curl.exe -s 'https://www.youtube.com/sw.js_data' -H "User-Agent: $($client.ua)" -H 'Accept: application/json' -H "Cookie: $consent" --compressed) -join "`n"
    $visitor = $null
    try { $visitor = ($swRaw.Substring($swRaw.IndexOf('[[')) | ConvertFrom-Json)[0][2][0][0][13] } catch {}
    $clientCtx = $client.ctx.Clone()
    if ($visitor) { $clientCtx['visitorData'] = $visitor }
    $body = @{ videoId = $videoId; contentCheckOk = $true; racyCheckOk = $true;
        context = @{ client = $clientCtx } } | ConvertTo-Json -Depth 6 -Compress
    [System.IO.File]::WriteAllText("$env:TEMP\p-body.json", $body, [System.Text.UTF8Encoding]::new($false))
    $out = "$env:TEMP\p-resp.json"
    $code = curl.exe -s 'https://www.youtube.com/youtubei/v1/player' -H "User-Agent: $($client.ua)" -H 'Content-Type: application/json' -H "Cookie: $consent" --data-binary "@$env:TEMP\p-body.json" -o $out -w '%{http_code}'
    return @{ code = $code; json = [System.IO.File]::ReadAllText($out) }
}

$ios = @{ ua = 'com.google.ios.youtube/20.10.4 (iPhone16,2; U; CPU iOS 17_5_1 like Mac OS X;)'
    ctx = @{ clientName = 'IOS'; clientVersion = '20.10.4'; deviceMake = 'Apple'; deviceModel = 'iPhone16,2'
        osName = 'iOS'; osVersion = '17.5.1.21F90'; platform = 'MOBILE'; hl = 'en'; gl = 'US'; utcOffsetMinutes = 0 } }

$android = @{ ua = 'com.google.android.youtube/19.09.37 (Linux; U; Android 11) gzip'
    ctx = @{ clientName = 'ANDROID'; clientVersion = '19.09.37'; androidSdkVersion = 30
        osName = 'Android'; osVersion = '11'; hl = 'en'; gl = 'US'; utcOffsetMinutes = 0 } }

function Get-Param($u, $name) { $m = [regex]::Match($u, "(?:[?&])$name=([^&]*)"); if ($m.Success) { $m.Groups[1].Value } else { $null } }

function T($label, $url, $from, $to) {
    try {
        $req = [System.Net.HttpWebRequest]::Create($url)
        $req.UserAgent = $script:curUA
        $req.AddRange($from, $to)
        $req.Timeout = 30000
        $resp = $req.GetResponse()
        $cr = $resp.Headers['Content-Range']
        $s = $resp.GetResponseStream(); $buf = New-Object byte[] 65536
        while ($s.Read($buf, 0, 65536) -gt 0) {}
        $resp.Close()
        "  $label -> 206 cr=$cr"
    } catch [System.Net.WebException] {
        $r = $_.Exception.Response
        if ($r) { "  $label -> $([int]$r.StatusCode)"; $r.Close() } else { "  $label -> EXC" }
    }
}

'=========== 1) paired params for known videos (IOS) ==========='
foreach ($vid in @('lgLm4_fq6GY', '_pmFp2W65Fs', 'jX1m45CwvJ8')) {
    $r = Get-PlayerRaw $vid $ios
    $j = $r.json | ConvertFrom-Json
    $fmts = @($j.streamingData.formats) + @($j.streamingData.adaptiveFormats)
    foreach ($f in $fmts) {
        if ($f.url -and $f.itag -in 135, 136, 137, 140) {
            $u = $f.url
            "videoId=$vid itag=$($f.itag) clen=$(Get-Param $u 'clen') dur=$(Get-Param $u 'dur') initcwnd=$(Get-Param $u 'initcwndbps') cps=$(Get-Param $u 'cps') txp=$(Get-Param $u 'txp') fvip=$(Get-Param $u 'fvip') mvi=$(Get-Param $u 'mvi')"
        }
    }
}

''
'=========== 2) 13Gon30 FULL range test (its cap >= 29.4M so far) ==========='
$r = Get-PlayerRaw '_pmFp2W65Fs' $ios
$j = $r.json | ConvertFrom-Json
$fmts = @($j.streamingData.formats) + @($j.streamingData.adaptiveFormats)
$v137 = $fmts | Where-Object { $_.itag -eq 137 -and $_.url } | Select-Object -First 1
$clen137 = [long](Get-Param $v137.url 'clen')
$script:curUA = $ios.ua
"itag137 clen=$clen137"
T "full [0-$($clen137-1)]" $v137.url 0 ($clen137 - 1)

''
'=========== 3) ANDROID client (fixed body) for 1408 ==========='
$r = Get-PlayerRaw 'lgLm4_fq6GY' $android
"android player HTTP: $($r.code)"
$j = $null
try { $j = $r.json | ConvertFrom-Json } catch {}
if ($j) {
    "playability: $($j.playabilityStatus.status) $($j.playabilityStatus.reason)"
    if ($j.streamingData) {
        $fmts = @($j.streamingData.formats) + @($j.streamingData.adaptiveFormats)
        "formats: $($fmts.Count), with url: $(@($fmts | Where-Object url).Count)"
        "hls: $([bool]$j.streamingData.hlsManifestUrl) dash: $([bool]$j.streamingData.dashManifestUrl)"
        $av = $fmts | Where-Object { $_.url } | Select-Object -First 3
        foreach ($f in $av) { "  itag=$($f.itag) mime=$($f.mimeType.Substring(0, [Math]::Min(30, $f.mimeType.Length))) clen=$(Get-Param $f.url 'clen')" }
        # full range test on best video
        $video = $fmts | Where-Object { $_.mimeType -like 'video/mp4*' -and $_.url } | Sort-Object { -$_.height } | Select-Object -First 1
        if ($video) {
            $cl = [long](Get-Param $video.url 'clen')
            $script:curUA = $android.ua
            "ANDROID full range test itag=$($video.itag) clen=$cl"
            T "[0-64K]       " $video.url 0 65535
            T "[0-$(($cl-1))]" $video.url 0 ($cl - 1)
        }
    } else { 'android: no streamingData' }
} else { "android raw: $($r.json.Substring(0, [Math]::Min(300, $r.json.Length)))" }
