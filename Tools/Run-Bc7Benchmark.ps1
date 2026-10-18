param([string]$UnityCli='C:\Program Files\Unity Hub\resources\unity.exe')
$ErrorActionPreference='Stop'
$project=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\compute'))
$outputText=& $UnityCli command eval 'return UnityCompute.Bc7.Editor.Bc7Benchmark.Run();' 600000 --timeout 900 --project-path $project --json --result-only
$code=$LASTEXITCODE;$result=($outputText -join "`n") | ConvertFrom-Json
if($code -ne 0 -or -not $result.success){throw ($outputText -join "`n")}
Write-Output $result.result
