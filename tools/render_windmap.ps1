param($freq = "", $oct = "")
Set-Location C:\claude-workspace\unturned-godot\game
$b = dotnet build -c Debug -v q -nologo 2>&1
if ($LASTEXITCODE -ne 0) { Write-Output "=== BUILD FAILED ==="; $b | Select-String 'error CS' | Select-Object -First 20; exit 1 }
Write-Output "=== BUILD OK ==="
$env:UG_WINDMAP="1"
if ($freq -ne "") { $env:UG_WINDFREQ = $freq; Write-Output "UG_WINDFREQ=$freq" }
if ($oct -ne "") { $env:UG_WINDOCT = $oct; Write-Output "UG_WINDOCT=$oct" }
& C:\ProgramData\chocolatey\bin\godot.exe --path C:\claude-workspace\unturned-godot\game --headless -- --deploytest 2>&1 | Out-Null
Write-Output "DONE windmap.png"
