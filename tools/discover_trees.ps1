$pei = "C:\Program Files (x86)\Steam\steamapps\common\Unturned\Maps\PEI"
$src = "C:\claude-workspace\archive\U3-SDK\Assets\Runtime\Assembly-CSharp"
Write-Output "=== Maps\PEI .dat/.blob + Resource/Tree files ==="
Get-ChildItem -Recurse $pei -File | Where-Object { $_.Extension -in ".dat",".blob" -or $_.Name -match "Resource|Tree|Level" } |
  ForEach-Object { Write-Output ($_.FullName.Substring($pei.Length) + "   (" + $_.Length + " b)") }
Write-Output ""
Write-Output "=== resource-loader source files ==="
Get-ChildItem -Recurse $src -Include *.cs | Where-Object { $_.BaseName -match "LevelGround|ResourceSpawnpoint|ResourceAsset|^ResourceManager|^Resource$" } |
  Select-Object -ExpandProperty FullName
Write-Output ""
Write-Output "=== how the resources file is read (LevelGround grep) ==="
$lg = Get-ChildItem -Recurse $src -Include LevelGround.cs -ErrorAction SilentlyContinue | Select-Object -First 1
if ($lg) { Write-Output $lg.FullName; Get-Content $lg.FullName | Select-String -Pattern "Resources|\.dat|path|Read|Block|Spawn|position|rotation" | Select-Object -First 50 }
