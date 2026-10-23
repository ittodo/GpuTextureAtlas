param([string]$UnityEditor = '')
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $root 'compute'
$temporary = Join-Path $project 'Assets/TextureCompression/AndroidVerificationTemp'
$output = Join-Path $root '.validation/gpu-atlas/android'
if (-not $UnityEditor) { $UnityEditor = Join-Path $root '.tooling/Unity/6000.7.0b5/Editor/Unity.exe' }
if (Test-Path -LiteralPath $temporary) { throw 'The temporary Android verification folder already exists.' }
New-Item -ItemType Directory -Path $temporary -Force | Out-Null
New-Item -ItemType Directory -Path $output -Force | Out-Null
try {
    Copy-Item -LiteralPath (Join-Path $root 'Verification/GpuAtlasAndroidProbe.cs') -Destination $temporary
    foreach ($name in @('GpuAtlasCopyValidation.cs','GpuAtlasRendererValidation.cs')) {
        [IO.File]::WriteAllText((Join-Path $temporary $name), "#define GPU_ATLAS_PLAYER_PROBE`n" + [IO.File]::ReadAllText((Join-Path $project "Assets/TextureCompression/Editor/$name")))
    }
    Copy-Item -LiteralPath (Join-Path $project 'Assets/TextureCompression/Editor/GpuAtlasValidation.compute') -Destination $temporary
    $log = Join-Path $output 'build.log'
    $arguments = '-batchmode -force-d3d12 -buildTarget Android -projectPath "' + $project + '" -executeMethod UnityCompute.TextureCompression.Editor.GpuAtlasAndroidBuild.RunBatch -logFile "' + $log + '"'
    $process = Start-Process -FilePath $UnityEditor -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw "Android verification build failed (exit $($process.ExitCode)). See $log" }
    Get-Item -LiteralPath (Join-Path $output 'GpuAtlasVerification.apk') | Select-Object FullName,Length,LastWriteTime
} finally {
    foreach ($name in @('GpuAtlasAndroidProbe.cs','GpuAtlasCopyValidation.cs','GpuAtlasRendererValidation.cs','GpuAtlasValidation.compute','Probe.unity')) {
        foreach ($path in @((Join-Path $temporary $name),(Join-Path $temporary ($name + '.meta')))) {
            if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path }
        }
    }
    if ((Get-ChildItem -LiteralPath $temporary -Force | Measure-Object).Count -eq 0) {
        Remove-Item -LiteralPath $temporary
        if (Test-Path -LiteralPath ($temporary + '.meta')) { Remove-Item -LiteralPath ($temporary + '.meta') }
    }
}
