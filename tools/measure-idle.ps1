# Measures Wipriss at rest: memory and CPU while nobody touches it.
# Usage:  powershell -ExecutionPolicy Bypass -File tools\measure-idle.ps1 -Exe <path to Wipriss.exe>
param(
    [Parameter(Mandatory)] [string]$Exe,
    [int]$WarmupSeconds = 10,
    [int]$SampleSeconds = 30
)

Get-Process Wipriss -ErrorAction SilentlyContinue | Stop-Process -Force
$sw = [Diagnostics.Stopwatch]::StartNew()
$p = Start-Process $Exe -PassThru
$p.WaitForInputIdle(15000) | Out-Null
$startupMs = $sw.ElapsedMilliseconds

Start-Sleep -Seconds $WarmupSeconds
$p.Refresh()
$cpuStart = $p.TotalProcessorTime
$wall = [Diagnostics.Stopwatch]::StartNew()
Start-Sleep -Seconds $SampleSeconds
$p.Refresh()
$cpuPercent = ($p.TotalProcessorTime - $cpuStart).TotalMilliseconds / $wall.Elapsed.TotalMilliseconds / [Environment]::ProcessorCount * 100

# "Memory" column in Task Manager = private working set
$perf = Get-CimInstance Win32_PerfFormattedData_PerfProc_Process -Filter "IDProcess=$($p.Id)"

[pscustomobject]@{
    StartupMs          = $startupMs
    TaskManagerMemMB   = [math]::Round($perf.WorkingSetPrivate / 1MB, 1)
    WorkingSetMB       = [math]::Round($p.WorkingSet64 / 1MB, 1)
    PrivateBytesMB     = [math]::Round($p.PrivateMemorySize64 / 1MB, 1)
    CpuPercent         = [math]::Round($cpuPercent, 3)
    CpuMsDuringSample  = [math]::Round(($p.TotalProcessorTime - $cpuStart).TotalMilliseconds, 1)
    Threads            = $p.Threads.Count
    Handles            = $p.HandleCount
} | Format-List
