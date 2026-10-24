param(
    [string]$EditorData=(Join-Path $PSScriptRoot '../../../.tooling/Unity/6000.7.0b5/Editor/Data'),
    [string]$OutputDirectory=(Join-Path $PSScriptRoot '../../../.validation/android-il2cpp-profiling/native')
)
$ErrorActionPreference='Stop'
$editor=[IO.Path]::GetFullPath($EditorData)
$ndk=Join-Path $editor 'PlaybackEngines/AndroidPlayer/NDK/toolchains/llvm/prebuilt/windows-x86_64'
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
$output=Join-Path $OutputDirectory 'libIl2CppAllocationProbe.so'
$temporary=Join-Path $OutputDirectory 'temp'
New-Item -ItemType Directory -Force $OutputDirectory,$temporary | Out-Null
$env:TEMP=$temporary;$env:TMP=$temporary
& "$ndk/bin/clang++.exe" --target=aarch64-linux-android26 "--sysroot=$ndk/sysroot" -shared -fPIC -fvisibility=hidden -O2 -std=c++17 -static-libstdc++ -Werror -Wall -Wextra "-I$editor/il2cpp/libil2cpp" (Join-Path $PSScriptRoot 'Native/Il2CppAllocationProbe.cpp') -ldl -llog '-Wl,-z,max-page-size=16384' '-Wl,--no-undefined' -o $output
if($LASTEXITCODE -ne 0){throw 'Android profiler compilation failed.'}
& "$ndk/bin/llvm-readelf.exe" -h $output
