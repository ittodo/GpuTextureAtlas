param([string]$UnityCli = 'C:\Program Files\Unity Hub\resources\unity.exe', [switch]$BatchMode, [string]$UnityEditor = '', [switch]$DebugGpu)
$ErrorActionPreference = 'Stop'
$project = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../compute'))
$output = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../.validation/gpu-atlas'))
New-Item -ItemType Directory -Path $output -Force | Out-Null
if ($BatchMode) {
    if (-not $UnityEditor) { $UnityEditor = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../.tooling/Unity/6000.7.0b5/Editor/Unity.exe')) }
    $result = Join-Path $output 'batch-result.txt'; if (Test-Path -LiteralPath $result) { Remove-Item -LiteralPath $result }
    $log = Join-Path $output 'batch-editor.log'
    $arguments = '-batchmode -force-d3d12 -projectPath "' + $project + '" -executeMethod UnityCompute.TextureCompression.Editor.GpuAtlasValidation.RunBatch -logFile "' + $log + '"'
    if ($DebugGpu) { $arguments += ' -force-d3d12-debug' }
    $process = Start-Process -FilePath $UnityEditor -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
    if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $result)) { throw "GPU atlas verification failed (exit $($process.ExitCode)). See $log" }
    Get-Content -LiteralPath $result
    $addition = Get-Content -LiteralPath (Join-Path $output 'addition.json') -Raw | ConvertFrom-Json
    if (-not $addition.passed) { throw 'GPU atlas additions failed.' }
    "PASS: AddAsync $($addition.atlases) atlases, $($addition.additions) additions, $($addition.growths) growths, $($addition.inPlace) in-place additions, $($addition.exactPixels) exact pixels, $($addition.convertedPixels) converted pixels, max $($addition.maximumDifference)/255; rejection and in-flight disposal checked."
    $editing = Get-Content -LiteralPath (Join-Path $output 'editing.json') -Raw | ConvertFrom-Json
    if (-not $editing.passed) { throw 'GPU atlas removal and repacking failed.' }
    "PASS: Remove/Repack $($editing.cases) cases, $($editing.removed) removals, $($editing.repacks) repacks, $($editing.reusedHoles) reused holes, $($editing.pixels) exact block pixels, $($editing.uvPixels) UV samples; stable IDs, empty refill and in-flight disposal checked."
    $renderer = Get-Content -LiteralPath (Join-Path $output 'renderer/result.json') -Raw | ConvertFrom-Json
    if (-not $renderer.passed) { throw 'GPU atlas renderer verification failed.' }
    "PASS: Renderer $($renderer.cases) pipeline/color cases, $($renderer.frames) frames ($($renderer.slicedFrames) sliced, $($renderer.maskFrames) mask), $($renderer.pixels) pixels, maximum difference $($renderer.maximumDifference)."
    return
}
function Invoke-Code([string]$code) {
    $raw = & $UnityCli command eval $code 600000 --timeout 900 --project-path $project --json --result-only
    $codeExit = $LASTEXITCODE; $result = ($raw -join "`n") | ConvertFrom-Json
    if ($codeExit -ne 0 -or -not $result.success) { throw ($raw -join "`n") }; $result.result
}
& $UnityCli status --until-ready --timeout 60 --project-path $project --json | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Open this project and wait for Pipeline readiness.' }
Invoke-Code 'UnityCompute.TextureCompression.Editor.GpuAtlasValidation.RunAsync();return "running";'
$deadline = [DateTime]::UtcNow.AddMinutes(8)
do { Start-Sleep -Seconds 2; $status = Invoke-Code 'return UnityCompute.TextureCompression.Editor.GpuAtlasValidation.Status;' } while ($status -eq 'running' -and [DateTime]::UtcNow -lt $deadline)
if ($status -notlike 'PASS:*') { throw $status }; $status
