import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';

const [input, destination = 'Benchmark/Results/mobile-atlas', correctionsFile] = process.argv.slice(2);
if (!input) throw new Error('Usage: node Tools/Report-GpuAtlasMobileBenchmark.mjs result.json [output-directory] [corrections.json]');
const raw = fs.readFileSync(input);
const report = JSON.parse(raw);
if (!report.passed) throw new Error(`Benchmark failed: ${report.error}`);
const sha256 = bytes => crypto.createHash('sha256').update(bytes).digest('hex');
const buildInfoPath = path.resolve(path.dirname(input),'../build-info.json');
const buildInfo = fs.existsSync(buildInfoPath) ? JSON.parse(fs.readFileSync(buildInfoPath,'utf8')) : null;
const manifest = JSON.parse(fs.readFileSync('Benchmark/Input/manifest.json', 'utf8'));
const builtManifestPath = path.resolve(path.dirname(input),'../input-manifest.json');
if (!fs.existsSync(builtManifestPath)) throw new Error('The APK input manifest is required beside the run build-info.json.');
const builtManifestBytes = fs.readFileSync(builtManifestPath);
const builtInputs = JSON.parse(builtManifestBytes).items;
if (builtInputs.length !== manifest.length || builtInputs.some(b => !manifest.some(m =>
  m.texture === b.texture && m.resolution === b.resolution && m.group === b.group && m.sha256 === b.sha256)))
  throw new Error('APK inputs differ from the repository manifest.');
