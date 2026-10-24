param([string]$UnityEditor = '')
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $root 'compute'
$temporary = Join-Path $project 'Assets/TextureCompression/MobileBenchmarkTemp'
$output = Join-Path $root '.validation/gpu-atlas/mobile'
if (-not $UnityEditor) { $UnityEditor = Join-Path $root '.tooling/Unity/6000.7.0b5/Editor/Unity.exe' }
if (Test-Path -LiteralPath $temporary) { throw 'The temporary benchmark folder already exists.' }
New-Item -ItemType Directory -Path (Join-Path $temporary 'Editor'),$output -Force | Out-Null
try {
    Copy-Item -LiteralPath (Join-Path $root 'Verification/GpuAtlasMobileBenchmark.cs') -Destination $temporary
    Copy-Item -LiteralPath (Join-Path $root 'Verification/Editor/GpuAtlasMobileBenchmarkBuild.cs') -Destination (Join-Path $temporary 'Editor')
    [IO.File]::WriteAllText((Join-Path $temporary 'GpuAtlasCopyValidation.cs'), "#define GPU_ATLAS_PLAYER_PROBE`n" + [IO.File]::ReadAllText((Join-Path $project 'Assets/TextureCompression/Editor/GpuAtlasCopyValidation.cs')))
    $log = Join-Path $output 'build.log'
    $arguments = '-batchmode -force-d3d12 -buildTarget Android -projectPath "' + $project + '" -executeMethod UnityCompute.TextureCompression.Editor.GpuAtlasMobileBenchmarkBuild.RunBatch -logFile "' + $log + '"'
    $process = Start-Process -FilePath $UnityEditor -ArgumentList $arguments -WindowStyle Hidden -PassThru
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) { throw "Mobile benchmark build failed (exit $($process.ExitCode)). See $log" }
    Get-Item -LiteralPath (Join-Path $output 'GpuAtlasBenchmark.apk') | Select-Object FullName,Length,LastWriteTime
} finally {
    foreach ($name in @('GpuAtlasMobileBenchmark.cs','GpuAtlasCopyValidation.cs','Editor/GpuAtlasMobileBenchmarkBuild.cs','Benchmark.unity')) {
        foreach ($path in @((Join-Path $temporary $name),(Join-Path $temporary ($name + '.meta')))) {
            if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path }
        }
    }
    foreach ($folder in @((Join-Path $temporary 'Editor'), $temporary)) {
        if ((Test-Path -LiteralPath $folder) -and (Get-ChildItem -LiteralPath $folder -Force | Measure-Object).Count -eq 0) {
            Remove-Item -LiteralPath $folder
            if (Test-Path -LiteralPath ($folder + '.meta')) { Remove-Item -LiteralPath ($folder + '.meta') }
        }
    }
}
