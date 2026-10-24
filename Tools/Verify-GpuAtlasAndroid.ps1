param([string]$Serial = '', [string]$Adb = '', [int]$TimeoutSeconds = 600)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $Adb) { $Adb = Join-Path $root '.tooling/Unity/6000.7.0b5/Editor/Data/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb.exe' }
$output = Join-Path $root '.validation/gpu-atlas/android'
$apk = Join-Path $output 'GpuAtlasVerification.apk'
$package = 'com.ittodo.gputextureatlas.validation'
if (-not (Test-Path -LiteralPath $apk)) { throw 'Build the test APK with Build-GpuAtlasAndroidProbe.ps1 first.' }
if (-not $Serial) {
    $devices = @(& $Adb devices | Select-String '^([^\s]+)\s+device$' | ForEach-Object { $_.Matches[0].Groups[1].Value })
    if ($devices.Count -ne 1) { throw 'Connect and authorize one Android device, or pass -Serial.' }
    $Serial = $devices[0]
}
function Invoke-Adb([string[]]$Arguments) {
    $result = & $Adb -s $Serial @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw "adb $($Arguments[0]) failed: $($result -join ' ')" }
    return $result
}
Invoke-Adb @('install','-r',$apk) | Out-Null
$activity = (Invoke-Adb @('shell','cmd','package','resolve-activity','--brief',$package) | Where-Object { $_ -match '^[\w.]+/[\w.]+' } | Select-Object -Last 1).Trim()
if (-not $activity) { throw 'Cannot resolve the test app activity.' }
$runOutput = Join-Path $output ('device-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $runOutput -Force | Out-Null
$remote = "/sdcard/Android/data/$package/files/gpu-atlas"
Invoke-Adb @('shell','am','force-stop',$package) | Out-Null
# Only reports written by this test package. No user application files are removed.
Invoke-Adb @('shell','rm','-f',"$remote/result.json","$remote/progress.json") | Out-Null
Invoke-Adb @('shell','input','keyevent','KEYCODE_WAKEUP') | Out-Null
Invoke-Adb @('shell','am','start','-W','-n',$activity) | Out-Null
$testProcessId = (Invoke-Adb @('shell','pidof',$package) -join '').Trim()
$deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
$lastPhase = ''; $complete = $false
try {
    while ([DateTime]::UtcNow -lt $deadline) {
        $raw = Invoke-Adb @('shell',"if [ -f '$remote/result.json' ]; then cat '$remote/result.json'; fi")
        if ($raw) { $complete = $true; break }
        $progressRaw = Invoke-Adb @('shell',"if [ -f '$remote/progress.json' ]; then cat '$remote/progress.json'; fi")
        if ($progressRaw) {
            $progress = ($progressRaw -join "`n") | ConvertFrom-Json
            if ($progress.phase -ne $lastPhase) { $lastPhase = $progress.phase; Write-Output $lastPhase }
        }
        Start-Sleep -Seconds 5
    }
} finally {
    & $Adb -s $Serial pull $remote $runOutput | Out-Null
    if ($testProcessId -match '^\d+$') { & $Adb -s $Serial logcat -d --pid=$testProcessId -v threadtime | Set-Content -LiteralPath (Join-Path $runOutput 'player.log') -Encoding utf8 }
}
if (-not $complete) { throw "Device test timed out. See $runOutput" }
$report = Get-Content -LiteralPath (Join-Path $runOutput 'gpu-atlas/result.json') -Raw | ConvertFrom-Json
$report | Select-Object passed,copyPassed,conversionPassed,rendererPassed,schedulingPassed,device,gpu,backend,frames,convertedCases,convertedPixels,elapsedSeconds | ConvertTo-Json
if ($report.error) { Write-Output (($report.error -split "`n")[0]) }
Write-Output "Reports: $runOutput"
if (-not $report.passed) { throw 'Android GPU atlas verification failed. Inspect the phase reports and player log.' }
