param([string]$UnityCli='C:\Program Files\Unity Hub\resources\unity.exe')
$ErrorActionPreference='Stop'
$project = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\compute'))
function Invoke-BenchmarkCode([string]$Code) {
    $outputText = & $UnityCli command eval $Code 600000 --timeout 900 --project-path $project --json --result-only
    $exitCode = $LASTEXITCODE
    $result = ($outputText -join "`n") | ConvertFrom-Json
    if ($exitCode -ne 0 -or -not $result.success) { throw ($outputText -join "`n") }
    Write-Output $result.result
}
Invoke-BenchmarkCode 'return UnityCompute.Astc.Editor.AstcBenchmark.Run("baseline", true);'
Invoke-BenchmarkCode 'return UnityCompute.Astc.Editor.AstcBenchmark.Run("fast", false);'
Invoke-BenchmarkCode 'return UnityCompute.Astc.Editor.AstcBenchmark.Run("balanced", false);'
