param([string]$UnityEditor = '')
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $root 'compute'
$temporary = Join-Path $project 'Assets/TextureCompression/VerificationTemp'
$output = Join-Path $root '.validation/gpu-atlas/player'
if (-not $UnityEditor) { $UnityEditor = Join-Path $root '.tooling/Unity/6000.7.0b5/Editor/Unity.exe' }
if (Test-Path -LiteralPath $temporary) { throw 'The temporary player asset folder already exists.' }
New-Item -ItemType Directory -Path $temporary -Force | Out-Null
New-Item -ItemType Directory -Path $output -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'Verification/GpuAtlasPlayerProbe.cs') -Destination $temporary
Copy-Item -LiteralPath (Join-Path $root 'Verification/GpuAtlasSchedulingValidation.cs') -Destination $temporary
[IO.File]::WriteAllText((Join-Path $temporary 'GpuAtlasCopyValidation.cs'), "#define GPU_ATLAS_PLAYER_PROBE`n" + [IO.File]::ReadAllText((Join-Path $project 'Assets/TextureCompression/Editor/GpuAtlasCopyValidation.cs')))
[IO.File]::WriteAllText((Join-Path $temporary 'GpuAtlasRendererValidation.cs'), "#define GPU_ATLAS_PLAYER_PROBE`n" + [IO.File]::ReadAllText((Join-Path $project 'Assets/TextureCompression/Editor/GpuAtlasRendererValidation.cs')))
Copy-Item -LiteralPath (Join-Path $project 'Assets/TextureCompression/Editor/GpuAtlasValidation.compute') -Destination (Join-Path $temporary 'GpuAtlasValidation.compute')
try {
    $buildLog = Join-Path $output 'build.log'
    $arguments = '-batchmode -force-d3d12 -buildTarget StandaloneWindows64 -projectPath "' + $project + '" -executeMethod UnityCompute.TextureCompression.Editor.GpuAtlasPlayerBuild.RunBatch -logFile "' + $buildLog + '"'
    $build = Start-Process -FilePath $UnityEditor -ArgumentList $arguments -WindowStyle Hidden -PassThru
    $build.WaitForExit()
    if ($build.ExitCode -ne 0) { throw "Player build failed. See $buildLog" }
    $exe = Join-Path $output 'GpuAtlasProbe.exe'; $report = Join-Path $output 'result.json'; $log = Join-Path $output 'player.log'
    if (Test-Path -LiteralPath $report) { Remove-Item -LiteralPath $report }
    $bundle = Join-Path $root '.validation/gpu-atlas/bundle/gpu-atlas-inputs'
    $arguments = '-force-d3d12 -screen-fullscreen 0 -screen-width 640 -screen-height 360 -gpu-atlas-bundle "' + $bundle + '" -gpu-atlas-report "' + $report + '" -logFile "' + $log + '"'
    $player = Start-Process -FilePath $exe -ArgumentList $arguments -WindowStyle Hidden -PassThru
    if (-not $player.WaitForExit(300000)) { $player.Kill(); throw 'GPU atlas verification player timed out.' }
    if (-not (Test-Path -LiteralPath $report)) { throw "Player did not write a report. See $log" }
    $result = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
    if ($player.ExitCode -ne 0 -or -not $result.passed) { throw ($result | ConvertTo-Json -Depth 5) }
    "PASS: Player $($result.sources) unreadable bundle textures, $($result.copied) unchanged + $($result.converted) converted, $($result.exactPixels) exact pixels, $($result.retainedPixels) retained pixels, $($result.frames) frames, zero pending releases."
    "PASS: Player AddAsync $($result.additions) additions, $($result.growths) growths, $($result.inPlace) in-place additions, $($result.addedPixels) preserved and converted pixels."
    "PASS: Player Remove/Repack $($result.removed) removals, $($result.repacks) repacks, $($result.editedPixels) exact pixels and UVs."
    $renderer = Get-Content -LiteralPath (Join-Path $output 'renderer/result.json') -Raw | ConvertFrom-Json
    if (-not $result.rendererPassed -or -not $renderer.passed) { throw 'Player renderer validation failed.' }
    "PASS: Player Renderer $($renderer.cases) pipeline/color cases, $($renderer.frames) frames ($($renderer.slicedFrames) sliced, $($renderer.maskFrames) mask), $($renderer.pixels) pixels, max difference $($renderer.maximumDifference)."
} finally {
    # Only known files created by this tool. Keep any unexpected contents.
    foreach ($name in @('GpuAtlasPlayerProbe.cs','GpuAtlasSchedulingValidation.cs','GpuAtlasCopyValidation.cs','GpuAtlasRendererValidation.cs','GpuAtlasValidation.compute','Probe.unity')) {
        foreach ($path in @((Join-Path $temporary $name),(Join-Path $temporary ($name+'.meta')))) {
            if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path }
        }
    }
    if ((Get-ChildItem -LiteralPath $temporary -Force | Measure-Object).Count -eq 0) {
        Remove-Item -LiteralPath $temporary
        if (Test-Path -LiteralPath ($temporary+'.meta')) { Remove-Item -LiteralPath ($temporary+'.meta') }
    }
}
