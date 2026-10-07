$dir = $env:EMBY_SYSTEM_PATH; if (-not $dir) { throw "Set EMBY_SYSTEM_PATH to the Emby server 'system' folder" }
$movies = $env:TEST_MOVIES_PATH; if (-not $movies) { throw 'Set TEST_MOVIES_PATH to a movie folder to test against' }
foreach ($d in @('MediaBrowser.Model.dll', 'MediaBrowser.Common.dll')) { [System.Reflection.Assembly]::LoadFile((Join-Path $dir $d)) | Out-Null }
$asm = [System.Reflection.Assembly]::LoadFile((Join-Path $dir 'Emby.Naming.dll'))
try { $types = $asm.GetTypes() } catch [System.Reflection.ReflectionTypeLoadException] { $types = $_.Exception.Types | Where-Object { $_ -ne $null } }

$metaType = [Type]::GetType('MediaBrowser.Model.IO.FileSystemMetadata, MediaBrowser.Model')
if (-not $metaType) {
    # find it in loaded assemblies
    foreach ($la in [AppDomain]::CurrentDomain.GetAssemblies()) {
        $t = $la.GetType('MediaBrowser.Model.IO.FileSystemMetadata')
        if ($t) { $metaType = $t; break }
    }
}
"FileSystemMetadata: $($metaType.FullName)"

$erType = $types | Where-Object FullName -eq 'Emby.Naming.Video.ExtraResolver'
$mediaTypeType = $types | Where-Object FullName -eq 'Emby.Naming.Common.MediaType'
$videoVal = [System.Enum]::Parse($mediaTypeType, 'Video')

$flags = [System.Reflection.BindingFlags]::Public -bor [System.Reflection.BindingFlags]::Static -bor [System.Reflection.BindingFlags]::Instance
$getExtra = $erType.GetMethods($flags) | Where-Object { $_.Name -eq 'GetExtraInfo' }
"GetExtraInfo isStatic=$($getExtra.IsStatic) params=$($getExtra.GetParameters().Count)"

# instantiate with ctor args (NamingOptions etc.)
$noType = $types | Where-Object FullName -eq 'Emby.Naming.Common.NamingOptions'
$no = [Activator]::CreateInstance($noType)
function New-Instance($t, $options) {
    foreach ($c in $t.GetConstructors()) {
        $ps = $c.GetParameters()
        $ok = $true
        $args = @()
        foreach ($p in $ps) {
            if ($p.ParameterType -eq $noType) { $args += $options }
            elseif ($p.HasDefaultValue) { $args += $p.DefaultValue }
            elseif ($p.ParameterType -eq [string]) { $args += '' }
            elseif ($p.ParameterType.IsArray) { $args += (New-Object ($p.ParameterType) 0) }
            else { $ok = $false; break }
        }
        if ($ok) { return $c.Invoke($args) }
    }
    return $null
}
$resolver = New-Instance $erType $no
"ExtraResolver instance: $($resolver -ne $null)"

function New-Meta($path) {
    $m = [Activator]::CreateInstance($metaType)
    $m.FullName = $path
    $m
}

foreach ($p in @(
        (Join-Path $movies '300.2006.BluRay.1080p.x264.YIFY-trailer.mp4'),
        (Join-Path $movies '21 (2008)-trailer.mp4'),
        (Join-Path $movies 'trailer.mp4'),
        (Join-Path $movies '300.2006.BluRay.1080p.x264.YIFY.mp4')
    )) {
    $meta = New-Meta $p
    try {
        if ($getExtra.IsStatic) { $r = $getExtra.Invoke($null, @($meta, $videoVal)) } else { $r = $getExtra.Invoke($resolver, @($meta, $videoVal)) }
        if ($r) { "$([IO.Path]::GetFileName($p)) -> ExtraType=$($r.ExtraType)" } else { "$([IO.Path]::GetFileName($p)) -> NULL (not an extra)" }
    } catch { $msg = $_.Exception.Message; if ($_.Exception.InnerException) { $msg = $_.Exception.InnerException.Message }; "$([IO.Path]::GetFileName($p)) -> EXC: $msg" }
}

''
'--- full VideoListResolver.Resolve on the real folder ---'
$vlrType = $types | Where-Object FullName -eq 'Emby.Naming.Video.VideoListResolver'
$flags2 = [System.Reflection.BindingFlags]::Public -bor [System.Reflection.BindingFlags]::Static -bor [System.Reflection.BindingFlags]::Instance
$resolve = $vlrType.GetMethods($flags2) | Where-Object { $_.Name -eq 'ResolveToResult' }
"ResolveToResult isStatic=$($resolve.IsStatic)"
$files = Get-ChildItem $movies -File | ForEach-Object { New-Meta $_.FullName }
$listType = [System.Collections.Generic.List`1].MakeGenericType($metaType)
$list = [Activator]::CreateInstance($listType)
foreach ($f in $files) { $list.Add($f) }
$vlrFlags = [System.Reflection.BindingFlags]::Public -bor [System.Reflection.BindingFlags]::Static
$vlr = New-Instance $vlrType $no
"VideoListResolver instance: $($vlr -ne $null)"
$args2 = @($list, $false, $true, $true)   # supportMultiVersion, parseName, isMixedFolder=true (flat folder!)
try {
    if ($resolve.IsStatic) { $result = $resolve.Invoke($null, $args2) } else { $result = $resolve.Invoke($vlr, $args2) }
    $videos = $result.Videos
    "videos resolved: $($videos.Count)"
    foreach ($v in $videos) {
        $extras = $v.Extras
        $extraDesc = if ($extras) { ($extras | ForEach-Object { "$($_.ExtraType):$(Split-Path $_.Path -Leaf)" }) -join '; ' } else { '(none)' }
        "$([IO.Path]::GetFileName($v.Path))  ->  EXTRAS: $extraDesc"
    }
} catch { $msg = $_.Exception.Message; if ($_.Exception.InnerException) { $msg = $_.Exception.InnerException.Message }; "RESOLVE EXC: $msg" }
