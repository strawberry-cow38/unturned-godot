$asm = "C:\claude-workspace\archive\U3-SDK\Assets\Runtime\Assembly-CSharp"
Write-Output "=== SAVEDATA_TREES_VERSION_* consts ==="
Get-Content "$asm\Unturned\Level\LevelGround.cs" | Select-String -Pattern "SAVEDATA_TREES_VERSION"
Write-Output ""
Write-Output "=== Trees.dat header ==="
$b = [System.IO.File]::ReadAllBytes("C:\Program Files (x86)\Steam\steamapps\common\Unturned\Maps\PEI\Terrain\Trees.dat")
Write-Output ("size=" + $b.Length + "  version=" + $b[0] + "  int32@1=" + [BitConverter]::ToInt32($b,1))
Write-Output ""
Write-Output "=== read* method bodies (River.cs / Block.cs) ==="
Get-ChildItem -Recurse $asm -Include River.cs,Block.cs -ErrorAction SilentlyContinue | ForEach-Object {
  Write-Output ("--- " + $_.Name + " ---")
  $lines = Get-Content $_.FullName
  for ($i=0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match "read(GUID|SingleVector3|SingleQuaternion|Boolean|Byte|Int32|UInt16)\s*\(") {
      for ($j=$i; $j -lt [Math]::Min($i+6, $lines.Count); $j++) { Write-Output $lines[$j] }
      Write-Output "    ...."
    }
  }
}
