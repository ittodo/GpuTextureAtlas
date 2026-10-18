param([string]$UnityCli='C:\Program Files\Unity Hub\resources\unity.exe')
$ErrorActionPreference='Stop'
$project=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\compute'))
function Invoke-Code([string]$code){
    $text=& $UnityCli command eval $code 600000 --timeout 900 --project-path $project --json --result-only
    $exitCode=$LASTEXITCODE;$result=($text -join "`n") | ConvertFrom-Json
    if($exitCode -ne 0 -or -not $result.success){throw ($text -join "`n")}
    return $result.result
}
Write-Output (Invoke-Code 'return UnityCompute.Bc7.Editor.Bc7Validation.Run();')
Write-Output (Invoke-Code 'return UnityCompute.Bc7.Editor.Bc7PartitionValidation.Run();')
Write-Output (Invoke-Code 'UnityCompute.Bc7.Editor.Bc7Validation.RunAsync(); return UnityCompute.Bc7.Editor.Bc7Validation.AsyncStatus;')
$deadline=[DateTime]::UtcNow.AddSeconds(40)
do{Start-Sleep -Milliseconds 500;$status=Invoke-Code 'return UnityCompute.Bc7.Editor.Bc7Validation.AsyncStatus;'}while($status -eq 'running' -and [DateTime]::UtcNow -lt $deadline)
if($status -notlike 'PASS:*'){throw $status}
Write-Output $status
Write-Output (Invoke-Code 'return UnityCompute.Bc7.Editor.Bc7ShaderValidation.Run();')
