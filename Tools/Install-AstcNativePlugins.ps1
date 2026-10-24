param([switch]$SkipAndroidBuild)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if(-not $SkipAndroidBuild) { & (Join-Path $PSScriptRoot 'Build-AstcAndroidPlugin.ps1') }
$ios=Join-Path $root 'Packages/com.ittodo.gpu-texture-atlas/Plugins/Astc/iOS'
New-Item -ItemType Directory -Path $ios -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'Native/AstcGpuUploadMetal.mm') -Destination $ios -Force
foreach($name in @('IUnityInterface.h','IUnityGraphics.h','IUnityGraphicsMetal.h','LICENSE.md')) {
    Copy-Item -LiteralPath (Join-Path $root "Native/Unity/$name") -Destination $ios -Force
}
Write-Output 'Installed native plug-in files. Run AstcNativePluginSetup.Configure in the Unity Editor to set platform import flags.'
