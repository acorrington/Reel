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
    Write-Output ("RULE ExtraType={0} Regex={1}" -f $r.ExtraType, $r.Expression)
}
