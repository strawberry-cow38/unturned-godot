param([string]$poi = "")
# Arena spawns + GUN RAIN debug shot (master 2026-09-02). Build then render the --arenaspawns harness:
# 8 spawn markers + border + the ArenaGuns node (40 churning guns, each a random gun w/ a random optic).
Set-Location C:\claude-workspace\unturned-godot\game
$b = dotnet build -c Debug -v q -nologo 2>&1
if ($LASTEXITCODE -ne 0) { Write-Output "=== BUILD FAILED ==="; $b | Select-String 'error' | Select-Object -First 30; exit 1 }
Write-Output "=== BUILD OK ==="
$png = "C:\claude-workspace\arena_guns.png"
$avi = "C:\claude-workspace\arena_guns.avi"
if (Test-Path $png) { Remove-Item $png -Force }
$harness = if ($poi) { "--arenaspawns=$poi" } else { "--arenaspawns" }
# --write-movie drives frames headlessly (Session-0); the shot self-quits ~45 frames after the world is ready,
# so the avi stays tiny. --quit-after is a backstop; --audio-driver Dummy so master doesn't hear it.
& C:\ProgramData\chocolatey\bin\godot.exe --path C:\claude-workspace\unturned-godot\game --rendering-driver vulkan --audio-driver Dummy --write-movie $avi --fixed-fps 30 --quit-after 1200 -- $harness --shot=$png 2>&1 |
  Select-String -Pattern 'SHOT','arena','ARENA','SCOPE','error','Unhandled','Exception','NullRef' | Select-Object -First 40
if (Test-Path $avi) { Remove-Item $avi -Force }   # DELETE the uncompressed movie IMMEDIATELY (disk hygiene)
if (Test-Path $png) { Write-Output ("PNG_OK " + $png + " " + (Get-Item $png).Length) } else { Write-Output "PNG_MISSING" }
