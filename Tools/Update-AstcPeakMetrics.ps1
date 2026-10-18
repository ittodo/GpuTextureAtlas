param([string]$UnityCli='C:\Program Files\Unity Hub\resources\unity.exe')
$ErrorActionPreference='Stop'
$project=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\compute'))
$code='return UnityCompute.Astc.Editor.AstcBenchmark.RefreshPeakMetrics();'
$outputText=& $UnityCli command eval $code 600000 --timeout 900 --project-path $project --json --result-only
$result=($outputText -join "`n") | ConvertFrom-Json
if($LASTEXITCODE -ne 0 -or -not $result.success){throw ($outputText -join "`n")}
Write-Output $result.result
