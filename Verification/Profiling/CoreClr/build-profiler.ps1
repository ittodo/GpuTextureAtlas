param(
    [string]$VisualStudioRoot='C:/Program Files/Microsoft Visual Studio/18/Community',
    [string]$OutputDirectory=(Join-Path $PSScriptRoot '../../../.validation/coreclr-profiling')
)
$ErrorActionPreference='Stop'
$taskRoot=$PSScriptRoot
$vs=$VisualStudioRoot
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
$vc=(Get-ChildItem "$vs/VC/Tools/MSVC" -Directory | Sort-Object {[version]$_.Name} -Descending | Select-Object -First 1).FullName
$sdkRoot='C:/Program Files (x86)/Windows Kits/10'
$sdk=(Get-ChildItem "$sdkRoot/Include" -Directory | Sort-Object {[version]$_.Name} -Descending | Select-Object -First 1).Name
$env:INCLUDE=@("$vc/include","$sdkRoot/Include/$sdk/ucrt","$sdkRoot/Include/$sdk/shared","$sdkRoot/Include/$sdk/um","$vs/SDK/ScopeCppSDK/vc15/SDK/include/um") -join ';'
$env:LIB=@("$vc/lib/x64","$sdkRoot/Lib/$sdk/ucrt/x64","$sdkRoot/Lib/$sdk/um/x64") -join ';'
& "$vc/bin/Hostx64/x64/cl.exe" /nologo /LD /O2 /W3 /std:c++17 /MD /EHsc "$taskRoot/AllocationProfiler.cpp" "/Fo:$OutputDirectory/AllocationProfiler.obj" "/Fe:$OutputDirectory/AllocationProfiler.dll" /link /MACHINE:X64 "/DEF:$taskRoot/AllocationProfiler.def" "/IMPLIB:$OutputDirectory/AllocationProfiler.lib" ole32.lib
if($LASTEXITCODE -ne 0){throw 'CoreCLR profiler compilation failed.'}
