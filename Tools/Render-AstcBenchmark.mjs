import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const read = p => JSON.parse(fs.readFileSync(path.join(root, p), 'utf8').replace(/^\uFEFF/, ''));
const astcRuns = ['baseline', 'fast', 'balanced'].map(name => ({ name, ...read(`Benchmark/Results/${name}/measurements.json`) }));
const hasBc7=fs.existsSync(path.join(root,'Benchmark/Results/bc7/measurements.json'));
const hasHighQuality=fs.existsSync(path.join(root,'Benchmark/Results/high-quality/report.html'));
const hasFrameSliced=fs.existsSync(path.join(root,'Benchmark/Results/frame-sliced/report.html'));
const runs=hasBc7?[...astcRuns,{name:'bc7',...read('Benchmark/Results/bc7/measurements.json')}]:astcRuns;
const inputs = read('Benchmark/Input/manifest.json');
const sources = read('Benchmark/Sources/sources.json');
const rows = runs.flatMap(run => run.rows);
const hash = p => crypto.createHash('sha256').update(fs.readFileSync(p)).digest('hex').toUpperCase();
const manifestHash = hash(path.join(root, 'Benchmark/Input/manifest.json'));
if (inputs.length !== 60 || astcRuns.flatMap(r=>r.rows).length !== 720 || rows.length !== (hasBc7?900:720)) throw Error(`Incomplete matrix: ${inputs.length} inputs, ${rows.length} rows`);
for (const run of runs) if (run.inputManifestSha256 !== manifestHash) throw Error(`Input manifest changed: ${run.name}`);
for (const input of inputs) {
  const file = path.join(root, `Benchmark/Input/${input.resolution}/${input.texture}.png`);
  if (hash(file) !== input.sha256) throw Error(`Input changed: ${file}`);
  const matches = rows.filter(r => r.texture === input.texture && r.width === input.resolution);
  const expected=hasBc7?15:12;
  if (matches.length !== expected || new Set(matches.map(r => `${r.encoder}/${r.size}`)).size !== expected) throw Error(`Incomplete or duplicate input: ${input.texture}/${input.resolution}`);
  if (matches.some(r => r.group !== input.group)) throw Error(`Group differs: ${input.texture}`);
  for (const row of matches) for (const signal of ['rgb','alpha','composite']) {
    const p = row.peak?.[signal];
    if (!p || Object.values(p).some(v=>!Number.isFinite(v)) || p.p99Error < 0 || p.maxError > 255 || p.p99Error > p.maxError ||
        p.maxX < 0 || p.maxX >= row.width || p.maxY < 0 || p.maxY >= row.height ||
        p.worstBlockX < 0 || p.worstBlockY < 0 || p.worstBlockWidth < 1 || p.worstBlockHeight < 1 ||
        p.worstBlockWidth > row.size || p.worstBlockHeight > row.size ||
        p.worstBlockX + p.worstBlockWidth > row.width || p.worstBlockY + p.worstBlockHeight > row.height ||
        p.worstBlockPsnr < 0 || p.worstBlockPsnr > row[signal+'Psnr'] + 1e-8)
      throw Error(`Invalid or missing peak metrics: ${row.texture}/${row.encoder}/${row.size}/${signal}. Run Update-AstcPeakMetrics.ps1.`);
  }
}
const isBc7=e=>e.startsWith('bc7-')||e==='unity-bc7';
const encoders = ['baseline', 'fast', 'balanced', 'astcenc-fastest', 'astcenc-fast', 'astcenc-medium',...(hasBc7?['bc7-fast','bc7-balanced','unity-bc7']:[])];
const mean = (rs, key) => rs.reduce((a, r) => a + r[key], 0) / rs.length;
const aggregate = [];
for (const group of ['opaque', 'alpha']) for (const resolution of [256, 512, 1024]) for (const size of [4, 6]) for (const encoder of encoders) {
  if(size!==4&&isBc7(encoder))continue;
  const rs = rows.filter(r => r.group === group && r.width === resolution && r.size === size && r.encoder === encoder);
  if (rs.length !== 10 || new Set(rs.map(r => r.texture)).size !== 10) throw Error('Each group must contain 10 distinct inputs');
  const a = { group, resolution, size, encoder, images: rs.length };
  for (const key of ['medianMs','p95Ms','codecMedianMs','bufferReuseBatchMedianMs','submissionMedianMs','rgbPsnr','alphaPsnr','compositePsnr','ssim','payloadBytes']) a[key] = mean(rs,key);
  // Equal-size images: combine MSE, then convert to PSNR. Treat the exact sentinel as zero MSE.
  for (const key of ['rgbPsnr','alphaPsnr','compositePsnr']) {
    const mse = rs.reduce((sum,r) => sum + (r[key] >= 119.999 ? 0 : 10 ** (-r[key]/10)),0)/rs.length;
    a[key] = mse === 0 ? 120 : -10*Math.log10(mse);
  }
  a.peak = {};
  for (const signal of ['rgb','alpha','composite']) {
    const worst = rs.reduce((a,b)=>a.peak[signal].worstBlockPsnr <= b.peak[signal].worstBlockPsnr ? a : b);
    const maximum = rs.reduce((a,b)=>a.peak[signal].maxError >= b.peak[signal].maxError ? a : b);
    a.peak[signal] = {maxError:maximum.peak[signal].maxError, meanP99Error:rs.reduce((sum,r)=>sum+r.peak[signal].p99Error,0)/rs.length,
      worstBlockPsnr:worst.peak[signal].worstBlockPsnr, worstTexture:worst.texture,
      worstBlockX:worst.peak[signal].worstBlockX, worstBlockY:worst.peak[signal].worstBlockY,
      maxTexture:maximum.texture, maxX:maximum.peak[signal].maxX, maxY:maximum.peak[signal].maxY};
  }
  aggregate.push(a);
}
fs.writeFileSync(path.join(root, 'Benchmark/Results/aggregate.json'), JSON.stringify(aggregate,null,2));
const flatten = (obj,prefix='') => Object.fromEntries(Object.entries(obj).flatMap(([k,v])=>v && typeof v==='object'?Object.entries(flatten(v,prefix+k+'.')):[[prefix+k,v]]));
const flatRows=rows.map(r=>flatten(r)),columns=Object.keys(flatRows[0]);
const csvCell=v=>'"'+String(v??'').replaceAll('"','""')+'"';
fs.writeFileSync(path.join(root,'Benchmark/Results/measurements.csv'), '\uFEFF'+columns.map(csvCell).join(',')+'\n'+flatRows.map(r=>columns.map(c=>csvCell(r[c])).join(',')).join('\n'));
const validation = ['report.json','report-fast.json'].map(p => read(`.validation/${p}`));
if (validation.some(r => !r.passed)) throw Error('Decoder validation did not pass');
const bc7Validation=hasBc7?read('.validation/bc7/report.json'):null;
if(hasBc7&&(!bc7Validation.passed||bc7Validation.maxDecoderDifference!==0))throw Error('BC7 hardware validation did not pass');
const bc7Modes=hasBc7?read('Benchmark/Results/bc7/modes.json'):null;
const bc7Conditions=hasBc7&&fs.existsSync(path.join(root,'.validation/bc7/conditions.json'))?read('.validation/bc7/conditions.json'):null;
const hasBc7Texture=hasBc7&&fs.existsSync(path.join(root,'Benchmark/Results/bc7/gpu-texture-measurements.json'));
const bc7Texture=hasBc7Texture?read('Benchmark/Results/bc7/gpu-texture-measurements.json'):null;
const bc7TextureValidation=hasBc7Texture?read('.validation/bc7/gpu-texture-validation.json'):null;
const bc7PlayerValidation=hasBc7Texture&&fs.existsSync(path.join(root,'.validation/bc7/player-report.json'))?read('.validation/bc7/player-report.json'):null;
if(hasBc7Texture){
  if(bc7Texture.rows.length!==120||bc7Texture.inputManifestSha256!==manifestHash||!bc7TextureValidation.passed||bc7TextureValidation.maximumDifference!==0)throw Error('BC7 GPU texture measurements or validation incomplete');
  if(bc7PlayerValidation&&!bc7PlayerValidation.passed)throw Error('BC7 standalone player verification failed');
  for(const input of inputs)for(const encoder of ['bc7-fast','bc7-balanced']){
    const selected=bc7Texture.rows.filter(r=>r.texture===input.texture&&r.width===input.resolution&&r.encoder===encoder);
    if(selected.length!==1||selected[0].group!==input.group||selected[0].recordedCopies!==64||selected[0].pipelineBatchMedianMs<=0||selected[0].allocatedBytes!==0)throw Error('BC7 GPU texture row differs');
  }
}
if(hasBc7){
  if(bc7Modes.rows.length!==120)throw Error('Incomplete BC7 mode counts');
  for(const row of rows.filter(r=>r.encoder.startsWith('bc7-'))){
    const usage=bc7Modes.rows.filter(u=>u.texture===row.texture&&u.width===row.width&&u.encoder===row.encoder);
    if(usage.length!==1)throw Error('Missing or duplicate BC7 mode record');
    const u=usage[0];
    if(u.mode4+u.mode5+u.mode6!==Math.ceil(row.width/4)*Math.ceil(row.height/4)||
       u.rotations.reduce((a,b)=>a+b,0)!==u.mode4+u.mode5||u.selectors.reduce((a,b)=>a+b,0)!==u.mode4||
       (row.encoder==='bc7-fast'&&u.mode4!==0))throw Error('BC7 mode counts differ');
  }
}
const html = `<!doctype html><html lang="ko"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>ASTC · BC7 GPU 압축 비교</title><style>
:root{color-scheme:dark;font-family:system-ui,'Malgun Gothic',sans-serif;background:#10151c;color:#dce4ee}*{box-sizing:border-box}body{max-width:1500px;margin:auto;padding:32px}h1{font-size:32px;margin:8px 0}h2{margin-top:38px;color:#f3f7fc}h3{margin:10px 0}p,li{line-height:1.7}a{color:#91caff}small,.muted{color:#a7b6c9}.pill{display:inline-block;border:1px solid #49647a;border-radius:16px;padding:4px 12px;font-size:13px;margin:2px}.notice{border-left:4px solid #edb759;background:#28251c;padding:12px 18px}.controls{display:flex;gap:16px;flex-wrap:wrap;align-items:end;background:#1a2330;padding:16px;border-radius:8px;position:sticky;top:0;z-index:3}label{display:grid;gap:5px;font-size:13px}select,button,input{background:#10151c;color:inherit;border:1px solid #526579;border-radius:5px;padding:8px;font-size:14px}button{cursor:pointer}.tablewrap{overflow:auto}table{width:100%;border-collapse:collapse;font-size:14px;font-variant-numeric:tabular-nums}th,td{border-bottom:1px solid #304151;padding:11px;text-align:right;white-space:nowrap}th:first-child,td:first-child{text-align:left}th{color:#a7c8e8;background:#192432}tr.fast{background:#163129}tr:hover{background:#25394c}.grid{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:14px}.card{background:#1a2330;border:1px solid #344659;border-radius:7px;overflow:hidden}.card header{padding:12px}.viewport{height:350px;overflow:auto;background-color:#ddd;background-image:conic-gradient(#bbb 25%,#eee 0 50%,#bbb 0 75%,#eee 0);background-size:24px 24px}.viewport img{display:block;max-width:none}.white .viewport{background:#fff}.black .viewport,.alpha .viewport{background:#000}.alpha img{filter:brightness(0) invert(1)}.thumbs{display:grid;grid-template-columns:repeat(5,1fr);gap:12px}.thumbs img{width:100%;aspect-ratio:1;object-fit:contain;background:#ccc}.thumbs button{padding:0;overflow:hidden}.thumbs span{display:block;padding:8px;font-size:12px;word-break:break-all}.metrics{font-size:12px;color:#b9ccdf;min-height:35px}.ok{color:#8addaf}details{margin:16px 0}pre{overflow:auto;white-space:pre-wrap;line-height:1.5;font-size:13px}@media(max-width:900px){body{padding:14px}.grid{grid-template-columns:1fr 1fr}.thumbs{grid-template-columns:repeat(3,1fr)}}@media(max-width:560px){.grid{grid-template-columns:1fr}.controls{position:static}}
.card{display:flex;flex-direction:column}.card header{min-height:168px}.viewport{flex-shrink:0}.frame{position:relative}.blockmark{position:absolute;pointer-events:none;border:2px solid #ff5a74;box-shadow:0 0 0 1px #000;z-index:1}.pixelmark{position:absolute;pointer-events:none;width:8px;height:8px;margin:-4px;border:2px solid #55e7ff;border-radius:50%;background:#10151c;z-index:2}.card button{font-size:12px;padding:5px 8px;margin-top:6px}.metrics{line-height:1.6;min-height:58px}#focusNote{font-variant-numeric:tabular-nums}
</style><body>
<span class="pill">256 · 512 · 1024</span><span class="pill">해상도별 알파 10장 + 비알파 10장</span><span class="pill">ASTC 4×4 · 6×6${hasBc7?' / BC7 4×4':''}</span><span class="pill">${rows.length}개 비교 결과</span>
<h1>ASTC${hasBc7?' · BC7':''} GPU 품질·속도 비교</h1><p id="hardware"></p>
${hasFrameSliced?'<p class="notice"><a href="frame-sliced/report.html">여러 프레임에 나누는 GPU 압축 →</a> 프레임별 행 예산·스냅샷 사용법과 별도 처리량 측정입니다.</p>':''}
${hasHighQuality?'<p class="notice"><a href="high-quality/report.html">새 HighQuality 모드 비교 →</a> Unity 6.7에서 Fast·Balanced·HighQuality를 같은 조건으로 측정한 별도 보고서입니다. 아래 기존 6.6 측정값은 보존했습니다.</p>':''}
<p class="notice">이 수치는 Windows 노트북의 D3D12 실측입니다. BC7은 PC용 비교입니다. Android·iOS 기기 시간과 발열은 측정하지 않았습니다. GPU 버퍼 재사용 시간에는 압축 텍스처로의 복사가 포함되지 않습니다. ASTC 모바일 업로드 경로는 실험 기능입니다.</p>
<div class="controls">
<label>표본 그룹<select id="group"><option value="alpha">알파 RGBA · 10장</option><option value="opaque">비알파 RGB · 10장</option></select></label>
<label>해상도<select id="res"><option>256</option><option>512</option><option selected>1024</option></select></label>
<label>비교 블록<select id="block"><option value="4" selected>4×4${hasBc7?' · ASTC+BC7':''}</option><option value="6">6×6 · ASTC</option></select></label>
<label>확인할 이미지<select id="texture"></select></label>
<label>표시 배경<select id="background"><option value="checker">체커</option><option value="white">흰색</option><option value="black">검은색</option><option value="alpha">알파만 표시</option></select></label>
<label>오차 대상<select id="signal"><option value="composite">흰색·검은색 합성</option><option value="alpha">알파</option><option value="rgb">RGB</option></select></label>
<label><span>배율 <output id="zoomValue">50%</output></span><input id="zoom" type="range" min="25" max="800" step="25" value="50"></label><button id="one">1:1 픽셀</button>
</div>
<h2>그룹 평균</h2><p id="takeaway"></p><div class="tablewrap"><table><thead><tr><th>인코더</th><th>RGB PSNR ↑</th><th>Alpha PSNR ↑</th><th>합성 PSNR ↑</th><th>SSIM ↑</th><th>재사용 ms ↓</th><th>참조 코딩 ms ↓</th><th>동기 API/프로세스 ms ↓</th></tr></thead><tbody id="summary"></tbody></table></div>
<p class="muted">그룹 PSNR은 같은 크기 10장의 MSE를 합친 뒤 계산합니다. SSIM과 시간은 이미지별 수치의 산술 평균입니다. PSNR 단위는 dB입니다. ∞는 오차 0이며 JSON에서는 120으로 저장합니다. 비알파 입력의 알파는 모두 255입니다. 압축 후 알파 오차도 표시합니다. 각 인코더의 압축 크기는 같습니다.</p>
${hasBc7Texture?'<section id="bc7TextureSection"><h2>BC7 매 프레임 텍스처 갱신</h2><p>Windows x64 D3D12에서 GPU 압축 → GPU CopyTextureRegion → BC7 텍스처 샘플링을 측정했습니다. 원본·출력 버퍼·텍스처·명령 버퍼를 재사용합니다. EncodeFrame에는 CPU 픽셀 읽기와 Apply가 없습니다. 위 표의 재사용 시간은 압축 버퍼만 만드는 이전 측정이며, 아래는 텍스처 복사를 포함한 별도 측정입니다.</p><div class="tablewrap"><table><thead><tr><th>품질</th><th>압축·복사 처리량 ms ↓</th><th>CPU 제출 중앙값 ms ↓</th><th>CPU 제출 p95 평균 ms ↓</th><th>호출 중 관리 힙 할당</th></tr></thead><tbody id="bc7TextureSummary"></tbody></table></div><p class="muted">선택한 해상도·그룹의 각 10장 평균입니다. 준비 16회 뒤 16회 호출을 3묶음 측정했습니다. 묶음 끝의 1픽셀 GPU 샘플링·readback으로 완료를 기다린 시간을 16으로 나눈 중앙값입니다. 이 마지막 검산용 읽기 비용 일부도 포함합니다. GPU timestamp나 프레임 지연 수치가 아닙니다. CPU 제출 시간은 GPU 완료를 기다리지 않습니다. 원본 갱신·최초 자원 생성 비용은 제외했습니다. 기존 화질 결과와는 별도 시각의 측정입니다.</p><p id="bc7TextureVerification" class="ok"></p></section>':''}
<h2>피크 오차</h2><p>위의 오차 대상 선택을 적용합니다. 최대오차는 10장 전체의 최댓값입니다. P99 평균은 이미지별 P99의 산술 평균이며, 10장을 합친 P99가 아닙니다. 최악 블록은 10장의 실제 압축 블록 중 PSNR이 가장 낮은 블록입니다.</p>
<div class="tablewrap"><table><thead><tr><th>인코더</th><th>최대오차 ↓</th><th>이미지별 P99 평균 ↓</th><th>최악 블록 dB ↑</th><th>최악 블록 이미지</th><th>좌상단 (x, y)</th></tr></thead><tbody id="peakSummary"></tbody></table></div>
<h2>복원 이미지 비교</h2><p>ASTC PNG는 공식 astcenc, BC7 PNG는 GPU 하드웨어 디코더로 복원했습니다. 카드의 ‘최악 블록 확대’ 또는 ‘최대오차 픽셀’을 누르면 모든 패널에서 같은 원본 위치를 400% 이상으로 비교합니다. 빨간 테두리는 선택한 인코더의 최악 블록이고, 하늘색 점은 최대오차 픽셀입니다. 알파 표시는 흰색이 불투명, 검은색이 투명입니다.</p><p id="focusNote" class="muted"></p><div id="images" class="grid"></div>
<h2>이미지별 수치</h2><div class="tablewrap"><table><thead><tr><th>이미지</th><th>인코더</th><th>RGB dB</th><th>Alpha dB</th><th>합성 dB</th><th>SSIM</th><th>최대오차 ↓</th><th>P99 ↓</th><th>최악 블록 dB ↑</th><th>블록 (x, y)</th><th>최대오차 (x, y)</th><th>재사용 ms</th><th>참조 코딩 ms</th><th>동기/프로세스 ms</th><th>p95 ms</th></tr></thead><tbody id="individual"></tbody></table></div>
<h2>입력 표본</h2><p>비알파: Poly Haven CC0 재질 사진 9장과 불투명 UI 1장. 알파: Arm·Khronos 참조 RGBA 5장, 사진에 독립 알파 마스크를 적용한 4장, RGBA UI 1장. 마스크는 잎·부드러운 연기·그라데이션·가는 풀 형태입니다. AI 생성 이미지는 사용하지 않았습니다.</p><p>모든 입력은 같은 원본에서 중앙 정사각형을 잘라 bicubic으로 256·512·1024로 변환했습니다. Arm 원본 3장은 256×256입니다. 해당 입력의 큰 해상도 결과는 확대 처리 비용을 보여주며, 고해상도 디테일 품질의 근거로 사용하지 않습니다. 알파 유무는 실제 8비트 픽셀 값으로 확인했습니다.</p><div id="thumbs" class="thumbs"></div>
<h2>측정 방법과 한계</h2><ul>
<li>GPU 동기 API: ASTC 준비 3회, BC7 준비 16회, 측정은 각각 9회입니다. 이미지별 중앙값과 p95를 저장했습니다. 출력 할당·dispatch·동기 readback·바이트 복사가 포함됩니다. 9회 표본의 p95는 최댓값입니다.</li>
<li>GPU 재사용: 출력 버퍼를 유지하고 16회 dispatch 후 한 번 readback으로 완료를 기다렸습니다. 이를 3묶음 측정한 중앙값을 16으로 나눴습니다. BC7은 측정 전에도 16회 묶음으로 준비했습니다. GPU timestamp가 아니며 CPU 제출 비용과 마지막 readback 비용 일부가 포함됩니다. 프레임당 지연이나 모바일 처리 시간으로 해석하지 않습니다.</li>
<li>CPU: 공식 astcenc 5.7.0 AVX2, -cs -j 16, fastest·fast·medium. 준비 1회, 프로세스 실행 3회. 코딩 시간은 astcenc가 출력한 Coding time입니다. 프로세스 시간에는 시작·PNG I/O 비용도 포함됩니다. GPU 재사용과 CPU 코딩 시간은 측정 범위가 다릅니다.</li>
${hasBc7?'<li>BC7은 고정 4×4, 16바이트로 ASTC 4×4와 같은 8bpp입니다. GPU Fast는 모드 6과 알파 분리 모드 5를 비교합니다. Balanced는 모드 4·5·6, 채널 회전, 추가 끝점 보정을 탐색합니다. 모드 0·1·2·3·7과 다중 분할은 구현하지 않았습니다. 모든 GPU BC7 결과는 별도 스칼라 디코더와 하드웨어 복원을 픽셀 단위로 정확히 대조했습니다.</li><li>Unity BC7 Normal은 EditorUtility.CompressTexture 참조 결과입니다. 준비 1회·측정 3회이며 입력 복사·업로드를 제외한 압축 API 호출 시간을 참조 코딩 열과 동기 API 열에 표시합니다. 내부 백엔드와 스레드 수는 확인하지 않았습니다. astcenc 프로세스 시간 및 GPU 재사용과 범위가 다릅니다. ASTC와 BC7은 별도 시각에 측정했으며 클록을 고정하지 않았습니다.</li><li>BC7은 PC용 비교입니다. BC7 EncodeGpu는 압축 버퍼에만 씁니다. 위 그룹 평균의 재사용 시간에는 압축 텍스처 복사가 없습니다. CreateTexture 경로는 CPU readback 후 업로드합니다. Bc7GpuTexture는 Windows x64 D3D12에서 CPU 픽셀 읽기 없이 압축 버퍼를 BC7 텍스처로 복사합니다. 직접 복사의 처리 시간은 별도 표에 표시합니다. 다른 BC7 그래픽 API의 직접 복사는 구현하지 않았습니다.</li>':''}
<li>입력은 모든 인코더에서 동일한 canonical 8비트 RGBA입니다. GPU sRGB 코드 값을 선형 텍스처로 읽어 이중 색 공간 변환을 피했습니다. 행 방향은 비교 전 맞췄습니다. ASTC 4×4는 8bpp, 6×6는 약 3.56bpp이며 가장자리 패딩이 있습니다.</li>
<li>RGB PSNR은 투명 픽셀의 숨은 RGB도 포함합니다. 알파 PSNR은 별도 측정합니다. 합성 PSNR은 sRGB 코드 값 공간에서 straight alpha로 검은색·흰색에 합성한 두 결과의 합산 MSE입니다. Linear 프로젝트의 실제 조명·블렌딩 결과와 같지 않습니다.</li>
<li>피크 오차의 단위는 0~255입니다. 픽셀별 RGB 오차는 세 채널 절대오차의 최댓값, 알파는 단일 채널 절대오차, 합성은 두 배경·세 채널 중 최댓값입니다. 최대오차는 그 픽셀 오차의 최댓값입니다. P99는 ceil(0.99×픽셀 수)번째 값인 nearest-rank 방식입니다. 합성 값은 8비트로 다시 반올림하지 않습니다.</li>
<li>최악 블록 PSNR은 각 실제 압축 블록 영역의 채널 평균 MSE로 계산합니다. 경계 패딩 픽셀은 제외합니다. 좌표는 원본 이미지의 왼쪽 위가 (0,0)입니다. 격자는 Unity 원시 행 순서에 따라 아래쪽에 맞추므로, ASTC 6×6의 위쪽 경계 블록은 부분 높이일 수 있습니다. 오차가 같으면 이미지의 위쪽·왼쪽 위치를 먼저 선택합니다. 블록 크기가 달라지면 집계 영역도 달라지므로, 최악 블록 PSNR은 같은 블록 크기에서 비교하십시오. P99가 0이어도 1% 미만의 국소 오류는 남을 수 있습니다.</li>
<li>피크 지표는 저장된 원본·복원 PNG에서 재계산할 수 있습니다. 그때 기존 압축 결과·속도 측정값·측정 시각은 유지합니다. 피크 지표 갱신 시각은 peakMetricsUtc에 별도 기록합니다. 높은 최대오차가 투명 픽셀의 숨은 RGB에서 나오는지도 알파·합성 지표와 함께 확인하십시오.</li>
<li>SSIM은 8×8 창의 RGB 휘도 근사 값입니다. 알파를 포함하지 않습니다. PSNR과 SSIM은 시각 검토를 대체하지 않습니다. 알파 coverage 및 mipmap 품질은 별도 과제입니다.</li>
<li>이 구현은 한 분할 LDR 모드만 탐색합니다. astcenc의 다중 분할·전체 모드 탐색을 구현하지 않았습니다. 알파 UI에는 4×4를 먼저 검토하십시오. 6×6의 세부 경계 손실은 비교 이미지로 확인하십시오.</li>
<li>ASTC 실행 순서는 baseline → Fast → Balanced입니다. BC7은 별도 실행에서 입력별 Fast → Balanced → Unity Normal 순서입니다. 노트북 클록·온도·다른 작업을 고정하지 않았습니다. 반복 간 변동과 CPU/GPU 하드웨어 차이가 있으므로 범용 속도 배수로 일반화할 수 없습니다.</li></ul>
<h2>검산과 재현</h2><p class="ok" id="verification"></p><p>검산은 런타임과 분리된 도구입니다. 정상 인코딩의 시작 조건이 아닙니다. Android ARM64 네이티브 라이브러리는 링크·ELF ABI 검사를 통과했습니다. iOS Metal 소스는 Windows에서 컴파일하지 못했습니다. 두 플랫폼 모두 기기에서 압축 후 샘플링·방향·색 공간·연속 프레임·리소스 해제를 검증해야 합니다.</p>
<p>피크 계산기는 완전 복원·부분 블록·P99 순위·숨은 RGB·알파 합성·소수 합성·아래쪽 정렬 격자의 7개 고정 사례를 별도로 검산했습니다. 저장 PNG를 System.Drawing으로 다시 읽어 ${rows.length}개 결과의 최대오차 픽셀과 최악 블록 영역을 RGB·알파·합성으로 대조했습니다(${(rows.length*3).toLocaleString('en-US')}개 확인). 기존 ASTC 속도와 전체 품질 측정값은 그대로 보존했습니다.</p>
<p><a href="measurements.csv">전체 ${rows.length}행 CSV</a> · <a href="aggregate.json">그룹 평균 JSON</a> · <a href="baseline/measurements.json">ASTC baseline·CPU</a> · <a href="fast/measurements.json">ASTC Fast</a> · <a href="balanced/measurements.json">ASTC Balanced</a>${hasBc7?' · <a href="bc7/measurements.json">BC7 원본</a> · <a href="bc7/modes.json">BC7 모드 사용량</a>':''}${hasBc7Texture?' · <a href="bc7/gpu-texture-measurements.json">BC7 압축·복사 120행</a>':''} · <a href="../Input/manifest.json">입력 크기·알파 통계·SHA-256</a> · <a href="../Sources/sources.json">출처</a> · <a href="../../README.md">프로젝트 사용법</a></p>
<details><summary>실행 메타데이터</summary><pre id="meta"></pre></details>
<details><summary>출처와 라이선스</summary><ul id="sources"></ul><p><a href="https://github.com/ARM-software/astc-encoder/tree/5.7.0">Arm astcenc 5.7.0</a> · <a href="https://polyhaven.com/license">Poly Haven CC0</a> · <a href="../Sources/Khronos-LICENSE.txt">Khronos 이미지 MIT 라이선스</a></p></details>
<script>
const data=${JSON.stringify({runs:runs.map(({rows,...r})=>r),rows,aggregate,inputs,sources,validation,bc7Validation,bc7Modes,bc7Conditions,bc7Texture,bc7TextureValidation,bc7PlayerValidation})};
const $=id=>document.getElementById(id), fmt=(v,n=2)=>v>=119.999?'∞':Number(v).toFixed(n), ms=v=>v?Number(v).toFixed(3):'—';
const labels={baseline:'ASTC 초기 구현',fast:'ASTC GPU Fast',balanced:'ASTC GPU Balanced','astcenc-fastest':'astcenc fastest','astcenc-fast':'astcenc fast','astcenc-medium':'astcenc medium','bc7-fast':'BC7 GPU Fast','bc7-balanced':'BC7 GPU Balanced','unity-bc7':'Unity BC7 Normal'};
$('hardware').textContent=data.runs[0].gpu+' · '+data.runs[0].cpu+' · Unity '+data.runs[0].unity+' · '+data.runs[0].api+' · '+new Date(data.runs[0].utc).toLocaleString('ko-KR');
if(data.bc7Validation)$('hardware').textContent+=' · BC7 측정 '+new Date(data.runs.find(r=>r.name==='bc7').utc).toLocaleString('ko-KR');
$('meta').textContent=JSON.stringify({runs:data.runs,bc7Conditions:data.bc7Conditions,bc7Texture:data.bc7Texture?Object.fromEntries(Object.entries(data.bc7Texture).filter(([key])=>key!=='rows')):null},null,2);
if(data.bc7TextureValidation){const v=data.bc7TextureValidation,p=data.bc7PlayerValidation;$('bc7TextureVerification').textContent='처리량 측정 Unity '+data.bc7Texture.unity+'. 직접 복사 검산(Unity '+v.unity+'): '+v.cases+'개 크기·품질·색 공간 사례, '+v.frames+'회 복사, '+v.comparedChannels.toLocaleString()+'개 채널의 컴퓨트·픽셀 셰이더 샘플링 차이 '+v.maximumDifference+'/255. 반복 호출 관리 힙 할당 '+v.encodeFrameAllocatedBytes+'바이트. GPU 완료 후 '+v.retiredOutputs+'개 출력 해제 확인.'+(p?.passed?' Windows D3D12 Player(Unity '+p.unity+'): '+p.frames+'개 연속 프레임, '+p.copies+'회 복사, 자원 해제 통과.':'');}
const validationVersions=[...new Set([...data.validation,data.bc7Validation].filter(Boolean).map(v=>v.unity))].join(', ');
$('verification').textContent='검산 Unity '+validationVersions+'. ASTC Fast·Balanced: '+data.validation.reduce((s,r)=>s+r.cases,0)+'개 GPU 인코딩·공식 Arm 디코딩 대조, 최대 차이 '+Math.max(...data.validation.map(r=>r.maxDecoderDifference))+'/255.'+(data.bc7Validation?' BC7: '+data.bc7Validation.cases+'개 GPU 사례의 하드웨어·스칼라 디코더 차이 0/255.':'');
let syncing=false,currentRows=[],focusedRow=null;
const signals={rgb:'RGB',alpha:'알파',composite:'흰색·검은색 합성'};
const pair=p=>'('+p.worstBlockX+', '+p.worstBlockY+')';
function selection(){return {group:$('group').value,resolution:+$('res').value,size:+$('block').value};}
function same(r,s){return r.group===s.group&&(r.resolution??r.width)===s.resolution&&r.size===s.size;}
function textures(){const old=$('texture').value,names=data.sources.filter(s=>s.group===$('group').value).map(s=>s.id);$('texture').replaceChildren(...names.map(n=>{let o=document.createElement('option');o.value=n;o.textContent=n;return o}));$('texture').value=names.includes(old)?old:(names.includes('ui_alpha_atlas')?'ui_alpha_atlas':names[0]);}
function render(){const s=selection(),agg=data.aggregate.filter(r=>same(r,s)),rs=data.rows.filter(r=>same(r,s));
if(data.bc7Texture){$('bc7TextureSection').hidden=s.size!==4;const stream=data.bc7Texture.rows.filter(r=>r.group===s.group&&r.width===s.resolution);$('bc7TextureSummary').innerHTML=['bc7-fast','bc7-balanced'].map(encoder=>{const a=stream.filter(r=>r.encoder===encoder),avg=key=>a.reduce((sum,r)=>sum+r[key],0)/a.length;return '<tr><td>'+labels[encoder]+'</td><td>'+ms(avg('pipelineBatchMedianMs'))+'</td><td>'+ms(avg('submissionMedianMs'))+'</td><td>'+ms(avg('submissionP95Ms'))+'</td><td>'+avg('allocatedBytes')+' bytes</td></tr>'}).join('');}
currentRows=rs.filter(r=>r.texture===$('texture').value);
$('summary').innerHTML=agg.map(r=>'<tr class="'+r.encoder+'"><td>'+labels[r.encoder]+'</td><td>'+fmt(r.rgbPsnr)+'</td><td>'+fmt(r.alphaPsnr)+'</td><td>'+fmt(r.compositePsnr)+'</td><td>'+fmt(r.ssim,4)+'</td><td>'+ms(r.bufferReuseBatchMedianMs)+'</td><td>'+ms(r.codecMedianMs)+'</td><td>'+ms(r.medianMs)+'</td></tr>').join('');
const fast=agg.find(r=>r.encoder==='fast'),base=agg.find(r=>r.encoder==='baseline'),ref=agg.find(r=>r.encoder==='astcenc-fastest');
$('takeaway').textContent='각 10장 평균. Fast의 RGB PSNR은 초기 구현보다 '+fmt(fast.rgbPsnr-base.rgbPsnr)+' dB 높고, astcenc fastest보다 '+fmt(ref.rgbPsnr-fast.rgbPsnr)+' dB 낮습니다. ASTC 페이로드: '+Math.round(fast.payloadBytes).toLocaleString()+' bytes / image.';
const bc7=agg.find(r=>r.encoder==='bc7-fast');if(bc7)$('takeaway').textContent='같은 8bpp, 각 10장 MSE 통합. RGB PSNR: ASTC Fast '+fmt(fast.rgbPsnr)+' / BC7 Fast '+fmt(bc7.rgbPsnr)+' / BC7 Balanced '+fmt(agg.find(r=>r.encoder==='bc7-balanced').rgbPsnr)+' / Unity BC7 Normal '+fmt(agg.find(r=>r.encoder==='unity-bc7').rgbPsnr)+' dB. 페이로드: '+Math.round(bc7.payloadBytes).toLocaleString()+' bytes / image.';
$('peakSummary').innerHTML=agg.map(r=>{const p=r.peak[$('signal').value];return '<tr class="'+r.encoder+'"><td>'+labels[r.encoder]+'</td><td>'+Number(p.maxError).toFixed(3)+'</td><td>'+Number(p.meanP99Error).toFixed(3)+'</td><td>'+fmt(p.worstBlockPsnr)+'</td><td>'+p.worstTexture+'</td><td>'+pair(p)+'</td></tr>'}).join('');
$('individual').innerHTML=rs.slice().sort((a,b)=>a.texture.localeCompare(b.texture)||data.aggregate.findIndex(r=>r.encoder===a.encoder)-data.aggregate.findIndex(r=>r.encoder===b.encoder)).map(r=>{const p=r.peak[$('signal').value];return '<tr class="'+r.encoder+'"><td>'+r.texture+'</td><td>'+labels[r.encoder]+'</td><td>'+fmt(r.rgbPsnr)+'</td><td>'+fmt(r.alphaPsnr)+'</td><td>'+fmt(r.compositePsnr)+'</td><td>'+fmt(r.ssim,4)+'</td><td>'+p.maxError.toFixed(3)+'</td><td>'+p.p99Error.toFixed(3)+'</td><td>'+fmt(p.worstBlockPsnr)+'</td><td>'+pair(p)+'</td><td>('+p.maxX+', '+p.maxY+')</td><td>'+ms(r.bufferReuseBatchMedianMs)+'</td><td>'+ms(r.codecMedianMs)+'</td><td>'+ms(r.medianMs)+'</td><td>'+ms(r.p95Ms)+'</td></tr>'}).join('');
const name=$('texture').value,stem=name+'_'+s.resolution+'_'+s.size;
const panels=[['original','원본','../Input/'+s.resolution+'/'+name+'.png'],...['baseline','fast','balanced','astcenc-fastest','astcenc-medium',...(bc7?['bc7-fast','bc7-balanced','unity-bc7']:[])].map(e=>[e,labels[e],e.startsWith('bc7-')||e==='unity-bc7'?'bc7/'+stem+'_'+e+'.png':e.startsWith('astcenc-')?'baseline/'+stem+'_'+e.replace('-','_')+'.png':e+'/'+stem+'_gpu.png'])];
$('images').innerHTML=panels.map(([e,label,url])=>{const r=currentRows.find(r=>r.encoder===e),p=r?.peak[$('signal').value];return '<section class="card"><header><h3>'+label+'</h3><div class="metrics">'+(r?'RGB '+fmt(r.rgbPsnr)+' · A '+fmt(r.alphaPsnr)+' · 합성 '+fmt(r.compositePsnr)+' dB<br>최대 '+p.maxError.toFixed(3)+' · P99 '+p.p99Error.toFixed(3)+' · 최악 블록 '+fmt(p.worstBlockPsnr)+' dB':'원본 '+s.resolution+'×'+s.resolution+'<br>모든 패널에 같은 원본 좌표 표시')+'</div>'+(r?'<button data-focus="'+e+'" '+(p.maxError===0?'disabled':'')+'>최악 블록 확대</button><button data-pixel="'+e+'" '+(p.maxError===0?'disabled':'')+'>최대오차 픽셀</button>':'')+'</header><div class="viewport"><div class="frame"><img alt="'+label+' '+name+'" src="'+url+'"><div class="blockmark"></div><div class="pixelmark"></div></div></div></section>'}).join('');
document.querySelectorAll('[data-focus]').forEach(b=>b.addEventListener('click',()=>focusEncoder(b.dataset.focus,true)));
document.querySelectorAll('[data-pixel]').forEach(b=>b.addEventListener('click',()=>focusEncoder(b.dataset.pixel,true,'pixel')));
document.querySelectorAll('.viewport').forEach(v=>v.addEventListener('scroll',()=>{if(syncing||!v.isConnected)return;syncing=true;document.querySelectorAll('.viewport').forEach(other=>{if(other!==v&&(other.scrollLeft!==v.scrollLeft||other.scrollTop!==v.scrollTop)){other.scrollLeft=v.scrollLeft;other.scrollTop=v.scrollTop}});syncing=false})); focusEncoder(bc7?'bc7-fast':'fast');background();}
function focusEncoder(encoder,jump=false,target='block'){focusedRow=currentRows.find(r=>r.encoder===encoder);const p=focusedRow.peak[$('signal').value];
$('focusNote').textContent='표시 기준: '+labels[encoder]+' · '+signals[$('signal').value]+' · 최악 블록 '+pair(p)+' '+p.worstBlockWidth+'×'+p.worstBlockHeight+' · '+fmt(p.worstBlockPsnr)+' dB · 최대오차 픽셀 ('+p.maxX+', '+p.maxY+'). 좌표는 원본의 왼쪽 위가 (0,0)입니다.';
if(jump)$('zoom').value=Math.max(400,+$('zoom').value);zoom();if(jump){const z=+$('zoom').value/100;document.querySelectorAll('.viewport').forEach(v=>{v.scrollLeft=Math.max(0,(target==='pixel'?p.maxX+.5:p.worstBlockX+p.worstBlockWidth/2)*z-v.clientWidth/2);v.scrollTop=Math.max(0,(target==='pixel'?p.maxY+.5:p.worstBlockY+p.worstBlockHeight/2)*z-v.clientHeight/2)})}}
function zoom(){$('zoomValue').textContent=$('zoom').value+'%';const z=+$('zoom').value/100,side=+$('res').value*z;document.querySelectorAll('.frame').forEach(f=>{f.style.width=side+'px';f.style.height=side+'px'});document.querySelectorAll('.viewport img').forEach(i=>{i.style.width=side+'px';i.style.height=side+'px';i.style.imageRendering=z>=1?'pixelated':'auto'});if(!focusedRow)return;const p=focusedRow.peak[$('signal').value];document.querySelectorAll('.blockmark').forEach(m=>{m.style.display=p.maxError===0?'none':'block';m.style.left=p.worstBlockX*z+'px';m.style.top=p.worstBlockY*z+'px';m.style.width=p.worstBlockWidth*z+'px';m.style.height=p.worstBlockHeight*z+'px'});document.querySelectorAll('.pixelmark').forEach(m=>{m.style.display=p.maxError===0?'none':'block';m.style.left=(p.maxX+.5)*z+'px';m.style.top=(p.maxY+.5)*z+'px'})}
function background(){$('images').className='grid '+$('background').value;}
$('group').addEventListener('change',()=>{textures();render()});['res','block','texture','signal'].forEach(id=>$(id).addEventListener('change',render));$('background').addEventListener('change',background);$('zoom').addEventListener('input',zoom);$('one').addEventListener('click',()=>{$('zoom').value=100;zoom()});
data.sources.forEach(s=>{let b=document.createElement('button');b.innerHTML='<img alt="'+s.id+'" src="../Input/256/'+s.id+'.png"><span>'+s.group+' · '+s.id+'</span>';b.onclick=()=>{$('group').value=s.group;textures();$('texture').value=s.id;render();$('images').scrollIntoView({behavior:'smooth'})};$('thumbs').append(b);let li=document.createElement('li');li.textContent=s.id+' · '+s.kind+' · '+s.license+' ';if(s.page){let a=document.createElement('a');a.href=s.page;a.textContent='출처';li.append(a)}$('sources').append(li)});textures();render();
</script></body></html>`;
fs.writeFileSync(path.join(root,'Benchmark/Results/report.html'),html);
console.log(`Verified 60 input hashes and ${rows.length} rows. Report: ${path.join(root,'Benchmark/Results/report.html')}`);
