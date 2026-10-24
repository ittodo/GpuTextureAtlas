param([string]$UnityEditor = '', [string]$ResumeProject = '')
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$package = Join-Path $root 'Packages/com.ittodo.gpu-texture-atlas'
if (-not $UnityEditor) { $UnityEditor = Join-Path $root '.tooling/Unity/6000.7.0b5/Editor/Unity.exe' }
$project = Join-Path $root ('.validation/gpu-atlas/package-consumer-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
if ($ResumeProject) {
    $project = [IO.Path]::GetFullPath($ResumeProject)
    $prefix = [IO.Path]::GetFullPath((Join-Path $root '.validation/gpu-atlas/package-consumer-'))
    if (-not $project.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or -not (Test-Path -LiteralPath (Join-Path $project 'Assets/Verification/Editor/GpuAtlasPackageBuild.cs'))) { throw 'ResumeProject must be a consumer project created by this tool.' }
}
New-Item -ItemType Directory -Path (Join-Path $project 'Assets/Verification/Editor'),(Join-Path $project 'Packages'),(Join-Path $project 'ProjectSettings') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'compute/ProjectSettings/ProjectVersion.txt') -Destination (Join-Path $project 'ProjectSettings')
$dependencies = [ordered]@{
    'com.ittodo.gpu-texture-atlas' = ('file:' + $package.Replace('\','/'))
    'com.unity.addressables' = '4.1.1'
    'com.unity.modules.assetbundle' = '1.0.0'
    'com.unity.modules.imageconversion' = '1.0.0'
    'com.unity.modules.imgui' = '1.0.0'
    'com.unity.modules.jsonserialize' = '1.0.0'
    'com.unity.modules.spritemask' = '1.0.0'
    'com.unity.modules.unitywebrequest' = '1.0.0'
    'com.unity.modules.unitywebrequestassetbundle' = '1.0.0'
}
@{dependencies=$dependencies} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $project 'Packages/manifest.json') -Encoding utf8NoBOM
Copy-Item -LiteralPath (Join-Path $package 'Samples~/DynamicAtlas') -Destination (Join-Path $project 'Assets') -Recurse -Force
Copy-Item -LiteralPath (Join-Path $root 'Verification/GpuAtlasPackageProbe.cs') -Destination (Join-Path $project 'Assets/Verification')
Copy-Item -LiteralPath (Join-Path $root 'Verification/Editor/GpuAtlasPackageBuild.cs') -Destination (Join-Path $project 'Assets/Verification/Editor')
$verification = Join-Path $root 'compute/Assets/TextureCompression/Editor'
[IO.File]::WriteAllText((Join-Path $project 'Assets/Verification/GpuAtlasCopyValidation.cs'), "#define GPU_ATLAS_PLAYER_PROBE`n" + [IO.File]::ReadAllText((Join-Path $verification 'GpuAtlasCopyValidation.cs')))
Copy-Item -LiteralPath (Join-Path $verification 'GpuAtlasValidation.compute') -Destination (Join-Path $project 'Assets/Verification')
$buildLog = Join-Path $project 'build.log'
$arguments = '-batchmode -force-d3d12 -projectPath "' + $project + '" -executeMethod GpuAtlasPackageBuild.RunBatch -logFile "' + $buildLog + '"'
$build = Start-Process -FilePath $UnityEditor -ArgumentList $arguments -WindowStyle Hidden -PassThru
$build.WaitForExit()
if ($build.ExitCode -ne 0) { throw "Fresh package build failed. See $buildLog" }
$report = Join-Path $project 'result.json'; $log = Join-Path $project 'player.log'
if (Test-Path -LiteralPath $report) { Move-Item -LiteralPath $report -Destination (Join-Path $project ('result-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.json')) }
$arguments = '-force-d3d12 -screen-fullscreen 0 -screen-width 800 -screen-height 600 -atlas-report "' + $report + '" -logFile "' + $log + '"'
$player = Start-Process -FilePath (Join-Path $project 'Build/PackageProbe.exe') -ArgumentList $arguments -WindowStyle Hidden -PassThru
if (-not $player.WaitForExit(600000)) { $player.Kill(); throw 'Package verification Player timed out.' }
if (-not (Test-Path -LiteralPath $report)) { throw "Player did not write a report. See $log" }
$result = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
$result | ConvertTo-Json
Write-Output "Reports: $project"
if ($player.ExitCode -ne 0 -or -not $result.passed) { throw 'Fresh package / Addressables verification failed.' }
