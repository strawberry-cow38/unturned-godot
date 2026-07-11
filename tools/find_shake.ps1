$src = "C:\claude-workspace\archive\U3-SDK\Assets\Runtime\Assembly-CSharp"
Write-Output "=== shake method defs + explosion-triggered shake ==="
Get-ChildItem -Recurse $src -Include *.cs -ErrorAction SilentlyContinue |
  Select-String -Pattern "public.*[Ss]hake\s*\(|void [Ss]hake|MainCamera.*[Ss]hake|\.[Ss]hake\(|shakeMagnitude|shakeDuration|shakeAmplitude|shakePosition" |
  Select-Object -First 30 | ForEach-Object { Write-Output ($_.Filename + ":" + $_.LineNumber + "  " + $_.Line.Trim()) }
Write-Output ""
Write-Output "=== files mentioning explosion + shake together ==="
Get-ChildItem -Recurse $src -Include *.cs -ErrorAction SilentlyContinue |
  Where-Object { $c = Get-Content $_.FullName -Raw; $c -match "[Ss]hake" -and $c -match "[Ee]xplos" } |
  Select-Object -ExpandProperty Name -Unique
