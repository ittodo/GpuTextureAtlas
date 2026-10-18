param([string]$UnityCli='C:\Program Files\Unity Hub\resources\unity.exe')
$ErrorActionPreference='Stop'
$project=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../compute'))
& $UnityCli status --until-ready --timeout 60 --project-path $project --json|Out-Null
$raw=& $UnityCli command eval 'return UnityCompute.TextureCompression.Editor.FrameSlicedBenchmark.Run();' 600000 --timeout 900 --project-path $project --json --result-only
$codeExit=$LASTEXITCODE;$result=($raw -join "`n")|ConvertFrom-Json
if($codeExit -ne 0 -or -not $result.success){throw ($raw -join "`n")};$result.result
