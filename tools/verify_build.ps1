# Fresh-checkout compile verify: build origin/main (what master pulls) in a throwaway worktree,
# so box-untestable PlayerController changes are confirmed to compile in the real integrated context.
Set-Location C:\claude-workspace\unturned-godot
git fetch origin 2>&1 | Out-Null
git worktree remove --force C:\claude-workspace\ug-verify 2>$null | Out-Null
git worktree add --detach C:\claude-workspace\ug-verify origin/main 2>&1 | Out-Null
Set-Location C:\claude-workspace\ug-verify
Write-Output ("HEAD: " + (git log --oneline -1))
Write-Output ("has-f2f7899: " + [bool](git log --oneline -12 | Select-String f2f7899))
$b = dotnet build game/UnturnedGodot.csproj --nologo -v q 2>&1
$errs = $b | Select-String -Pattern ': error '
if ($errs) { Write-Output "=== BUILD FAILED ==="; $errs | Select-Object -First 25 | ForEach-Object { Write-Output $_.Line } }
else { Write-Output "=== BUILD OK ===" }
Set-Location C:\claude-workspace\unturned-godot
git worktree remove --force C:\claude-workspace\ug-verify 2>$null | Out-Null
