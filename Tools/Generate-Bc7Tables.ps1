param([string]$ReferenceRoot=(Join-Path $PSScriptRoot '../.validation/reference/DirectXTex'))
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$source=Get-Content -LiteralPath (Join-Path $ReferenceRoot 'BC6HBC7.cpp') -Raw
$revision=(Get-Content -LiteralPath (Join-Path $ReferenceRoot 'revision.txt') -Raw).Trim()
$partition=[regex]::Match($source,'g_aPartitionTable\[3\]\[64\]\[16\].*?=\s*\{(.*?)\n\s*\};','Singleline').Groups[1].Value
$fixup=[regex]::Match($source,'g_aFixUp\[3\]\[64\]\[3\].*?=\s*\{(.*?)\n\s*\};','Singleline').Groups[1].Value
$rows=[regex]::Matches(($partition -replace '//[^\r\n]*',''),'\{\s*([012](?:\s*,\s*[012]){15})\s*\}')
$anchors=[regex]::Matches(($fixup -replace '//[^\r\n]*',''),'\{\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*\}')
if($rows.Count -ne 192 -or $anchors.Count -ne 192){throw 'Unexpected Microsoft BC7 table layout.'}
$masks=@();$indices=@()
for($i=0;$i -lt 64;$i++){
    $values=$rows[64+$i].Groups[1].Value -split '\s*,\s*';[uint32]$mask=0
    for($j=0;$j -lt 16;$j++){$mask=$mask -bor ([uint32]$values[$j] -shl $j)}
    $anchor=[int]$anchors[64+$i].Groups[2].Value
    if(($mask -band 1) -ne 0 -or (($mask -shr $anchor) -band 1) -ne 1){throw 'Invalid BC7 subset anchor.'}
    $masks+=$mask;$indices+=$anchor
}
$header="// BC7 specification tables from Microsoft DirectXTex, revision $revision.`n// Copyright (c) Microsoft Corporation. MIT License: ThirdParty/DirectXTex/LICENSE.txt.`n"
$hlsl=$header+'static const uint Bc7Partitions[64] = {'+($masks -join ',')+"};`n"+'static const uint Bc7Anchors[64] = {'+($indices -join ',')+"};`n"
$cs=$header+"namespace UnityCompute.Bc7.Editor { internal static class Bc7Partitions {`n"+'internal static readonly int[] Masks = {'+($masks -join ',')+"};`n"+'internal static readonly int[] Anchors = {'+($indices -join ',')+"};`n} }`n"
[IO.File]::WriteAllText((Join-Path $root 'Packages/com.ittodo.gpu-texture-atlas/Shaders/Bc7/Bc7Partitions.hlsl'),$hlsl)
[IO.File]::WriteAllText((Join-Path $root 'compute/Assets/Bc7/Editor/Bc7Partitions.cs'),$cs)
@{repository='https://github.com/microsoft/DirectXTex';revision=$revision;file='DirectXTex/BC6HBC7.cpp';sha256=(Get-FileHash -LiteralPath (Join-Path $ReferenceRoot 'BC6HBC7.cpp')).Hash;license='MIT'}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $root 'ThirdParty/DirectXTex/source.json') -Encoding utf8
'Generated 64 BC7 two-subset masks and anchors.'
