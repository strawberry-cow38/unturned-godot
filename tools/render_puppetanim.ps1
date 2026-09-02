# --puppetanim: prove the RemotePlayers locomotion animates (idle->walk->run). Build, movie-render, extract 3 verify
# frames (idle/walk/run) + an mp4 for Discord, delete the avi.
Set-Location C:\claude-workspace\unturned-godot\game
$b = dotnet build -c Debug -v q -nologo 2>&1
if ($LASTEXITCODE -ne 0) { Write-Output "BUILD_FAILED"; $b | Select-String error | Select-Object -First 20; exit 1 }
Write-Output "BUILD_OK"
$avi = "C:\claude-workspace\pa.avi"; $mp4 = "C:\claude-workspace\pa.mp4"
Remove-Item $mp4,C:\claude-workspace\pa_idle.png,C:\claude-workspace\pa_walk.png,C:\claude-workspace\pa_run.png -Force -EA SilentlyContinue
& C:\ProgramData\chocolatey\bin\godot.exe --path C:\claude-workspace\unturned-godot\game --rendering-driver vulkan --audio-driver Dummy --write-movie $avi --fixed-fps 30 --quit-after 215 -- --puppetanim 2>&1 | Select-String "puppetanim","error","Exception","NullRef" | Select-Object -First 10
$ff = (Get-ChildItem -Recurse C:\claude-workspace\ffmpeg -Filter ffmpeg.exe | Select-Object -First 1).FullName
& $ff -y -ss 1.0 -i $avi -frames:v 1 C:\claude-workspace\pa_idle.png 2>$null
& $ff -y -ss 3.3 -i $avi -frames:v 1 C:\claude-workspace\pa_walk.png 2>$null
& $ff -y -ss 6.2 -i $avi -frames:v 1 C:\claude-workspace\pa_run.png 2>$null
& $ff -y -i $avi -pix_fmt yuv420p -crf 28 -movflags +faststart $mp4 2>$null
if (Test-Path $avi) { Remove-Item $avi -Force }   # kill the uncompressed movie immediately
if (Test-Path $mp4) { Write-Output ("MP4_OK " + [int]((Get-Item $mp4).Length/1KB) + "KB") } else { Write-Output "MP4_MISSING" }