for (const m of manifest) {
  const bytes = fs.readFileSync(path.join('Benchmark/Input',String(m.resolution),m.texture+'.png'));
  if (crypto.createHash('sha256').update(bytes).digest('hex') !== m.sha256.toLowerCase())
    throw new Error(`Input hash changed: ${m.resolution}/${m.texture}`);
}
const rows = [...report.results];
const corrections = [];
const replacementFiles = [];
const fields = ['resolution','group','texture','operation','quality','scheduling','repetition'];
if (correctionsFile) {
  const specification = JSON.parse(fs.readFileSync(correctionsFile,'utf8'));
  if (specification.originalSha256 !== sha256(raw)) throw new Error('Correction source hash does not match the original report.');
  const corrected = new Set();
  for (const change of specification.replacements) {
    if (!change.reason?.trim() || fields.some(f=>change.id[f] === undefined)) throw new Error('Each correction needs a complete identity and reason.');
    const identity = fields.map(f=>change.id[f]).join('/');
    if (corrected.has(identity)) throw new Error('Duplicate correction: '+identity);
    corrected.add(identity);
    const replacementPath = path.resolve(path.dirname(correctionsFile),change.resultFile);
    const replacementRaw = fs.readFileSync(replacementPath), replacement = JSON.parse(replacementRaw);
    const replacementBuild = JSON.parse(fs.readFileSync(path.resolve(path.dirname(replacementPath),'../build-info.json'),'utf8'));
    const replacementManifest = fs.readFileSync(path.resolve(path.dirname(replacementPath),'../input-manifest.json'));
    if (!replacement.passed || replacement.pauses > 0 || !buildInfo?.apkSha256 ||
        replacementBuild.apkSha256 !== buildInfo.apkSha256 || sha256(replacementManifest) !== sha256(builtManifestBytes) ||
        ['unity','device','gpu','api','os'].some(f=>replacement[f] !== report[f]) ||
        ['encodeBlocks','highQualityEncodeBlocks'].some(f=>replacement.request[f] !== report.request[f]))
      throw new Error('Replacement must use the same APK, device, inputs and budgets, with no application pause.');
    const matches = row => fields.every(f=>row[f] === change.id[f]);
    const originals = rows.map((row,index)=>({row,index})).filter(x=>matches(x.row));
    const candidates = replacement.results.filter(matches);
    if (originals.length !== 1 || candidates.length !== 1) throw new Error('Correction must select exactly one original and one replacement: '+identity);
    const filename = `replacement-${corrections.length+1}.json`;
    replacementFiles.push({filename,bytes:replacementRaw});
    corrections.push({id:change.id,reason:change.reason,originalCompletionMs:originals[0].row.completionMs,
      replacementCompletionMs:candidates[0].completionMs,resultFile:filename,sourceSha256:sha256(replacementRaw),utc:replacement.utc});
    rows[originals[0].index] = candidates[0];
  }
}
const skipped = report.skipped ?? [];
const quantile = (values, p) => {
  const a = values.filter(Number.isFinite).filter(v => v >= 0).sort((a,b) => a-b);
  return a.length ? a[Math.ceil((a.length-1)*p)] : null;
};
const key = r => [r.resolution,r.group,r.operation,r.quality,r.scheduling].join('/');
const groups = Map.groupBy(rows, key);
const summary = [];
if (report.request.measure) {
  if (rows.length + skipped.length !== manifest.length * 12 * report.request.repetitions) throw new Error(`Incomplete measurement count: ${rows.length} measured, ${skipped.length} excluded`);
  const seen = new Set();
  for (const r of [...rows, ...skipped]) {
    if (!manifest.some(m => m.texture === r.texture && m.resolution === r.resolution && m.group === r.group)) throw new Error('Unknown input');
    const id = [key(r),r.texture,r.repetition].join('/');
    if (seen.has(id)) throw new Error(`Duplicate result: ${id}`);
    seen.add(id);
  }
  for (const m of manifest) for (const mode of ['immediate','frame-budget']) {
    const paths = [['pack-convert','Fast'],['pack-convert','Balanced'],['pack-convert','HighQuality'],['pack-copy','Fast'],['add-copy','Fast'],['repack','Fast']];
    for (const [operation, quality] of paths) for (let repeat=0; repeat<report.request.repetitions; repeat++) {
      const id = [[m.resolution,m.group,operation,quality,mode].join('/'),m.texture,repeat].join('/');
      if (!seen.has(id)) throw new Error(`Missing planned measurement: ${id}`);
    }
  }
}
for (const [id, values] of groups) {
  if (values.length !== 10 * report.request.repetitions) throw new Error(`Incomplete group: ${id}`);
  const frames = values.flatMap(v => v.activeFrameMs);
  const gpu = values.flatMap(v => v.gpuWindow.map(t => t.gpuMs)).filter(v => v > 0);
  summary.push({group:id, samples:values.length,
    completionMedianMs:quantile(values.map(v=>v.completionMs),.5), completionP95Ms:quantile(values.map(v=>v.completionMs),.95),
    submitMedianMs:quantile(values.map(v=>v.submitCpuMs),.5), submitMaxMs:quantile(values.map(v=>v.submitCpuMs),1),
    activeFrameSamples:frames.length, activeFrameP95Ms:quantile(frames,.95), activeFrameP99Ms:quantile(frames,.99), activeFrameMaxMs:quantile(frames,1),
    gpuWindowSamples:gpu.length, gpuWindowP95Ms:quantile(gpu,.95), gpuWindowP99Ms:quantile(gpu,.99), gpuWindowMaxMs:quantile(gpu,1),
    submitManagedMedianBytes:report.allocationCounterAvailable ? quantile(values.map(v=>v.submitManagedBytes),.5) : null,
    graphicsPeakIncreaseBytes:values.some(v=>v.graphicsPeak > 0 || v.graphicsStart > 0)
      ? quantile(values.map(v=>v.graphicsPeak-v.graphicsStart),1) : null});
}
summary.sort((a,b)=>a.group.localeCompare(b.group,undefined,{numeric:true}));
fs.mkdirSync(destination,{recursive:true});
const effectiveRaw = corrections.length ? Buffer.from(JSON.stringify({...report,results:rows,corrections})) : raw;
const output = {...report, results:undefined, sourceSha256:sha256(raw), effectiveSourceSha256:sha256(effectiveRaw),
  inputManifestSha256:sha256(builtManifestBytes), corrections, summary};
