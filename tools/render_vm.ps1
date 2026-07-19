param([string]$item="gascan")
Set-Location C:\claude-workspace\unturned-godot\game
$env:UG_VMSMALL="1"
& C:\ProgramData\chocolatey\bin\godot.exe --path C:\claude-workspace\unturned-godot\game --rendering-driver opengl3 --write-movie "C:\claude-workspace\vm_$item.avi" --fixed-fps 30 --quit-after 55 -- --vm=C:\claude-workspace\vmout --gun=$item 2>$null | Out-Null
$ff=(Get-ChildItem -Recurse C:\claude-workspace\ffmpeg -Filter ffmpeg.exe | Select-Object -First 1).FullName
& $ff -y -ss 1.2 -i "C:\claude-workspace\vm_$item.avi" -frames:v 1 "C:\claude-workspace\vm_$item.png" 2>$null | Out-Null
Write-Output "DONE vm_$item.png"
