$dir = $env:EMBY_SYSTEM_PATH; if (-not $dir) { throw "Set EMBY_SYSTEM_PATH to the Emby server 'system' folder" }
foreach ($d in @('MediaBrowser.Model.dll', 'MediaBrowser.Common.dll')) {
    [System.Reflection.Assembly]::LoadFile((Join-Path $dir $d)) | Out-Null
}
$asm = [System.Reflection.Assembly]::LoadFile((Join-Path $dir 'Emby.Naming.dll'))
try { $types = $asm.GetTypes() } catch [System.Reflection.ReflectionTypeLoadException] {
    $types = $_.Exception.Types | Where-Object { $_ -ne $null }
}
$flags = [System.Reflection.BindingFlags]::Public -bor [System.Reflection.BindingFlags]::NonPublic -bor [System.Reflection.BindingFlags]::Static
foreach ($t in $types) {
    foreach ($f in $t.GetFields($flags)) {
        if ($f.IsLiteral -or $f.FieldType.Name -eq 'String') {
            $val = $f.GetValue($null)
            if ($val -is [string] -and $val -match 'trailer') {
                Write-Output "$($t.FullName)::$($f.Name) = $val"
            }
        }
    }
}
