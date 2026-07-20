Set-Location C:\claude-workspace\unturned-godot\game
$b = dotnet build UnturnedGodot.csproj -c Debug -v q -nologo 2>&1
if ($LASTEXITCODE -ne 0) { Write-Output "=== BUILD FAILED ==="; $b | Select-String 'error CS' | Select-Object -First 15; exit 1 }
Write-Output "=== BUILD OK ==="
$godot = "C:\ProgramData\chocolatey\bin\godot.exe"
Write-Output "--- deploy tests ---"
& $godot --path C:\claude-workspace\unturned-godot\game --headless -- "--tests=deploy*" 2>&1 | Select-String -Pattern '\[TEST\]','passed=','FAIL','error'
Write-Output "--- power tests ---"
& $godot --path C:\claude-workspace\unturned-godot\game --headless -- "--tests=power*" 2>&1 | Select-String -Pattern '\[TEST\]','passed=','FAIL','error'
Write-Output "=== TESTS DONE ==="
