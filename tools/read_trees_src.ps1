$base = "C:\claude-workspace\archive\U3-SDK\Assets\Runtime\Assembly-CSharp\Unturned"
$lg = Get-Content "$base\Level\LevelGround.cs"
$m = $lg | Select-String -Pattern "Trees\.dat"
Write-Output "=== Trees.dat mentions in LevelGround ==="
$m | ForEach-Object { Write-Output ("  line " + $_.LineNumber + ": " + $_.Line.Trim()) }
$idx = ($m | Select-Object -First 1).LineNumber
Write-Output "=== LevelGround.cs [load region] ==="
$lo = [Math]::Max(0, $idx-6); $hi = [Math]::Min($lg.Count-1, $idx+80)
$lg[$lo..$hi] | ForEach-Object { Write-Output $_ }
Write-Output ""
Write-Output "=== ResourceSpawnpoint.cs (first 75) ==="
Get-Content "$base\Level\ResourceSpawnpoint.cs" | Select-Object -First 75
Write-Output ""
Write-Output "=== ResourceAsset.cs mesh/model refs ==="
Get-Content "$base\Bundles\ResourceAsset.cs" | Select-String -Pattern "ContentReference|Mesh| mesh|model|prefab|GUID|LOD|Christmas|Holiday"
