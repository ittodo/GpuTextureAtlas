param(
    [string]$UnityCli='C:\Program Files\Unity Hub\resources\unity.exe',
    [ValidateSet(256,512,1024)][int[]]$Resolution=@(256,512,1024)
)
$ErrorActionPreference='Stop'
$project=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../compute'))
foreach($size in $Resolution){
    & $UnityCli status --until-ready --timeout 60 --project-path $project --json|Out-Null
    $text=& $UnityCli command eval "return UnityCompute.Astc.Editor.HighQualityBenchmark.Run($size);" 600000 --timeout 900 --project-path $project --json --result-only
    $exitCode=$LASTEXITCODE;$result=($text -join "`n")|ConvertFrom-Json
    if($exitCode -ne 0 -or -not $result.success){throw ($text -join "`n")}
    Write-Output $result.result
}
