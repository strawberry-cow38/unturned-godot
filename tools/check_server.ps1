# Definitive check for whatever "dedicated server" is loading the box: INSTANTANEOUS cpu, dotnet procs, all listeners.
"=== INSTANTANEOUS TOP CPU (1s sample) ==="
try {
  $s = Get-Counter '\Process(*)\% Processor Time' -SampleInterval 1 -MaxSamples 1 -ErrorAction Stop
  $s.CounterSamples | Where-Object { $_.InstanceName -notin '_total','idle' -and $_.CookedValue -gt 1 } | Sort-Object CookedValue -Descending | Select-Object -First 12 InstanceName, @{N='CPUpct';E={[int]$_.CookedValue}} | Format-Table -AutoSize | Out-String
} catch { "Get-Counter failed: $_" }
"=== dotnet / server-ish processes (cmdline) ==="
Get-CimInstance Win32_Process | Where-Object { $_.Name -match 'dotnet|server|godot|nturned|Unturned' } | ForEach-Object {
  $p = Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue
  "PID $($_.ProcessId) | $($_.Name) | CPUmin=$(if($p){[int]($p.CPU/60)}else{0}) | MemMB=$(if($p){[int]($p.WS/1MB)}else{0})"
  "   CMD: $($_.CommandLine)"
}
"=== ALL listening TCP ports (owning pid -> name) ==="
Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue | Sort-Object LocalPort -Unique | ForEach-Object {
  $op = Get-Process -Id $_.OwningProcess -ErrorAction SilentlyContinue
  "port $($_.LocalPort) <- pid $($_.OwningProcess) ($($op.ProcessName))"
} | Select-Object -First 40
