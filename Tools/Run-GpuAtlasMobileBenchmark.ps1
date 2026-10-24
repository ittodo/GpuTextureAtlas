param([string]$Serial = '', [string]$Adb = '', [ValidateRange(1,10)][int]$Repetitions = 3,
    [ValidateRange(0,3600)][int]$StressSeconds = 600, [switch]$SkipMeasurements, [int]$TimeoutSeconds = 7200,
    [ValidateSet('','Fast','Balanced','HighQuality')][string]$OnlyQuality = '',
    [ValidateSet('','alpha','opaque')][string]$OnlyGroup = '',
    [ValidateSet('','immediate','frame-budget')][string]$OnlyScheduling = '',
    [ValidateSet(0,256,512,1024)][int]$Resolution = 0, [int]$InputLimit = 0, [int]$EncodeBlocks = 128,
    [int]$HighQualityEncodeBlocks = 16, [switch]$IncludeHighQualityImmediate, [int]$MaxImmediateResolution = 256,
    [string]$ResumeRun = '')
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $Adb) { $Adb = Join-Path $root '.tooling/Unity/6000.7.0b5/Editor/Data/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb.exe' }
$output = Join-Path $root '.validation/gpu-atlas/mobile'
$apk = Join-Path $output 'GpuAtlasBenchmark.apk'
$package = 'com.ittodo.gputextureatlas.benchmark'
$files = "/sdcard/Android/data/$package/files"
$remote = "$files/mobile-benchmark"
if ($ResumeRun) {
    $runOutput = [IO.Path]::GetFullPath($ResumeRun)
    if ([IO.Path]::GetDirectoryName($runOutput) -ne $output -or [IO.Path]::GetFileName($runOutput) -notmatch '^device-\d{8}-\d{6}$') {
        throw 'ResumeRun must be a device run directory created by this tool.'
    }
    $monitor = Get-Content -LiteralPath (Join-Path $runOutput 'monitor.json') -Raw | ConvertFrom-Json
    if ($Serial -and $Serial -ne $monitor.serial) { throw 'ResumeRun belongs to a different device.' }
    $Serial = $monitor.serial
    $originalRequest = Get-Content -LiteralPath (Join-Path $runOutput 'request.json') -Raw | ConvertFrom-Json
    $StressSeconds = $originalRequest.stressSeconds
} elseif (-not (Test-Path -LiteralPath $apk)) { throw 'Build the benchmark APK first.' }
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
if (-not $ResumeRun) {
    Invoke-Adb @('install','-r',$apk) | Out-Null
    $activity = (Invoke-Adb @('shell','cmd','package','resolve-activity','--brief',$package) | Where-Object { $_ -match '^[\w.]+/[\w.]+' } | Select-Object -Last 1).Trim()
    if (-not $activity) { throw 'Cannot resolve benchmark activity.' }
    $runOutput = Join-Path $output ('device-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
    New-Item -ItemType Directory -Path $runOutput -Force | Out-Null
    $buildInfo = if (Test-Path -LiteralPath (Join-Path $output 'build-info.json')) {
        Get-Content -LiteralPath (Join-Path $output 'build-info.json') -Raw | ConvertFrom-Json
    } else { [pscustomobject]@{} }
    $buildInfo | Add-Member -NotePropertyName apkSha256 -NotePropertyValue (Get-FileHash -LiteralPath $apk -Algorithm SHA256).Hash.ToLowerInvariant() -Force
    $inputManifestPath = Join-Path $runOutput 'input-manifest.json'
    $archive = [IO.Compression.ZipFile]::OpenRead($apk)
    try {
        $entry = $archive.GetEntry('assets/benchmark-manifest.json')
        if ($null -eq $entry) { throw 'APK input manifest is missing.' }
        $source = $entry.Open(); $destination = [IO.File]::Create($inputManifestPath)
        try { $source.CopyTo($destination) } finally { $source.Dispose(); $destination.Dispose() }
    } finally { $archive.Dispose() }
    $buildInfo | Add-Member -NotePropertyName inputManifestSha256 -NotePropertyValue (Get-FileHash -LiteralPath $inputManifestPath -Algorithm SHA256).Hash.ToLowerInvariant() -Force
    $buildInfo | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $runOutput 'build-info.json') -Encoding utf8NoBOM
    Invoke-Adb @('shell','am','force-stop',$package) | Out-Null
    Invoke-Adb @('shell','mkdir','-p',$remote) | Out-Null
    # Remove only reports written by this dedicated benchmark app.
    Invoke-Adb @('shell','rm','-f',"$remote/result.json","$remote/progress.json","$remote/measurements.json","$remote/stress.json") | Out-Null
    $requestPath = Join-Path $runOutput 'request.json'
    @{ repetitions=$Repetitions; stressSeconds=$StressSeconds; measure=(-not $SkipMeasurements); stress=($StressSeconds -gt 0)
        onlyQuality=$OnlyQuality; onlyGroup=$OnlyGroup; onlyScheduling=$OnlyScheduling; resolution=$Resolution; inputLimit=$InputLimit; encodeBlocks=$EncodeBlocks
        highQualityEncodeBlocks=$HighQualityEncodeBlocks; skipHighQualityImmediate=(-not $IncludeHighQualityImmediate); maxImmediateResolution=$MaxImmediateResolution } | ConvertTo-Json | Set-Content -LiteralPath $requestPath -Encoding utf8NoBOM
    Invoke-Adb @('push',$requestPath,"$files/benchmark-request.json") | Out-Null
    Invoke-Adb @('shell','input','keyevent','KEYCODE_WAKEUP') | Out-Null
    Invoke-Adb @('shell','am','start','-W','-n',$activity) | Out-Null
    $testProcessId = (Invoke-Adb @('shell','pidof',$package) -join '').Trim()
    $monitor = [pscustomobject]@{ runId = [guid]::NewGuid().ToString(); serial = $Serial; processId = $testProcessId; backgroundChecked = $false }
    $monitorPath = Join-Path $runOutput 'monitor.json'
    $monitor | ConvertTo-Json | Set-Content -LiteralPath $monitorPath -Encoding utf8NoBOM
    Invoke-Adb @('push',$monitorPath,"$files/benchmark-monitor.json") | Out-Null
} else {
    $activity = (Invoke-Adb @('shell','cmd','package','resolve-activity','--brief',$package) | Where-Object { $_ -match '^[\w.]+/[\w.]+' } | Select-Object -Last 1).Trim()
    if (-not $activity) { throw 'Cannot resolve the installed benchmark activity.' }
    $testProcessId = [string]$monitor.processId
    if ($testProcessId -notmatch '^\d+$') { throw 'ResumeRun does not contain a valid process ID.' }
    $remoteMonitor = ((Invoke-Adb @('shell','cat',"$files/benchmark-monitor.json")) -join "`n") | ConvertFrom-Json
    if (-not $monitor.runId -or $remoteMonitor.runId -ne $monitor.runId) { throw 'The device now contains a different benchmark run.' }
    Write-Output "Monitoring existing run: $runOutput"
}
$deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
$lastPhase = ''; $complete = $false; $backgroundChecked = [bool]$monitor.backgroundChecked
try {
    while ([DateTime]::UtcNow -lt $deadline) {
        # Only test file existence here. Large result JSON is pulled once after completion.
        $done = Invoke-Adb @('shell',"if [ -f '$remote/result.json' ]; then echo complete; fi")
        if ($done -contains 'complete') { $complete = $true; break }
        $alive = (& $Adb -s $Serial shell pidof $package 2>$null) -join ''
        if ($alive.Trim() -ne $testProcessId) { throw 'Benchmark process exited before writing its result. Inspect player.log.' }
        $progressRaw = Invoke-Adb @('shell',"if [ -f '$remote/progress.json' ]; then cat '$remote/progress.json'; fi")
        if ($progressRaw) {
            $progress = ($progressRaw -join "`n") | ConvertFrom-Json
            if ($progress.phase -ne $lastPhase) { $lastPhase = $progress.phase; Write-Output "$lastPhase ($($progress.results) results)" }
            if (-not $backgroundChecked -and $progress.phase -like 'Stress*') {
                Start-Sleep -Seconds 10
                Invoke-Adb @('shell','input','keyevent','KEYCODE_HOME') | Out-Null
                Start-Sleep -Seconds 5
                Invoke-Adb @('shell','am','start','-W','-n',$activity) | Out-Null
                $backgroundChecked = $true
                $monitor.backgroundChecked = $true
                $monitor | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runOutput 'monitor.json') -Encoding utf8NoBOM
                Write-Output 'Background/resume exercised.'
            }
        }
        Start-Sleep -Seconds 5
    }
} finally {
    & $Adb -s $Serial pull $remote $runOutput | Out-Null
    if ($testProcessId -match '^\d+$') { & $Adb -s $Serial logcat -d --pid=$testProcessId -v threadtime | Set-Content -LiteralPath (Join-Path $runOutput 'player.log') -Encoding utf8 }
}
if (-not $complete) { throw "Device benchmark timed out. Reports and the device process were retained. Continue monitoring with -ResumeRun '$runOutput'." }
$report = Get-Content -LiteralPath (Join-Path $runOutput 'mobile-benchmark/result.json') -Raw | ConvertFrom-Json
$report | Select-Object passed,device,gpu,frameTimingEnabled,pauses,resumes,stressEdits,elapsedSeconds,@{n='measurements';e={$_.results.Count}} | ConvertTo-Json
if ($report.error) { Write-Output (($report.error -split "`n")[0]) }
Write-Output "Reports: $runOutput"
if (-not $report.passed) { throw 'Android benchmark failed. Inspect the report and player log.' }
if ($StressSeconds -gt 30 -and ($report.pauses -lt 1 -or $report.resumes -lt 1)) { throw 'Background/resume was not observed by the benchmark app.' }
