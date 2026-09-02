# stance-adaptive hitbox viz: render the zone overlay per stance/angle to verify the fit to the model.
Set-Location C:\claude-workspace\unturned-godot\game
$b = dotnet build -c Debug -v q -nologo 2>&1
if ($LASTEXITCODE -ne 0) { Write-Output BUILD_FAILED; $b | Select-String error | Select-Object -First 14; exit 1 }
Write-Output BUILD_OK
$g = "C:\ProgramData\chocolatey\bin\godot.exe"
$ff = (Get-ChildItem -Recurse C:\claude-workspace\ffmpeg -Filter ffmpeg.exe | Select-Object -First 1).FullName
$env:UG_PAHITBOX = "1"
function R($stance, $cam, $out) {
    $env:UG_PASTANCE = $stance; $env:UG_PACAM = $cam
    & $g --path C:\claude-workspace\unturned-godot\game --rendering-driver vulkan --audio-driver Dummy --write-movie C:\claude-workspace\h.avi --fixed-fps 30 --quit-after 40 -- --puppetanim 2>&1 | Out-Null
    & $ff -y -ss 1.0 -i C:\claude-workspace\h.avi -frames:v 1 $out 2>$null
    Remove-Item C:\claude-workspace\h.avi -Force -EA SilentlyContinue
}
R "stand" "0" "C:\claude-workspace\hz_stand.png"
R "stand" "1" "C:\claude-workspace\hz_stand_side.png"
R "crouch" "0" "C:\claude-workspace\hz_crouch.png"
R "prone" "3" "C:\claude-workspace\hz_prone.png"
R "lean" "0" "C:\claude-workspace\hz_lean.png"
Write-Output DONE
