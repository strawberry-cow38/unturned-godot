$guids = @(
 "aefaa04d7d3d4a7a85d2b1419a6a2ff4",
 "c928fb99bae9434795563319a64f6461",
 "703378589e9f404db3bf2d539b740593",
 "e145629762c3442782d40a2fcd15e306",
 "75a0a1c332b5498baa7c7c1d59dc5122",
 "8d1cfde8b23d48ad95b49b3cbf5e18d2",
 "d00e2b1ff7b84935be1ba5c85ef3b716")
Write-Output "=== foliage .dat/.asset files matching blob GUIDs ==="
$root = "C:\Program Files (x86)\Steam\steamapps\common\Unturned"
Get-ChildItem -Recurse -Path $root -Include *.dat,*.asset -File -ErrorAction SilentlyContinue |
  Select-String -SimpleMatch -Pattern $guids -List |
  ForEach-Object { Write-Output ($_.Path + "  ::  " + $_.Line.Trim()) }
Write-Output "=== ContentReference.cs ==="
$c=(Get-ChildItem -Recurse C:\claude-workspace\archive\U3-SDK -Filter ContentReference.cs -ErrorAction SilentlyContinue | Select-Object -First 1).FullName
Write-Output $c
if($c){ Get-Content $c | Select-Object -First 80 }
