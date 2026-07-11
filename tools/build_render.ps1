param([string]$sx="-806",[string]$sz="676",[string]$r="14",[string]$h="3",[string]$cy="1.5",[string]$out="gfix")
Set-Location C:\claude-workspace\unturned-godot\game
$b = dotnet build -c Debug -v q -nologo 2>&1
if ($LASTEXITCODE -ne 0) { Write-Output "=== BUILD FAILED ==="; $b | Select-String -Pattern 'error' | Select-Object -First 20; exit 1 }
Write-Output "=== BUILD OK ==="
$env:UG_LHSPAWN="1"; $env:UG_SPAWNX=$sx; $env:UG_SPAWNZ=$sz; $env:UG_ORBIT="1"; $env:UG_ORBITR=$r; $env:UG_ORBITH=$h; $env:UG_ORBITCY=$cy
godot --path C:\claude-workspace\unturned-godot\game --rendering-driver opengl3 --write-movie "C:\claude-workspace\$out.avi" --fixed-fps 30 -- --peidrive 2>$null | Select-String foliage
$ff=(Get-ChildItem -Recurse C:\claude-workspace\ffmpeg -Filter ffmpeg.exe | Select-Object -First 1).FullName
& $ff -y -ss 2.0 -i "C:\claude-workspace\$out.avi" -frames:v 1 "C:\claude-workspace\$out.png" 2>$null
Write-Output "DONE $out.png"
