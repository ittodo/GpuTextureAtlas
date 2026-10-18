param(
    [string]$Clang = 'C:\Users\sksk1\AppData\Local\Programs\Swift\Toolchains\6.3.2+Asserts\usr\bin\clang.exe',
    [switch]$NoInstall
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$native = Join-Path $root 'Native'
$output = Join-Path $native 'build\arm64-v8a'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$library = Join-Path $output 'libAstcGpuUpload.so'
& $Clang --target=aarch64-linux-android24 -ffreestanding -fPIC -fvisibility=hidden -nostdlib -fno-exceptions -fno-rtti -fno-stack-protector -mno-outline-atomics -O2 -std=c++17 -shared -fuse-ld=lld -Werror -Wall -Wextra '-Wl,--no-undefined' '-Wl,-soname,libAstcGpuUpload.so' "-I$(Join-Path $native 'Unity')" "-I$(Join-Path $native 'Vulkan\include')" (Join-Path $native 'AstcGpuUploadVulkan.cpp') -o $library
if ($LASTEXITCODE -ne 0) { throw 'Android Vulkan native plug-in build failed.' }
$elf = [IO.File]::ReadAllBytes($library)
if ($elf[0] -ne 127 -or $elf[1] -ne 69 -or $elf[2] -ne 76 -or $elf[3] -ne 70 -or $elf[4] -ne 2 -or [BitConverter]::ToUInt16($elf,18) -ne 183) { throw 'Output is not an ELF64 AArch64 library.' }
if (-not $NoInstall) {
    $destination = Join-Path $root 'compute\Assets\Astc\Plugins\Android\arm64-v8a'
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    Copy-Item -LiteralPath $library -Destination $destination -Force
}
Write-Output "Built ELF64 AArch64 Vulkan bridge: $library"
Write-Output 'This checks compilation and ABI, not execution on an Android device.'
