Set-Location C:\claude-workspace\unturned-godot\game
$b = dotnet build UnturnedGodot.csproj -c Debug -v q -nologo 2>&1
if ($LASTEXITCODE -ne 0) { Write-Output "=== BUILD FAILED ==="; $b | Select-String 'error CS' | Select-Object -First 12; exit 1 }
Write-Output "=== BUILD OK ==="
& C:\ProgramData\chocolatey\bin\godot.exe --path C:\claude-workspace\unturned-godot\game --headless -- "--tests=power*" 2>&1 | Select-String -Pattern '\[TEST\]','\[L1\]','passed='
Write-Output "=== TESTS DONE ==="
