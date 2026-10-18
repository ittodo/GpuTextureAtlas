param([string]$UnityCli = 'C:\Program Files\Unity Hub\resources\unity.exe', [switch]$SkipAssetBundle,
    [switch]$BatchMode, [string]$UnityEditor = '')
$ErrorActionPreference = 'Stop'
$project = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../compute'))
if ($BatchMode) {
    if ($SkipAssetBundle) { throw '-BatchMode runs both block and AssetBundle verification.' }
    if (-not $UnityEditor) { $UnityEditor = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../.tooling/Unity/6000.7.0b5/Editor/Unity.exe')) }
    if (-not (Test-Path -LiteralPath $UnityEditor)) { throw 'Specify the full Unity.exe path with -UnityEditor.' }
    $output = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../.validation/compressed-atlas'))
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    $log = Join-Path $output 'batch-editor.log'
    $resultFile = Join-Path $output 'batch-result.txt'
    if (Test-Path -LiteralPath $resultFile) { Remove-Item -LiteralPath $resultFile }
    $arguments = '-batchmode -force-d3d12 -projectPath "' + $project + '" -executeMethod UnityCompute.TextureCompression.Editor.CompressedAtlasValidation.RunBatch -logFile "' + $log + '"'
    $process = Start-Process -FilePath $UnityEditor -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
    if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $resultFile)) { throw "Unity verification failed (exit $($process.ExitCode)). See $log" }
    Get-Content -LiteralPath $resultFile
    return
}
function Invoke-Code([string]$code) {
    $raw = & $UnityCli command eval $code 600000 --timeout 900 --project-path $project --json --result-only
    $codeExit = $LASTEXITCODE
    $result = ($raw -join "`n") | ConvertFrom-Json
    if ($codeExit -ne 0 -or -not $result.success) { throw ($raw -join "`n") }
    $result.result
}
& $UnityCli status --until-ready --timeout 60 --project-path $project --json | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Open the Unity project and wait for Pipeline readiness.' }
Invoke-Code 'return UnityCompute.TextureCompression.Editor.CompressedAtlasValidation.RunBlocks();'
if (-not $SkipAssetBundle) { Invoke-Code 'return UnityCompute.TextureCompression.Editor.CompressedAtlasValidation.RunAssetBundle();' }
