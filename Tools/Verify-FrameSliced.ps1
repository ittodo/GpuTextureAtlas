param([string]$UnityCli='C:\Program Files\Unity Hub\resources\unity.exe',[switch]$SkipPlatformCompilers)
$ErrorActionPreference='Stop'
$project=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../compute'))
function Invoke-Code([string]$code){
    $raw=& $UnityCli command eval $code 600000 --timeout 900 --project-path $project --json --result-only
    $codeExit=$LASTEXITCODE;$result=($raw -join "`n")|ConvertFrom-Json
    if($codeExit -ne 0 -or -not $result.success){throw ($raw -join "`n")};return $result.result
}
& $UnityCli status --until-ready --timeout 60 --project-path $project --json|Out-Null
Invoke-Code 'return UnityCompute.TextureCompression.Editor.FrameSlicedValidation.RunGpuRows();'
Invoke-Code 'UnityCompute.TextureCompression.Editor.FrameSlicedValidation.RunStreamingAsync();return UnityCompute.TextureCompression.Editor.FrameSlicedValidation.Status;'
$deadline=[DateTime]::UtcNow.AddMinutes(3)
do{Start-Sleep -Seconds 1;$status=Invoke-Code 'return UnityCompute.TextureCompression.Editor.FrameSlicedValidation.Status;'}while($status -eq 'running' -and [DateTime]::UtcNow -lt $deadline)
if($status -notlike 'PASS:*'){throw $status};$status
if(-not $SkipPlatformCompilers){
    Invoke-Code 'return UnityCompute.Astc.Editor.AstcValidation.RunShaderCompilers();'
    Invoke-Code 'return UnityCompute.Bc7.Editor.Bc7ShaderValidation.Run();'
}
