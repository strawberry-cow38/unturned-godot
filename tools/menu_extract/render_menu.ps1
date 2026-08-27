param([string]$eye = "", [string]$look = "", [switch]$notrees, [string]$scale = "")
$repo   = "C:\claude-workspace\unturned-godot"
$dotnet = "C:\PROGRA~1\dotnet\dotnet.exe"
$godot  = "C:\ProgramData\chocolatey\bin\godot.exe"
$out    = "C:\claude-workspace\menuout"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Path $out | Out-Null

Push-Location "$repo\game"
& $dotnet build UnturnedGodot.sln -c Debug -v q -nologo 2>&1 | Select-String -Pattern 'error|Build succeeded|Build FAILED' | Select-Object -First 8
$code = $LASTEXITCODE
Pop-Location
if ($code -ne 0) { Write-Output "BUILD_FAILED code=$code"; exit 1 }
Write-Output "BUILD_OK"

$env:UG_MENUREAL = "1"
if ($notrees) { $env:UG_MENUNOTREES = "1" } else { $env:UG_MENUNOTREES = "" }
if ($scale) { $env:UG_LAMPSCALE = $scale } else { $env:UG_LAMPSCALE = "" }
if ($eye)  { $env:UG_MENUEYE  = $eye }
if ($look) { $env:UG_MENULOOK = $look }
& $godot --path "$repo\game" --rendering-driver vulkan --audio-driver Dummy -- --menushot=$out --quit-after 140 2>&1 |
    Select-String -Pattern 'MENUSHOT|extracted diorama|menu_scene|ERROR|Exception' | Select-Object -First 20
Write-Output "RENDER_DONE"
Get-ChildItem $out | Select-Object -ExpandProperty Name
