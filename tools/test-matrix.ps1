$uaVr = 'com.google.android.apps.youtube.vr.oculus/1.60.19 (Linux; U; Android 12L; Quest 3 Build/SQ3A.220605.009.A1) gzip'
$uaYtAndroid = 'com.google.android.youtube/19.09.37 (Linux; U; Android 11) gzip'
$uaIos = 'com.google.ios.youtube/20.10.4 (iPhone16,2; U; CPU iOS 17_5_1 like Mac OS X;)'
$uaChrome = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36'
$uaTv = 'Mozilla/5.0 (SMART-TV; Linux; Tizen 6.0) AppleWebKit/537.36 (KHTML, like Gecko) 65.0.3325.181 TV Safari/537.36'

# visitorData
$raw = [System.IO.File]::ReadAllText("$env:TEMP\sw-raw.txt")
$visitor = ($raw.Substring($raw.IndexOf('[[')) | ConvertFrom-Json)[0][2][0][0][13]

$videoId = 'jX1m45CwvJ8'

function Test-Client($name, $clientName, $clientVersion, $ua, $extraClient, $useKey) {
    $client = @{
        clientName    = $clientName
        clientVersion = $clientVersion
        hl            = 'en'
        gl            = 'US'
        visitorData   = $visitor
    }
    if ($extraClient) { foreach ($k in $extraClient.Keys) { $client[$k] = $extraClient[$k] } }
    $body = @{
        videoId        = $videoId
        contentCheckOk = $true
        context        = @{ client = $client }
    } | ConvertTo-Json -Depth 6 -Compress
    $tmp = "$env:TEMP\yt-m-$name.json"
    [System.IO.File]::WriteAllText($tmp, $body, [System.Text.UTF8Encoding]::new($false))
    $url = 'https://www.youtube.com/youtubei/v1/player'
    if ($useKey) { $url += '?key=AIzaSyA8eiZmM1FaDVjRy-ds2MAqdkv2EEgdwLg' }
    $out = "$env:TEMP\yt-m-$name-out.json"
    $code = curl.exe -s $url -H "User-Agent: $ua" -H 'Content-Type: application/json' `
        -H "X-Youtube-Client-Name: 28" -H "X-Youtube-Client-Version: $clientVersion" `
        -H 'Cookie: CONSENT=YES+cb.20210328-17-p0.en+FX+678' `
        --data-binary "@$tmp" -o $out -w '%{http_code}'
    $txt = [System.IO.File]::ReadAllText($out)
    try { $j = $txt | ConvertFrom-Json } catch { "$name -> HTTP $code PARSE FAIL: $($txt.Substring(0,[Math]::Min(120,$txt.Length)))"; return }
    $status = $j.playabilityStatus.status
    $reason = $j.playabilityStatus.reason
    $nUrl = 0; $nCipher = 0
    if ($j.streamingData) {
        $fmts = @($j.streamingData.formats) + @($j.streamingData.adaptiveFormats)
        $nUrl = @($fmts | Where-Object url).Count
        $nCipher = @($fmts | Where-Object { $_.signatureCipher -or $_.cipher }).Count
    }
    "$name -> HTTP $code | playability=$status $reason | directUrl=$nUrl cipher=$nCipher"
}

Test-Client 'vr-key'    'ANDROID_VR' '1.60.19'    $uaVr        @{ deviceMake='Oculus'; deviceModel='Quest 3'; osName='Android'; osVersion='12L'; platform='MOBILE' } $true
Test-Client 'android'   'ANDROID'    '19.09.37'   $uaYtAndroid @{ androidSdkVersion=30; osName='Android'; osVersion='11'; platform='MOBILE' } $false
Test-Client 'ios'       'IOS'        '20.10.4'    $uaIos       @{ deviceMake='Apple'; deviceModel='iPhone16,2'; osName='iOS'; osVersion='17.5.1.21F90'; platform='MOBILE' } $false
Test-Client 'tvhtml5'   'TVHTML5'    '7.20240304.16.00' $uaTv  @{ } $false
Test-Client 'mweb'      'MWEB'       '2.20240304.00.00' $uaChrome @{ } $false
Test-Client 'webembed'  'WEB_EMBEDDED_PLAYER' '1.20240304.01.00' $uaChrome @{ clientScreen='EMBED' } $false
Test-Client 'web'       'WEB'        '2.20240304.00.00' $uaChrome @{ } $false

# HTML fallback: watch page
$out = "$env:TEMP\yt-watch.html"
$code = curl.exe -s -L "https://www.youtube.com/watch?v=$videoId" -H "User-Agent: $uaChrome" `
    -H 'Accept-Language: en-US,en;q=0.9' -H 'Cookie: CONSENT=YES+cb.20210328-17-p0.en+FX+678' `
    -H 'Accept: text/html,application/xhtml+xml' --compressed -o $out -w '%{http_code}'
$size = (Get-Item $out).Length
$html = [System.IO.File]::ReadAllText($out)
$hasInitial = $html.Contains('ytInitialPlayerResponse')
$hasPlayErr = $html.Contains('LOGIN_REQUIRED') -or $html.Contains('confirm you')
"watch-page -> HTTP $code size=$size hasInitialPlayerResponse=$hasInitial botBlock=$hasPlayErr"
