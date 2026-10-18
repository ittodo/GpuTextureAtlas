param([string]$UnityCli='C:\Program Files\Unity Hub\resources\unity.exe')
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project=Join-Path $root 'compute'
$temporary=Join-Path $project 'Assets/Bc7/VerificationTemp'
function Invoke-Code([string]$code){
    $text=& $UnityCli command eval $code 600000 --timeout 900 --project-path $project --json --result-only
    $exitCode=$LASTEXITCODE;$result=($text -join "`n")|ConvertFrom-Json
    if($exitCode -ne 0 -or -not $result.success){throw ($text -join "`n")}
    return $result.result
}
New-Item -ItemType Directory -Path $temporary -Force|Out-Null
Copy-Item -LiteralPath (Join-Path $root 'Verification/Bc7GpuPlayerProbe.cs') -Destination (Join-Path $temporary 'Bc7GpuPlayerProbe.cs') -Force
try{
    $compile=& $UnityCli recompile --project-path $project --timeout 120 --json
    $result=($compile -join "`n")|ConvertFrom-Json
    if($LASTEXITCODE -ne 0 -or -not $result.success -or $result.data.compilationFailed){throw ($compile -join "`n")}
    # A ready status selects the new Pipeline endpoint after a domain reload.
    & $UnityCli status --project-path $project --json|Out-Null
    $exe=Invoke-Code 'return UnityCompute.Bc7.Editor.Bc7GpuPlayerBuild.Build();'
    $report=Join-Path $root '.validation/bc7/player-report.json'
    $log=Join-Path $root '.validation/bc7/player.log'
    if(Test-Path -LiteralPath $report){Remove-Item -LiteralPath $report}
    $player=Start-Process -FilePath $exe -ArgumentList @('-force-d3d12','-screen-fullscreen','0','-screen-width','640','-screen-height','360','-bc7-probe-report',('"'+$report+'"'),'-logFile',('"'+$log+'"')) -WindowStyle Hidden -PassThru
    if(-not $player.WaitForExit(120000)){$player.Kill();throw 'BC7 verification player timed out.'}
    if(-not (Test-Path -LiteralPath $report)){throw 'Player did not write a verification report.'}
    $result=Get-Content -LiteralPath $report -Raw|ConvertFrom-Json
    if(-not $result.passed -or $player.ExitCode -ne 0){throw ($result|ConvertTo-Json -Depth 5)}
    if($result.slicedJobs -ne 4 -or $result.copies -ne 216 -or $result.slicedBeginAllocatedBytes -ne 0 -or $result.slicedStepAllocatedBytes -ne 0){throw 'Player sliced-job verification is incomplete.'}
    Write-Output "PASS: D3D12 Player, $($result.frames) full frames + $($result.slicedFrames) sliced frames / $($result.slicedJobs) jobs, $($result.copies) copies, $($result.comparedPixels) sampled pixels, 0 reused Begin/Step bytes, resources released."
}finally{
    # Remove only this tool's known temporary assets. Keep unrelated files in the folder.
    Invoke-Code 'UnityEditor.AssetDatabase.DeleteAsset("Assets/Bc7/VerificationTemp/Probe.unity"); UnityEditor.AssetDatabase.DeleteAsset("Assets/Bc7/VerificationTemp/Bc7GpuPlayerProbe.cs"); return "Temporary verification assets removed.";'|Out-Null
}
