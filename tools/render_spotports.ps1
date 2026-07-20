param($spc = "", $spp = "")
Set-Location C:\claude-workspace\unturned-godot\game
$b = dotnet build -c Debug -v q -nologo 2>&1
if ($LASTEXITCODE -ne 0) { Write-Output "=== BUILD FAILED ==="; $b | Select-String 'error CS' | Select-Object -First 20; exit 1 }
Write-Output "=== BUILD OK ==="
$env:UG_SPOTPORTS="1"
if ($spc -ne "") { $env:UG_SPC = $spc; Write-Output "UG_SPC=$spc" }
if ($spp -ne "") { $env:UG_SPP = $spp; Write-Output "UG_SPP=$spp" }
& C:\ProgramData\chocolatey\bin\godot.exe --path C:\claude-workspace\unturned-godot\game --rendering-driver opengl3 --write-movie C:\claude-workspace\spot.avi --fixed-fps 30 --quit-after 90 -- --deploytest 2>&1 | Out-Null
$ff=(Get-ChildItem -Recurse C:\claude-workspace\ffmpeg -Filter ffmpeg.exe | Select-Object -First 1).FullName
& $ff -y -ss 2.0 -i C:\claude-workspace\spot.avi -frames:v 1 C:\claude-workspace\spot.png 2>$null
Write-Output "DONE spot.png"
