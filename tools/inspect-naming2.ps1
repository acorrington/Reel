$dir = $env:EMBY_SYSTEM_PATH; if (-not $dir) { throw "Set EMBY_SYSTEM_PATH to the Emby server 'system' folder" }
foreach ($d in @('MediaBrowser.Model.dll', 'MediaBrowser.Common.dll')) {
    [System.Reflection.Assembly]::LoadFile((Join-Path $dir $d)) | Out-Null
}
$asm = [System.Reflection.Assembly]::LoadFile((Join-Path $dir 'Emby.Naming.dll'))
try { $types = $asm.GetTypes() } catch [System.Reflection.ReflectionTypeLoadException] {
    $types = $_.Exception.Types | Where-Object { $_ -ne $null }
    Write-Output "NOTE: partial type load, $($types.Count) types"
}
Write-Output "Total types: $($types.Count)"
$flags = [System.Reflection.BindingFlags]::Public -bor [System.Reflection.BindingFlags]::NonPublic -bor [System.Reflection.BindingFlags]::Static
# 1) any string field value containing 'trailer' (case-insensitive), any type
foreach ($t in $types) {
    foreach ($f in $t.GetFields($flags)) {
        if (-not $f.IsLiteral -and $f.FieldType.Name -eq 'String') {
            $val = $f.GetValue($null)
            if ($val -is [string] -and $val.ToLower() -match 'trailer') {
                Write-Output "FIELD $($t.FullName)::$($f.Name) = $val"
            }
        }
    }
}
# 2) NamingOptions members dump
$no = $types | Where-Object { $_.Name -eq 'NamingOptions' }
if ($no) {
    foreach ($p in $no[0].GetProperties()) { Write-Output "PROP $($p.Name) : $($p.PropertyType.Name)" }
}
