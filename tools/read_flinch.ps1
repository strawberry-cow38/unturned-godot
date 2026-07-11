$f = Get-ChildItem -Recurse "C:\claude-workspace\archive\U3-SDK\Assets\Runtime\Assembly-CSharp" -Filter PlayerLook.cs -ErrorAction SilentlyContinue | Select-Object -First 1
$lg = Get-Content $f.FullName
Write-Output ("PlayerLook.cs (" + $lg.Count + " lines)")
$m = $lg | Select-String "FlinchFromExplosion|shakeMagnitude|_shake|shakeYaw|shakePitch|ApplyRecoil|flinch"
$m | ForEach-Object { Write-Output ("  " + $_.LineNumber + ": " + $_.Line.Trim()) }
$defLine = ($lg | Select-String "void FlinchFromExplosion" | Select-Object -First 1).LineNumber
if ($defLine) {
    Write-Output "=== FlinchFromExplosion body ==="
    $lo = [Math]::Max(0, $defLine - 2); $hi = [Math]::Min($lg.Count - 1, $defLine + 40)
    for ($i = $lo; $i -le $hi; $i++) { Write-Output ("{0,4}: {1}" -f ($i + 1), $lg[$i]) }
}
