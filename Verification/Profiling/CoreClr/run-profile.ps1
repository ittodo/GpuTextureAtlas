param([Parameter(Mandatory)][string]$Executable,[string[]]$PlayerArguments=@(),[Parameter(Mandatory)][string]$Output,[int]$TimeoutSeconds=180,
    [string]$ProfilerPath=(Join-Path $PSScriptRoot '../../../.validation/coreclr-profiling/AllocationProfiler.dll'))
$ErrorActionPreference='Stop'
$env:CORECLR_ENABLE_PROFILING='1'
$env:CORECLR_PROFILER='{B52F6D9C-5EAF-4730-9AE5-1E481BA46712}'
$env:CORECLR_PROFILER_PATH=[IO.Path]::GetFullPath($ProfilerPath)
$env:CORECLR_PROFILER_PATH_64=$env:CORECLR_PROFILER_PATH
$env:ATLAS_CLR_PROFILE_OUTPUT=[IO.Path]::GetFullPath($Output)
New-Item -ItemType Directory -Force (Split-Path $env:ATLAS_CLR_PROFILE_OUTPUT -Parent) | Out-Null
# Keep this verification run's GC non-concurrent so all callback accounting has a stable boundary.
$env:DOTNET_gcConcurrent='0'
$process=Start-Process -FilePath $Executable -ArgumentList $PlayerArguments -WindowStyle Hidden -PassThru
if (-not $process.WaitForExit($TimeoutSeconds*1000)) { $process.Kill(); throw 'The diagnostic run timed out.' }
Write-Output "Player exit: $($process.ExitCode)"
if (-not (Test-Path -LiteralPath $Output)) { throw 'The CLR profiler did not produce output.' }
& node (Join-Path $PSScriptRoot 'finalize-profile.mjs') $Output
if ($LASTEXITCODE -ne 0) { throw 'The final profiler snapshot did not validate.' }
$report=Get-Content -LiteralPath $Output -Raw | ConvertFrom-Json
$report | Select-Object pid,shutdown,collisions,boundsFailures,nestedGc,@{n='types';e={$_.types.Count}},@{n='GCs';e={$_.gc.Count}}
if ($process.ExitCode -ne 0) { throw 'The profiled process failed.' }
