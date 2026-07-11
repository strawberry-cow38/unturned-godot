$roots = @(
  "C:\Program Files (x86)\Steam\steamapps\common\Unturned\Bundles\Effects",
  "C:\Program Files (x86)\Steam\steamapps\common\Unturned\Bundles"
)
$seen = @{}
foreach ($r in $roots) {
  Get-ChildItem -Recurse $r -Filter *.dat -ErrorAction SilentlyContinue | ForEach-Object {
    if ($seen.ContainsKey($_.FullName)) { return }; $seen[$_.FullName] = 1
    $c = Get-Content $_.FullName -Raw -ErrorAction SilentlyContinue
    if ($c -match "CameraShake_Radius\s+([\d.]+)") {
      $rad = $Matches[1]
      $mag = if ($c -match "CameraShake_MagnitudeDegrees\s+([\d.]+)") { $Matches[1] } else { "?" }
      Write-Output ("{0,-34} radius={1,-6} mag={2}" -f $_.BaseName, $rad, $mag)
    }
  }
}
