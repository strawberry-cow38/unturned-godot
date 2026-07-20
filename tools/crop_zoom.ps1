param($src = "C:\claude-workspace\spot.png", $out = "C:\claude-workspace\spot_zoom.png", $crop = "380:240:460:330")
# crop = w:h:x:y ; then 3x nearest-neighbour upscale so pixel detail stays crisp
$ff = (Get-ChildItem -Recurse C:\claude-workspace\ffmpeg -Filter ffmpeg.exe | Select-Object -First 1).FullName
& $ff -y -i $src -vf "crop=$crop,scale=iw*3:ih*3:flags=neighbor" $out 2>$null
Write-Output "DONE $out"
