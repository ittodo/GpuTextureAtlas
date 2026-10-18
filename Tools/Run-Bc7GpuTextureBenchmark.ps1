param([string]$UnityCli='C:\Program Files\Unity Hub\resources\unity.exe')
$ErrorActionPreference='Stop'
$project=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../compute'))
$text=& $UnityCli command eval 'return UnityCompute.Bc7.Editor.Bc7GpuTextureBenchmark.Run();' 600000 --timeout 900 --project-path $project --json --result-only
$exitCode=$LASTEXITCODE;$result=($text -join "`n")|ConvertFrom-Json
if($exitCode -ne 0 -or -not $result.success){throw ($text -join "`n")}
Write-Output $result.result
