$stage = "C:\claude-workspace\tex_stage"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage | Out-Null
Get-Content C:\claude-workspace\tex_pull.txt | ForEach-Object { if ($_ -and (Test-Path $_)) { Copy-Item $_ $stage } }
$n = (Get-ChildItem $stage -Filter *.png).Count
tar -cf C:\claude-workspace\menu_tex.tar -C $stage .
Write-Output ("staged textures: " + $n)
