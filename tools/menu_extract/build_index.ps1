$base = "C:\claude-workspace\ripped\unturned-up\ExportedProject\Assets"
$out  = "C:\claude-workspace\menu_guid_index.txt"
$acc  = New-Object System.Collections.ArrayList
foreach ($d in @("Mesh","Material","Texture2D")) {
    $p = Join-Path $base $d
    $metas = Get-ChildItem -Path $p -Recurse -Filter *.meta
    Write-Output ("$d metas: " + $metas.Count)
    foreach ($f in $metas) {
        $m = Select-String -Path $f.FullName -Pattern 'guid: ([0-9a-f]{32})' -List
        if ($m) { [void]$acc.Add($m.Matches[0].Groups[1].Value + "|" + $f.FullName) }
    }
}
Set-Content -Path $out -Value $acc -Encoding ascii
Write-Output ("total: " + $acc.Count)
