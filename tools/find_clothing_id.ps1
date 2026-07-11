$base = "C:\Program Files (x86)\Steam\steamapps\common\Unturned\Bundles\Items"
$rows = @()
Get-ChildItem -Recurse $base -Filter *.dat -ErrorAction SilentlyContinue | ForEach-Object {
    $c = Get-Content $_.FullName -Raw -ErrorAction SilentlyContinue
    if ($c -match "Type\s+Shirt" -and $c -match "(?m)^\s*ID\s+(\d+)") {
        $rows += [pscustomobject]@{ ID = [int]$Matches[1]; Path = $_.FullName.Substring($base.Length) }
    }
}
Write-Output ("total shirts with ID: " + $rows.Count)
Write-Output "=== 15 LOWEST ID shirts ==="
$rows | Sort-Object ID | Select-Object -First 15 | ForEach-Object { Write-Output ($_.ID.ToString().PadLeft(6) + "  " + $_.Path) }
Write-Output "=== 10 HIGHEST ID shirts ==="
$rows | Sort-Object ID -Descending | Select-Object -First 10 | ForEach-Object { Write-Output ($_.ID.ToString().PadLeft(6) + "  " + $_.Path) }
