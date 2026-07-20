param($src, $out, $w = 850)
$ff = (Get-ChildItem -Recurse C:\claude-workspace\ffmpeg -Filter ffmpeg.exe | Select-Object -First 1).FullName
& $ff -y -i $src -vf "scale=${w}:-1" $out 2>$null
Write-Output "DONE $out"
