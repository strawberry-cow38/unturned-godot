$matdir = "C:\claude-workspace\ripped\unturned-up\ExportedProject\Assets\Material"
$out    = "C:\claude-workspace\menu_mat_tex.txt"
$acc = New-Object System.Collections.ArrayList
Get-ChildItem -Path $matdir -Filter *.mat | ForEach-Object {
    $name = $_.BaseName
    $txt  = Get-Content $_.FullName -Raw
    $g = $null
    if ($txt -match '_MainTex:\s*\r?\n\s*m_Texture:\s*\{fileID:\s*-?\d+,\s*guid:\s*([0-9a-f]{32})') { $g = $matches[1] }
    elseif ($txt -match 'm_Texture:\s*\{fileID:\s*-?\d+,\s*guid:\s*([0-9a-f]{32})') { $g = $matches[1] }
    if ($g) { [void]$acc.Add("$name|$g") }
}
Set-Content -Path $out -Value $acc -Encoding ascii
Write-Output ("materials with a texture: " + $acc.Count)
