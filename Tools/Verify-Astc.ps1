param(
    [string]$UnityCli = 'C:\Program Files\Unity Hub\resources\unity.exe',
    [string]$ProjectPath = (Join-Path $PSScriptRoot '..\compute')
)

$ErrorActionPreference = 'Stop'
$ProjectPath = [IO.Path]::GetFullPath($ProjectPath)
$validationRoot = [IO.Path]::GetFullPath((Join-Path $ProjectPath '..\.validation'))
New-Item -ItemType Directory -Path $validationRoot -Force | Out-Null
if (-not (Test-Path -LiteralPath $UnityCli)) { throw "Unity CLI not found: $UnityCli" }

function Invoke-UnityJson([string[]]$CliArguments) {
    $responseText = & $UnityCli @CliArguments --json
    $responseExit = $LASTEXITCODE
    $response = ($responseText -join "`n") | ConvertFrom-Json
    if ($responseExit -ne 0 -or -not $response.success) { throw ($responseText -join "`n") }
    return $response
}

function Invoke-EditorCode([string]$Code) {
    $response = Invoke-UnityJson -CliArguments @('command', 'eval', $Code, '600000', '--timeout', '900',
        '--project-path', $ProjectPath, '--result-only')
    return $response.result
}

$state = Invoke-UnityJson -CliArguments @('status')
$instance = $state.data.instances | Where-Object { $_.project -eq $ProjectPath -and $_.state -eq 'ready' }
if (-not $instance) { throw "Open this Unity project and wait until the Pipeline package is ready: $ProjectPath" }

# Pin the official reference decoder archive. The SHA-256 is from Arm's 5.7.0 release-sha256.txt.
$archivePath = Join-Path $validationRoot 'astcenc.zip'
$expectedHash = 'CBDE7C78ADCC90D0D90ECF10F32D03F3340FBE1720CB6D6C892147D625E74D2D'
if (-not (Test-Path -LiteralPath $archivePath)) {
    Invoke-WebRequest -Uri 'https://github.com/ARM-software/astc-encoder/releases/download/5.7.0/astcenc-5.7.0-windows-x64.zip' -OutFile $archivePath
}
if ((Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash -ne $expectedHash) {
    throw "Reference decoder archive checksum differs: $archivePath"
}
Expand-Archive -LiteralPath $archivePath -DestinationPath (Join-Path $validationRoot 'astcenc') -Force

$compile = Invoke-UnityJson -CliArguments @('recompile', '--project-path', $ProjectPath, '--timeout', '120')
if ($compile.data.compilationFailed) { throw 'Unity C# compilation failed.' }
$ready = Invoke-UnityJson -CliArguments @('status', '--until-ready', '--timeout', '60', '--project-path', $ProjectPath)
Write-Output (Invoke-EditorCode 'return UnityCompute.Astc.Editor.AstcValidation.RunShaderCompilers();')
Write-Output (Invoke-EditorCode 'return UnityCompute.Astc.Editor.AstcValidation.Run();')
Write-Output (Invoke-EditorCode 'return UnityCompute.Astc.Editor.AstcValidation.Run(UnityCompute.Astc.AstcQuality.Fast);')
Write-Output (Invoke-EditorCode 'return UnityCompute.Astc.Editor.AstcValidation.Run(UnityCompute.Astc.AstcQuality.HighQuality);')
Write-Output (Invoke-EditorCode 'return UnityCompute.Astc.Editor.AstcValidation.RunGpuBuffers();')
Write-Output (Invoke-EditorCode 'return UnityCompute.Astc.Editor.AstcPeakMetricsValidation.Run();')
Write-Output (Invoke-EditorCode 'UnityCompute.Astc.Editor.AstcValidation.RunAsync(); return UnityCompute.Astc.Editor.AstcValidation.AsyncStatus;')
$asyncDeadline = [DateTime]::UtcNow.AddSeconds(30)
do {
    Start-Sleep -Milliseconds 500
    $asyncStatus = Invoke-EditorCode 'return UnityCompute.Astc.Editor.AstcValidation.AsyncStatus;'
} while ($asyncStatus -eq 'running' -and [DateTime]::UtcNow -lt $asyncDeadline)
if ($asyncStatus -notlike 'PASS:*') { throw "Async validation did not pass: $asyncStatus" }
Write-Output $asyncStatus
Write-Output "Reports: $validationRoot"
