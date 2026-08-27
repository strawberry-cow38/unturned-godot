$matdir = "C:\claude-workspace\ripped\unturned-up\ExportedProject\Assets\Material"
$out    = "C:\claude-workspace\menu_mat_tex.txt"
$acc = New-Object System.Collections.ArrayList
Get-ChildItem -Path $matdir -Filter *.mat | ForEach-Object {
    $name = $_.BaseName
    $txt  = Get-Content $_.FullName -Raw
    # _MainTex guid ONLY. Do NOT fall back to "first texture in the file": m_TexEnvs is serialized alphabetically,
    # so _BumpMap sorts before _MainTex and an albedo-less material would get its NORMAL MAP assigned as albedo.
    $g = ""
    if ($txt -match '_MainTex:\s*\r?\n\s*m_Texture:\s*\{fileID:\s*-?\d+,\s*guid:\s*([0-9a-f]{32})') { $g = $matches[1] }
    # _Color (RGBA, the sRGB albedo tint). Real albedo = _Color * _MainTex, and _MainTex defaults to white -- so a
    # material with no _MainTex is still coloured by _Color, not grey.
    $col = "1,1,1,1"
    if ($txt -match '_Color:\s*\{r:\s*([-\d.eE]+),\s*g:\s*([-\d.eE]+),\s*b:\s*([-\d.eE]+),\s*a:\s*([-\d.eE]+)\}') {
        $col = "$($matches[1]),$($matches[2]),$($matches[3]),$($matches[4])"
    }
    # _Cutoff (alpha-scissor threshold; retail's Leaves override 0.5 -> 0.2)
    $cut = ""
    if ($txt -match '_Cutoff:\s*([-\d.eE]+)') { $cut = $matches[1] }
    [void]$acc.Add("$name|$g|$col|$cut")
}
Set-Content -Path $out -Value $acc -Encoding ascii
Write-Output ("materials: " + $acc.Count)
