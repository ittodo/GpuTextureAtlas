import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import {fileURLToPath} from 'node:url';
const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'..');
const read=p=>JSON.parse(fs.readFileSync(path.join(root,p),'utf8').replace(/^\uFEFF/,''));
const run=read('Benchmark/Results/frame-sliced/measurements.json');
if(run.rows.length!==36||new Set(run.rows.map(r=>[r.texture,r.format,r.blockSize,r.quality,r.rowsPerBatch].join('/'))).size!==36)throw Error('Incomplete timing matrix.');
for(const row of run.rows)if(![4,32].includes(row.rowsPerBatch)||row.batches!==Math.ceil(Math.ceil(row.height/row.blockSize)/row.rowsPerBatch)||![row.fullMs,row.meanBatchMs,row.p95BatchMs,row.maxBatchMs].every(v=>Number.isFinite(v)&&v>0)||row.meanBatchMs>row.maxBatchMs||row.p95BatchMs>row.maxBatchMs)throw Error('Invalid batch timing.');
const hash=p=>crypto.createHash('sha256').update(fs.readFileSync(path.join(root,p))).digest('hex').toUpperCase();
if(run.astcSha256!==hash('Packages/com.ittodo.gpu-texture-atlas/Shaders/Astc/AstcEncoder.compute')||run.bc7Sha256!==hash('Packages/com.ittodo.gpu-texture-atlas/Shaders/Bc7/Bc7Encoder.compute'))throw Error('Current timing source differs.');
const columns=Object.keys(run.rows[0]),csv=values=>values.map(v=>'"'+String(v).replaceAll('"','""')+'"').join(',');
fs.writeFileSync(path.join(root,'Benchmark/Results/frame-sliced/measurements.csv'),'\uFEFF'+[csv(columns),...run.rows.map(r=>csv(columns.map(k=>r[k])))].join('\n'));
const data=JSON.stringify(run).replaceAll('<','\\u003c');
const html=`<!doctype html><html lang="ko"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>프레임 분산 GPU 압축</title>
<style>:root{color-scheme:dark;font-family:system-ui,'Malgun Gothic',sans-serif;background:#10151c;color:#dce4ee}body{max-width:1300px;margin:auto;padding:28px}h1{font-size:30px}p,li{line-height:1.8}a{color:#91caff}pre,.controls{padding:16px;background:#1b2634;border-radius:6px}pre{overflow:auto}select{background:#111923;color:inherit;padding:8px;border:1px solid #52657b;border-radius:4px}.controls{display:flex;gap:16px;flex-wrap:wrap}label{display:grid;gap:6px}.notice{border-left:4px solid #efbf68;padding:12px;background:#30291c}.wrap{overflow:auto}table{border-collapse:collapse;width:100%;font-variant-numeric:tabular-nums}th,td{padding:12px;border-bottom:1px solid #344354;text-align:right;white-space:nowrap}th:first-child,td:first-child{text-align:left}.small{font-size:13px;color:#afbdcd}.good{color:#8addaf}</style>
<body><p><a href="../report.html">기존 비교</a> · <a href="../high-quality/report.html">HighQuality 품질 비교</a></p><h1>프레임 분산 GPU 압축</h1><p id="hardware"></p>
<p>ASTC와 BC7은 프레임마다 일부 블록 행만 압축할 수 있습니다. 앞선 묶음의 GPU fence가 남아 있으면 다음 제출을 미룹니다. 전체 행을 제출한 뒤 텍스처로 한 번 복사합니다. 처리 중에는 이전 완성 텍스처를 계속 표시합니다.</p>
<p class="notice">분산하면 한 번에 제출하는 GPU 작업은 작아지고, 결과가 갱신되는 간격은 길어집니다. 행 수는 엄격한 ms 제한이 아닙니다. 입력 스냅샷과 마지막 텍스처 복사도 GPU 작업입니다. 여러 객체의 예산은 각각 적용됩니다.</p>
<h2>일괄 압축과 행 묶음 비교</h2><p>1024×1024 알파 UI와 비알파 사진 각 1장, 총 2개 대표 입력입니다. 기존 60개 품질 표본 전체를 다시 측정한 결과가 아닙니다. 모든 행 묶음을 측정해 복잡한 부분의 최대 비용도 포함했습니다.</p>
<div class="controls"><label>입력<select id="texture"><option value="ui_alpha_atlas">알파 UI</option><option value="wood_floor_worn">비알파 사진</option></select></label><label>형식<select id="format"><option value="ASTC/4">ASTC 4×4</option><option value="ASTC/6">ASTC 6×6</option><option value="BC7/4">BC7 4×4</option></select></label></div>
<div class="wrap"><table><thead><tr><th>품질</th><th>블록 행/제출</th><th>최소 제출 횟수</th><th>일괄 ms</th><th>묶음 평균 ms</th><th>묶음 P95 ms</th><th>묶음 최대 ms</th></tr></thead><tbody id="rows"></tbody></table></div><p id="gain" class="good"></p>
<p class="small">각 품질은 최소 200ms 사전 실행하고, 각 행 범위도 2회 준비합니다. 같은 GPU 버퍼에 4회 제출한 뒤 한 번 readback으로 완료를 기다립니다. 이를 3묶음 측정한 중앙값을 사용합니다. readback 비용 일부가 포함된 처리량이며 GPU timestamp나 실제 프레임 지연이 아닙니다. 스냅샷·최종 네이티브 텍스처 복사·렌더링은 이 시간에서 제외했습니다. 최소 제출 횟수에는 GPU 대기와 스냅샷·완료 확인 프레임을 더해야 합니다. 모바일 기기 시간은 측정하지 않았습니다.</p>
<h2>사용 방법</h2><p>기존 AstcGpuStream·Bc7GpuStream에서 Spread Across Frames를 켜고 Block Rows Per Frame을 지정합니다. 기본값은 4행입니다. Snapshot Input은 기본으로 켭니다. 1024의 4×4 형식은 4행씩 최소 64번, 32행씩 최소 8번 제출합니다.</p>
<pre>// output은 AstcGpuTexture 또는 Bc7GpuTexture입니다.
// Update에서 한 번 호출합니다.
if (!output.IsEncoding)
    output.BeginEncode(source, quality: Bc7Quality.HighQuality);
bool completed = output.EncodeNextFrame(maxBlockRows: 4);
float progress = output.EncodingProgress;</pre>
<p>ASTC는 AstcQuality를 사용합니다. 입력은 시작할 때 같은 형식의 GPU RenderTexture에 복사합니다. 원본 값이 이후 바뀌어도 이번 결과는 캡처한 입력으로 완성합니다. 원본 객체는 완료까지 유지합니다. snapshotInput:false이면 입력 값을 완료까지 변경하지 않습니다. RGBA8 1024 스냅샷은 약 4MiB를 추가로 사용합니다. 재사용 BeginEncode·EncodeNextFrame에는 CPU readback과 Apply가 없습니다.</p>
<p>작업 중 quality·색 공간 설정은 시작값을 유지합니다. 완료 전 새 BeginEncode·EncodeFrame 호출은 거부합니다. 중단할 때는 마지막 소비자의 그리기 제출 후 Dispose합니다. 입력 스냅샷과 출력 자원은 GPU fence 뒤에 해제합니다. ASTC의 모바일 직접 복사는 Android Vulkan·iOS Metal에서 실기 검증이 남아 있습니다.</p>
<p><a href="measurements.json">원시 JSON</a> · <a href="measurements.csv">36행 CSV</a> · <a href="../../../README.md">프로젝트 사용법</a></p><details><summary>측정 조건</summary><pre id="metadata"></pre></details>
<script>const data=${data};const $=id=>document.getElementById(id);$('hardware').textContent=data.gpu+' · Unity '+data.unity+' · '+data.api+' · '+data.utc;$('metadata').textContent=JSON.stringify({...data,rows:undefined},null,2);function render(){const [format,block]=$('format').value.split('/');const rs=data.rows.filter(r=>r.texture===$('texture').value&&r.format===format&&r.blockSize===+block);$('rows').innerHTML=rs.map(r=>'<tr><td>'+r.quality+'</td><td>'+r.rowsPerBatch+'</td><td>'+r.batches+'</td>'+['fullMs','meanBatchMs','p95BatchMs','maxBatchMs'].map(k=>'<td>'+r[k].toFixed(3)+'</td>').join('')+'</tr>').join('');const h=rs.find(r=>r.quality==='HighQuality'&&r.rowsPerBatch===4);$('gain').textContent='HighQuality 4행: 일괄 '+h.fullMs.toFixed(3)+' ms → 묶음 최대 '+h.maxBatchMs.toFixed(3)+' ms, 최소 '+h.batches+'번 제출';}for(const id of ['texture','format'])$(id).onchange=render;render();</script></body></html>`;
fs.writeFileSync(path.join(root,'Benchmark/Results/frame-sliced/report.html'),html);
console.log('Verified 36 full-vs-band timing rows. Frame-sliced report written.');
