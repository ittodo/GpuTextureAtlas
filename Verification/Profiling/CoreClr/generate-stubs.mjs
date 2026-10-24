import fs from 'node:fs';
const headerPath = process.argv[2];
if (!headerPath) throw new Error('Pass the installed Microsoft SDK corprof.h path.');
const header = fs.readFileSync(headerPath,'utf8');
const implemented = new Set(['Initialize','Shutdown','ObjectAllocated','GarbageCollectionStarted','GarbageCollectionFinished','SurvivingReferences','MovedReferences']);
let output='// Callback declarations derived from the installed Microsoft SDK. Verification only.\n';
for (const name of ['ICorProfilerCallback','ICorProfilerCallback2']) {
  const start=header.indexOf(`${name} : public `);
  const body=header.slice(start,header.indexOf('\n    };',start));
  for (const m of body.matchAll(/virtual HRESULT STDMETHODCALLTYPE (\w+)\s*\(([\s\S]*?)\) = 0;/g)) {
    if (implemented.has(m[1])) continue;
    const params=m[2].replace(/\/\*[\s\S]*?\*\//g,'').replace(/\s+/g,' ').trim();
    output+=`HRESULT STDMETHODCALLTYPE ${m[1]}(${params}) override { return S_OK; }\n`;
  }
}
fs.writeFileSync(new URL('CallbackStubs.h', import.meta.url),output);
console.log(`Generated ${output.split('\n').length-2} no-op callback declarations.`);
