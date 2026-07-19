Set-Location C:\claude-workspace\unturned-godot\game
$b = dotnet build -c Debug -v q -nologo 2>&1
if ($LASTEXITCODE -ne 0) { Write-Output "=== BUILD FAILED ==="; $b | Select-String 'error' | Select-Object -First 20; exit 1 }
Write-Output "=== BUILD OK ==="
$env:UG_BATTERY="1"
& C:\ProgramData\chocolatey\bin\godot.exe --path C:\claude-workspace\unturned-godot\game --rendering-driver opengl3 --write-movie C:\claude-workspace\battery.avi --fixed-fps 30 --quit-after 90 -- --deploytest 2>&1 | Select-String BATTERY
$ff=(Get-ChildItem -Recurse C:\claude-workspace\ffmpeg -Filter ffmpeg.exe | Select-Object -First 1).FullName
& $ff -y -ss 2.0 -i C:\claude-workspace\battery.avi -frames:v 1 C:\claude-workspace\battery.png 2>$null
Write-Output "DONE battery.png"
