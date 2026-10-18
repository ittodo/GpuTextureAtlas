param(
    [string]$UnityEditor,
    [string]$VisualStudio='C:\Program Files\Microsoft Visual Studio\2022\Community',
    [string]$WindowsSdk='C:\Program Files (x86)\Windows Kits\10',
    [switch]$NoInstall,
    [string]$UnityCli='C:\Program Files\Unity Hub\resources\unity.exe'
)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$native=Join-Path $root 'Native'
if(-not $UnityEditor){
    $versionFile=Join-Path $root 'compute/ProjectSettings/ProjectVersion.txt'
    $versionLine=Get-Content -LiteralPath $versionFile|Where-Object {$_ -match '^m_EditorVersion: ' }|Select-Object -First 1
    if(-not $versionLine){throw 'Project Editor version is missing. Supply -UnityEditor explicitly.'}
    $version=$versionLine.Substring('m_EditorVersion: '.Length).Trim()
    $candidates=@()
    if(Test-Path -LiteralPath $UnityCli){
        $editorText=& $UnityCli editors --json
        if($LASTEXITCODE -ne 0){throw 'Unity CLI could not list Editors. Supply -UnityEditor explicitly.'}
        $editors=($editorText -join "`n")|ConvertFrom-Json
        if(-not $editors.success){throw 'Unity CLI could not list Editors. Supply -UnityEditor explicitly.'}
        $candidates+=@($editors.data|Where-Object {$_.version -eq $version -and $_.location}|ForEach-Object {Split-Path -Parent ([IO.Path]::GetFullPath($_.location))})
    }
    $candidates+=Join-Path 'C:\Program Files\Unity\Hub\Editor' "$version/Editor"
    $UnityEditor=$candidates|Where-Object {Test-Path -LiteralPath (Join-Path $_ 'Data/PluginAPI/IUnityGraphicsD3D12.h')}|Select-Object -First 1
    if(-not $UnityEditor){throw "Unity $version D3D12 SDK was not found. Supply -UnityEditor explicitly."}
}
$sdkHeader=Join-Path $UnityEditor 'Data/PluginAPI/IUnityGraphicsD3D12.h'
if(-not (Test-Path -LiteralPath $sdkHeader)){throw "Unity D3D12 SDK header is missing: $sdkHeader"}
Write-Output "Unity SDK: $UnityEditor"
$vc=(Get-ChildItem (Join-Path $VisualStudio 'VC/Tools/MSVC') -Directory|Sort-Object {[version]$_.Name} -Descending|Select-Object -First 1).FullName
$sdk=(Get-ChildItem (Join-Path $WindowsSdk 'Include') -Directory|Sort-Object {[version]$_.Name} -Descending|Select-Object -First 1).Name
if(-not $vc -or -not $sdk){throw 'MSVC x64 and Windows SDK are required. Supply their installation paths.'}
$compiler=Join-Path $vc 'bin/Hostx64/x64/cl.exe'
$output=Join-Path $native 'build/Windows-x86_64'
New-Item -ItemType Directory -Path $output -Force|Out-Null
$library=Join-Path $output 'Bc7GpuUpload.dll'
# Keep the exact Unity SDK header beside the other licensed Unity headers.
Copy-Item -LiteralPath $sdkHeader -Destination (Join-Path $native 'Unity/IUnityGraphicsD3D12.h') -Force
$savedInclude=$env:INCLUDE;$savedLib=$env:LIB;$savedPath=$env:PATH
try{
    $env:INCLUDE=@((Join-Path $vc 'include'),(Join-Path $WindowsSdk "Include/$sdk/ucrt"),(Join-Path $WindowsSdk "Include/$sdk/shared"),(Join-Path $WindowsSdk "Include/$sdk/um")) -join ';'
    $env:LIB=@((Join-Path $vc 'lib/x64'),(Join-Path $WindowsSdk "Lib/$sdk/ucrt/x64"),(Join-Path $WindowsSdk "Lib/$sdk/um/x64")) -join ';'
    $env:PATH=(Join-Path $vc 'bin/Hostx64/x64')+';'+$savedPath
    & $compiler /nologo /LD /O2 /W4 /WX /std:c++17 /MD /EHsc "/I$(Join-Path $native 'Unity')" (Join-Path $native 'Bc7GpuUploadD3D12.cpp') "/Fo:$output/Bc7GpuUpload.obj" "/Fe:$library" /link /MACHINE:X64 "/IMPLIB:$output/Bc7GpuUpload.lib"
    if($LASTEXITCODE -ne 0){throw 'BC7 D3D12 native build failed.'}
}finally{$env:INCLUDE=$savedInclude;$env:LIB=$savedLib;$env:PATH=$savedPath}
if(-not $NoInstall){
    $destination=Join-Path $root 'compute/Assets/Bc7/Plugins/Windows/x86_64'
    New-Item -ItemType Directory -Path $destination -Force|Out-Null
    Copy-Item -LiteralPath $library -Destination $destination -Force
}
Write-Output "Built Windows x64 D3D12 BC7 bridge: $library"
