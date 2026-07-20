Set-Location C:\claude-workspace\unturned-godot\game
$b = dotnet build -c Debug -v q -nologo 2>&1
if ($LASTEXITCODE -ne 0) { Write-Output "=== BUILD FAILED ==="; $b | Select-String 'error CS' | Select-Object -First 20; exit 1 }
Write-Output "=== BUILD OK ==="
$ff=(Get-ChildItem -Recurse C:\claude-workspace\ffmpeg -Filter ffmpeg.exe | Select-Object -First 1).FullName
$env:UG_SWITCHCKT="1"
Remove-Item Env:\UG_TRIGOFF -ErrorAction SilentlyContinue
& C:\ProgramData\chocolatey\bin\godot.exe --path C:\claude-workspace\unturned-godot\game --rendering-driver opengl3 --write-movie C:\claude-workspace\ckton.avi --fixed-fps 30 --quit-after 90 -- --deploytest 2>&1 | Out-Null
& $ff -y -ss 2.0 -i C:\claude-workspace\ckton.avi -frames:v 1 C:\claude-workspace\ckt_on.png 2>$null
$env:UG_TRIGOFF="1"
& C:\ProgramData\chocolatey\bin\godot.exe --path C:\claude-workspace\unturned-godot\game --rendering-driver opengl3 --write-movie C:\claude-workspace\cktoff.avi --fixed-fps 30 --quit-after 90 -- --deploytest 2>&1 | Out-Null
& $ff -y -ss 2.0 -i C:\claude-workspace\cktoff.avi -frames:v 1 C:\claude-workspace\ckt_off.png 2>$null
Write-Output "DONE ckt_on.png + ckt_off.png"
