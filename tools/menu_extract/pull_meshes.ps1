$stage = "C:\claude-workspace\menu_stage"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage | Out-Null
Get-Content C:\claude-workspace\mesh_pull.txt | ForEach-Object {
    if ($_ -and (Test-Path $_)) { Copy-Item $_ $stage }
}
$n = (Get-ChildItem $stage -Filter *.asset).Count
tar -cf C:\claude-workspace\menu_meshes.tar -C $stage .
Write-Output ("staged asset files: " + $n)
