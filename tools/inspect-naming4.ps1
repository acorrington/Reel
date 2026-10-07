$dir = $env:EMBY_SYSTEM_PATH; if (-not $dir) { throw "Set EMBY_SYSTEM_PATH to the Emby server 'system' folder" }
foreach ($d in @('MediaBrowser.Model.dll', 'MediaBrowser.Common.dll')) {
    [System.Reflection.Assembly]::LoadFile((Join-Path $dir $d)) | Out-Null
}
$asm = [System.Reflection.Assembly]::LoadFile((Join-Path $dir 'Emby.Naming.dll'))
try { $types = $asm.GetTypes() } catch [System.Reflection.ReflectionTypeLoadException] {
    $types = $_.Exception.Types | Where-Object { $_ -ne $null }
}
$noType = $types | Where-Object { $_.Name -eq 'NamingOptions' } | Select-Object -First 1
$inst = [Activator]::CreateInstance($noType)
$rules = $inst.VideoExtraRules
foreach ($r in $rules) {
    foreach ($p in $r.GetType().GetProperties()) {
        $v = $p.GetValue($r)
        if ($v -ne $null) { Write-Output ("{0} | {1} = {2}" -f $r.ExtraType, $p.Name, $v) }
    }
    Write-Output '---'
}