if (buildInfo) output.buildInfo = buildInfo;
fs.writeFileSync(path.join(destination,'summary.json'),JSON.stringify(output,null,2));
fs.writeFileSync(path.join(destination,'measurements.json'),effectiveRaw);
if (corrections.length) fs.copyFileSync(input,path.join(destination,'original-measurements.json'));
for (const replacement of replacementFiles) fs.writeFileSync(path.join(destination,replacement.filename),replacement.bytes);
fs.copyFileSync(builtManifestPath,path.join(destination,'inputs.json'));
const cols = Object.keys(summary[0]??{});
fs.writeFileSync(path.join(destination,'summary.csv'),cols.join(',')+'\n'+summary.map(r=>cols.map(c=>r[c]??'').join(',')).join('\n')+'\n');
const esc = v => String(v??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const fmt = v => v === null || v === undefined || v < 0 ? 'N/A' : v.toFixed(2);
const mb = v => (v/1048576).toFixed(1);
const startedAt = new Date(report.utc);
const localStart = Number.isNaN(startedAt.getTime()) ? report.utc
  : new Intl.DateTimeFormat('ko-KR',{timeZone:'Asia/Seoul',dateStyle:'medium',timeStyle:'short'}).format(startedAt)+' KST';
const correctionNote = corrections.length ? `<p>중단된 측정 ${corrections.length}건을 같은 APK·기기·예산의 재측정으로 교체했습니다. 원본은 보존했습니다. 백그라운드/복귀 횟수에는 최초 실행 중의 중단도 포함합니다. <a href="original-measurements.json">교체 전 원본</a><br>${corrections.map(c=>`${esc(fields.map(f=>c.id[f]).join('/'))}: ${esc(c.reason)} <a href="${c.resultFile}">재측정 원본</a>`).join('<br>')}</p>` : '';
const thermal = report.thermal.map(t=>`<tr><td>${fmt(t.elapsedSeconds)}</td><td>${t.edits}</td><td>${t.status}</td><td>${t.batteryTemperatureTenthsC < 0 ? 'N/A' : fmt(t.batteryTemperatureTenthsC/10)}</td><td>${mb(t.totalMemory)}</td><td>${t.graphicsMemory > 0 ? mb(t.graphicsMemory) : 'N/A'}</td><td>${mb(t.managedMemory)}</td></tr>`).join('');
const body = summary.map(r=>`<tr><th>${esc(r.group)}</th><td>${r.samples}</td><td>${fmt(r.completionMedianMs)}</td><td>${fmt(r.submitMedianMs)}</td><td>${fmt(r.activeFrameP95Ms)}</td><td>${fmt(r.activeFrameP99Ms)}</td><td>${fmt(r.activeFrameMaxMs)}</td><td>${fmt(r.gpuWindowP95Ms)}</td><td>${fmt(r.gpuWindowMaxMs)}</td><td>${r.submitManagedMedianBytes ?? 'N/A'}</td></tr>`).join('');
fs.writeFileSync(path.join(destination,'report.html'),`<!doctype html><html lang="ko"><meta charset="utf-8"><meta name="viewport" content="width=device-width"><title>GpuTextureAtlas · Android 측정</title><style>body{font:15px/1.6 system-ui;margin:32px;background:#10151e;color:#e1e8f2}h1{font-size:28px}p{max-width:1100px}a{color:#79cfff}.scroll{overflow:auto}table{border-collapse:collapse;width:100%;font-variant-numeric:tabular-nums;font-size:13px}th,td{padding:9px;border-bottom:1px solid #304057;text-align:right;white-space:nowrap}th:first-child{text-align:left}thead{background:#1d2b40}tr:hover{background:#182332}input{background:#1d2b40;color:white;padding:9px;border:1px solid #526278;width:300px}small{color:#afbdd2}</style>
<h1>GpuTextureAtlas · Android 측정</h1><p>${esc(report.device)} · ${esc(report.gpu)} · Unity ${esc(report.unity)}<br>${esc(report.api)}<br>측정 시작 ${esc(localStart)} · ${rows.length}회 측정 · 연속 편집 ${report.stressEdits}회 · 백그라운드/복귀 ${report.pauses}/${report.resumes}회</p>
${correctionNote}
<p>측정 제외: ${skipped.length}건. ${skipped.length ? [...new Set(skipped.map(s=>s.reason))].map(esc).join(' ') : '없음'} 제외된 경로에는 성능 수치를 부여하지 않았습니다. 일반 압축 예산은 ${report.request.encodeBlocks ?? 1024}블록, HighQuality 예산은 ${report.request.highQualityEncodeBlocks ?? report.request.encodeBlocks ?? 1024}블록/프레임입니다.</p>
<p>256·512·1024에서 알파 10장과 불투명 10장씩 사용했습니다. 입력 로딩은 측정에서 제외했습니다. 각 경로를 예열한 뒤 입력별 ${report.request.repetitions}회 측정했습니다. 단순 카메라와 상태 UI가 있는 전용 앱의 결과입니다. 측정한 아틀라스를 화면에 렌더링하는 부하는 포함하지 않았습니다. GPU 클록은 고정하지 않았습니다. 실제 게임 장면의 성능을 보장하지 않습니다.</p>
<p>완료 시간은 프레임과 fence 대기를 포함합니다. 프레임 P95/P99는 각 그룹의 작업 진행 중 프레임 표본을 합산한 값입니다. GPU 열은 뒤의 8프레임까지 포함한 앱 전체 FrameTimingManager 값이며 압축 커널 시간이 아닙니다. 유효한 GPU 표본이 없으면 N/A입니다. 짧은 실행의 분위수는 표본 수에 영향을 받습니다. 프레임 예산은 처리량과 완료 지연을 교환하며 고정 ms를 보장하지 않습니다. 호출별 할당 카운터가 실제 배열 할당을 감지하지 못하면 GC 열은 N/A입니다. 고정 0 반환을 0GC로 해석하지 않습니다.</p>
<p><a href="summary.json">요약 JSON</a> · <a href="summary.csv">CSV</a> · <a href="measurements.json">측정 JSON</a> · <a href="inputs.json">APK 입력 목록</a></p><input id="filter" aria-label="측정 결과 필터" placeholder="예: 1024 alpha HighQuality"><div class="scroll"><table id="results"><thead><tr><th>해상도/표본/작업/품질/방식</th><th>횟수</th><th>완료 중앙값 ms</th><th>호출 CPU 중앙값 ms</th><th>프레임 P95 ms</th><th>P99 ms</th><th>최대 ms</th><th>GPU P95 ms</th><th>GPU 최대 ms</th><th>호출 GC bytes</th></tr></thead><tbody>${body}</tbody></table></div>
<h2>반복 편집과 기기 상태</h2><p>반복 검사 설정은 ${report.request.stressSeconds}초입니다. 경과 시간은 앱 시작 기준이며 중단 시간도 포함합니다. Android 열 상태와 배터리 온도를 기록했습니다. 배터리 온도는 GPU 온도가 아닙니다. 메모리는 Unity 할당량 추정치입니다. 보고서와 측정 앱의 메모리도 포함합니다. 열 상태 -1은 조회 불가입니다. 마지막 표본은 아틀라스와 번들 해제 후 값입니다.</p><div class="scroll"><table><thead><tr><th>경과 s</th><th>편집 횟수</th><th>열 상태</th><th>배터리 °C</th><th>전체 MiB</th><th>그래픽 MiB</th><th>Managed MiB</th></tr></thead><tbody>${thermal}</tbody></table></div><p><small>원본 SHA-256: ${output.sourceSha256}</small></p><script>document.querySelector('#filter').oninput=e=>{let terms=e.target.value.toLowerCase().split(' ').filter(Boolean);document.querySelectorAll('#results tbody tr').forEach(r=>r.hidden=!terms.every(q=>r.textContent.toLowerCase().includes(q)))}</script></html>`);
console.log(JSON.stringify({rows:rows.length,groups:summary.length,output:destination,stressEdits:report.stressEdits,passed:report.passed}));
