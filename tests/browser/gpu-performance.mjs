import assert from 'node:assert/strict';
import { appendFile, mkdir, readFile, writeFile } from 'node:fs/promises';
import { resolve } from 'node:path';
import { chromium } from 'playwright';
import { graphicsLaunchOptions } from './graphics-options.mjs';
import { publishedPrefix, servePublishedSite } from './published-site.mjs';
import {
  waitForGpuPresentation, compareGpuPaint, verifySelectionOverlay
} from './gpu-presentation.mjs';

// This runner is intentionally shared by both independently built revisions.
// It never creates a WebGPU adapter/device or submits benchmark-only GPU work.
const [baselineDirectory, candidateDirectory] = process.argv.slice(2);
assert.ok(baselineDirectory && candidateDirectory,
  'Usage: node tests/browser/gpu-performance.mjs BASELINE_SITE CANDIDATE_SITE');
const baselineSha = process.env.UNOUML_BASELINE_SHA;
const candidateSha = process.env.UNOUML_CANDIDATE_SHA;
for (const [name, value] of Object.entries({baselineSha, candidateSha}))
  assert.match(value ?? '', /^[0-9a-f]{40}$/, name + ' must be an immutable full commit SHA.');
assert.equal(process.env.UNOUML_REQUIRE_WEBGPU, '1');
const output = resolve(process.env.SCREENSHOT_DIR || 'gpu-performance');
await mkdir(output, {recursive:true});
const configuration = {
  viewport:{width:1390,height:860}, deviceScaleFactor:1,
  colorScheme:'light', locale:'en-US', timezoneId:'UTC',
  sampleCount:4, cameraScale:1.5, cameraWarmups:2, cameraSamples:6,
  editWarmups:2, editSamples:4, transformWarmups:2, transformSamples:4,
  requestedDriver:process.env.UNOUML_GPU_DRIVER || (process.platform === 'linux' ? 'software' : 'platform'),
  launch:graphicsLaunchOptions(true),
  order:['baseline','candidate','candidate','baseline'],
  workloads:['starter','classes-64','classes-256']
};

// Passive observation preserves the exact promises, exceptions, return values,
// and API order. Promise observers add JS work to BOTH revisions. Their latency
// is API completion latency, not a GPU timestamp or GPU execution duration.
// Shared-device observations can include Uno's own work in addition to diagram
// rendering; candidate-only native fields separately delimit diagram callbacks.
function installGpuObserver() {
  const events = [], requests = [], devices = new WeakMap(), queues = new WeakMap();
  const stacks = new WeakMap();
  let sequence = 0, dropped = 0, nextDevice = 1;
  const limit = 16384;
  function record(kind, details) {
    const event = {sequence:++sequence,kind,started:performance.now(),...details};
    if (events.length < limit) events.push(event); else dropped++;
    return event;
  }
  function errorText(error) { return error ? String(error.message || error).slice(0,1000) : null; }
  function adapterInfo(adapter) {
    const info = adapter.info;
    return info ? {vendor:info.vendor,architecture:info.architecture,device:info.device,
      description:info.description,isFallbackAdapter:info.isFallbackAdapter} : null;
  }
  const available = {
    requestDevice:typeof globalThis.GPUAdapter?.prototype.requestDevice === 'function',
    popErrorScope:typeof globalThis.GPUDevice?.prototype.popErrorScope === 'function',
    submit:typeof globalThis.GPUQueue?.prototype.submit === 'function'
  };
  if (available.requestDevice) {
    const original = GPUAdapter.prototype.requestDevice;
    GPUAdapter.prototype.requestDevice = function(...args) {
      const entry = {index:requests.length,info:adapterInfo(this),requestedAt:performance.now()};
      requests.push(entry);
      const promise = Reflect.apply(original,this,args);
      promise.then(device => {
        const id = nextDevice++;
        devices.set(device,id); queues.set(device.queue,id);
        entry.deviceId = id; entry.resolvedAt = performance.now();
        entry.features = [...device.features];
      }, error => { entry.error = errorText(error); entry.resolvedAt = performance.now(); });
      return promise;
    };
  }
  if (available.popErrorScope) {
    const push = GPUDevice.prototype.pushErrorScope, pop = GPUDevice.prototype.popErrorScope;
    GPUDevice.prototype.pushErrorScope = function(filter) {
      const result = Reflect.apply(push,this,[filter]);
      let stack = stacks.get(this);
      if (!stack) stacks.set(this,stack=[]);
      stack.push(filter);
      return result;
    };
    GPUDevice.prototype.popErrorScope = function(...args) {
      const entry = record('popErrorScope',{deviceId:devices.get(this) ?? null,
        filter:stacks.get(this)?.pop() ?? null});
      let promise;
      try { promise = Reflect.apply(pop,this,args); }
      catch (error) { entry.error=errorText(error); entry.thrown=true; throw error; }
      entry.callMilliseconds = performance.now()-entry.started;
      promise.then(error => {
        entry.completed = performance.now();
        entry.milliseconds = entry.completed-entry.started;
        entry.error = errorText(error);
      }, error => {
        entry.completed = performance.now();
        entry.milliseconds = entry.completed-entry.started;
        entry.error = errorText(error); entry.rejected = true;
      });
      return promise;
    };
  }
  if (available.submit) {
    const original = GPUQueue.prototype.submit;
    GPUQueue.prototype.submit = function(...args) {
      const entry = record('queue.submit',{deviceId:queues.get(this) ?? null});
      try { return Reflect.apply(original,this,args); }
      catch (error) { entry.error=errorText(error); throw error; }
      finally { entry.callMilliseconds=performance.now()-entry.started; entry.completed=performance.now(); }
    };
  }
  Object.defineProperty(globalThis,'__unoGpuBenchmark',{
    value:{
      mark:() => sequence,
      snapshot:(after=0) => ({
        available,requests:structuredClone(requests),dropped,
        events:events.filter(event=>event.sequence>after).map(event=>({...event}))
      })
    }
  });
}

const cumulative = [
  'geometryUploadBytes','textureUploadBytes','cameraUploadBytes',
  'drawUniformUploadBytes','gradientUploadBytes','bundleBuilds',
  'presentedFrames','submittedFrames','deferredFrameRequests'
];
const retainedCamera = [
  'geometryUploadBytes','textureUploadBytes','drawUniformUploadBytes',
  'gradientUploadBytes','bundleBuilds','vertexBytes','textureBytes'
];
function accepted(value) {
  assert.equal(value.backend,'webgpu');
  assert.equal(value.pending,false);
  assert.equal(value.presentedRevision,value.revision);
  assert.equal(value.statistics.revision,value.revision);
  assert.equal(value.statistics.backend,'Uno shared WebGPU');
  assert.equal(value.statistics.sampleCount,configuration.sampleCount);
  assert.ok(value.statistics.drawCalls>0 && value.statistics.visibleCommands>0);
  assert.ok(Number.isFinite(value.statistics.cpuEncodeMilliseconds));
  assert.equal(value.statistics.commands,
    value.statistics.visibleCommands+value.statistics.culledCommands);
}
function resourceSummary(state) {
  const fields=['sampleCount','pipelineCount','commands','visibleCommands','culledCommands',
    'drawCalls','visibleDirectMeshCommands','viewportCommands','visibleViewportCommands',
    'geometryBuffers','imageTextures','renderBundles','vertexBytes','textureBytes',
    'drawUniformCapacityBytes','gradientCapacityBytes','uniformAndStopBytes'];
  return Object.fromEntries(fields.map(key=>[key,state.statistics[key]??null]));
}
function counters(before, after) {
  return Object.fromEntries(cumulative.filter(key=>Number.isFinite(before[key])&&Number.isFinite(after[key]))
    .map(key=>[key,after[key]-before[key]]));
}
function distribution(values) {
  const sorted=values.filter(Number.isFinite).sort((a,b)=>a-b);
  if (!sorted.length) return null;
  const quantile=p=>sorted[Math.min(sorted.length-1,Math.ceil(sorted.length*p)-1)];
  return {count:sorted.length,min:sorted[0],median:quantile(.5),
    p95:quantile(.95),max:sorted.at(-1)};
}
function phaseSummary(samples) {
  const scopeEvents=samples.flatMap(sample=>sample.observer.events)
    .filter(event=>event.kind==='popErrorScope');
  const completedScopes=scopeEvents.filter(event=>Number.isFinite(event.milliseconds));
  return {
    scopePromises:{observed:scopeEvents.length,completed:completedScopes.length,
      pending:scopeEvents.filter(event=>!Number.isFinite(event.milliseconds)&&!event.thrown).length,
      synchronousFailures:scopeEvents.filter(event=>event.thrown).length,
      rejected:scopeEvents.filter(event=>event.rejected).length},
    wallMilliseconds:distribution(samples.map(sample=>sample.wallMilliseconds)),
    operationMilliseconds:distribution(samples.map(sample=>sample.operationMilliseconds)),
    cpuEncodeMilliseconds:distribution(samples.map(sample=>sample.after.statistics.cpuEncodeMilliseconds)),
    compilationMilliseconds:distribution(samples.filter(sample=>sample.operation!=='camera')
      .map(sample=>sample.after.compiler?.compileMs)),
    compiler:Object.fromEntries(['geometryBuilds','geometryCacheHits','textureBytes','cacheBytes']
      .map(key=>[key,distribution(samples.filter(sample=>sample.operation!=='camera')
        .map(sample=>sample.after.compiler?.[key]))])),
    rasterCacheDeltas:Object.fromEntries(['rasterCacheHits','rasterCacheMisses'].map(key=>[key,
      distribution(samples.filter(sample=>sample.operation!=='camera').map(sample=>
        Number.isFinite(sample.before.compiler?.[key])&&Number.isFinite(sample.after.compiler?.[key])
          ?sample.after.compiler[key]-sample.before.compiler[key]:null))])),
    validationScopeMilliseconds:distribution(samples.map(sample=>sample.after.statistics.validationScopeMilliseconds)),
    validationDispatchMilliseconds:distribution(samples.map(sample=>sample.after.statistics.validationDispatchMilliseconds)),
    scopePromiseMilliseconds:distribution(completedScopes.map(event=>event.milliseconds)),
    queueSubmitCallMilliseconds:distribution(samples.flatMap(sample=>sample.observer.events
      .filter(event=>event.kind==='queue.submit').map(event=>event.callMilliseconds))),
    drawCalls:distribution(samples.map(sample=>sample.after.statistics.drawCalls)),
    visibleCommands:distribution(samples.map(sample=>sample.after.statistics.visibleCommands)),
    visibleDirectMeshCommands:distribution(samples.map(sample=>sample.after.statistics.visibleDirectMeshCommands)),
    counters:Object.fromEntries(cumulative.map(key=>[key,
      distribution(samples.map(sample=>sample.deltas[key]))]))
  };
}
function validateObserver(observer) {
  assert.equal(observer.dropped,0,'Passive GPU diagnostics exceeded their bounded buffer.');
  for (const event of observer.events)
    assert.equal(event.error ?? null,null,'Observed GPU API error: '+JSON.stringify(event));
}

// Same-sized native class sources exercise large retained scenes. Each measured
// edit uses a NEW source string, preventing the viewport's 8-entry SVG cache
// from turning a native source render measurement into a cache-hit benchmark.
function classSource(count, edit=0) {
  const lines=['@startuml','skinparam defaultFontName monospace'];
  for (let index=0;index<count;index++) {
    const alias='N'+String(index).padStart(3,'0');
    const label=index===Math.floor(count/2)+Math.floor(Math.ceil(Math.sqrt(count))/2)
      ? 'Edit '+String(edit).padStart(3,'0') : 'Node '+String(index).padStart(3,'0');
    lines.push('class "'+label+'" as '+alias);
  }
  return [...lines,'@enduml'].join('\n');
}

async function setupClasses(page, count) {
  await page.locator('.surface').first().evaluate(async (surface,{source,count,scale}) => {
    const ui=await import(new URL('uml/viewport.js',document.baseURI).href);
    const id=surface.getRootNode().host.id;
    ui.setLocalMode(id,true);
    await ui.render(id,source,'benchmark.puml');
    if(surface.querySelector('.paper').dataset.renderer!=='managed')
      throw new Error('The representative workload did not use the native PlantUML renderer.');
    const saved=JSON.parse(ui.getCanvasState(id)).state;
    if(saved.nodes.length!==count || saved.edges.length!==0)
      throw new Error('Unexpected class authoring scene: '+JSON.stringify(saved));
    saved.settings.automatic=false;
    const columns=Math.ceil(Math.sqrt(count));
    const width=Math.max(...saved.nodes.map(node=>node.width))+40;
    const height=Math.max(...saved.nodes.map(node=>node.height))+40;
    saved.nodes.forEach((node,index)=>{
      node.x=(index%columns)*width; node.y=Math.floor(index/columns)*height;
    });
    ui.setCanvasState(id,JSON.stringify(saved),10000);
    ui.fit(id);
    const current=new DOMMatrixReadOnly(getComputedStyle(surface.querySelector('.paper')).transform).a;
    // Stay inside one quality bucket without forcing an unchanged camera transaction.
    if(Math.abs(current-scale)>1e-9*Math.max(1,current,scale))ui.zoom(id,scale/current);
    const minimum=Math.min(...saved.nodes.map(node=>node.x));
    const maximum=Math.max(...saved.nodes.map(node=>node.x+node.width));
    const centerX=(minimum+maximum)/2;
    const centerY=(Math.min(...saved.nodes.map(node=>node.y))+
      Math.max(...saved.nodes.map(node=>node.y+node.height)))/2;
    const distance=node=>(node.x+node.width/2-centerX)**2+(node.y+node.height/2-centerY)**2;
    const middle=saved.nodes.filter(node=>!node.parent&&node.x>minimum+12&&node.x+node.width<maximum-12)
      .sort((a,b)=>distance(a)-distance(b))[0];
    if(!middle)throw new Error('No interior, unconnected node for retained-transform measurement.');
    globalThis.__unoBenchmarkFixture={state:saved,nodeId:middle.id,x:middle.x,revision:10001};
  },{source:classSource(count),count,scale:configuration.cameraScale});
  return waitForGpuPresentation(page);
}

// A single in-page clock covers the requested operation through exact native
// presentation acknowledgment. Polling overhead is bounded and identical for
// both revisions; these are software-driver wall latencies, not hardware FPS.
async function sample(page, operation, value) {
  const result=await page.locator('.surface').first().evaluate(async (surface,{operation,value})=>{
    const ui=await import(new URL('uml/viewport.js',document.baseURI).href);
    const id=surface.getRootNode().host.id;
    const before=ui.rendererState(id), mark=globalThis.__unoGpuBenchmark.mark();
    const svgBox=()=>{
      const b=surface.querySelector('.paper svg').viewBox.baseVal;
      return {x:b.x,y:b.y,width:b.width,height:b.height};
    };
    const boxBefore=svgBox();
    const started=performance.now();
    if(operation==='camera')ui.zoom(id,value);
    else if(operation==='source'){
      await ui.render(id,value,'benchmark.puml');
      const fixture=globalThis.__unoBenchmarkFixture;
      ui.setCanvasState(id,JSON.stringify(fixture.state),++fixture.revision);
    }else if(operation==='transform'){
      const fixture=globalThis.__unoBenchmarkFixture;
      const saved=structuredClone(fixture.state);
      saved.nodes.find(node=>node.id===fixture.nodeId).x=fixture.x+value;
      ui.setCanvasState(id,JSON.stringify(saved),++fixture.revision);
    }else throw new Error('Unknown benchmark operation '+operation);
    const operationMilliseconds=performance.now()-started;
    let after;
    const deadline=started+60000;
    while(true){
      after=ui.rendererState(id);
      if(after?.backend==='webgpu'&&!after.pending&&after.revision>before.revision&&
        after.presentedRevision===after.revision&&after.statistics?.revision===after.revision)break;
      if(performance.now()>deadline)
        throw new Error('Benchmark presentation deadline: '+operation+' '+JSON.stringify(after));
      await new Promise(resolve=>setTimeout(resolve,10));
    }
    const wallMilliseconds=performance.now()-started;
    return {operation,wallMilliseconds,operationMilliseconds,before,after,
      boxBefore,boxAfter:svgBox(),observer:globalThis.__unoGpuBenchmark.snapshot(mark)};
  },{operation,value});
  accepted(result.before); accepted(result.after);
  validateObserver(result.observer);
  result.deltas=counters(result.before.statistics,result.after.statistics);
  if(operation==='camera'){
    for(const field of retainedCamera)
      assert.equal(result.after.statistics[field],result.before.statistics[field],
        'Camera requests must retain '+field);
    assert.ok(result.deltas.cameraUploadBytes>0);
  }
  if(operation==='transform'){
    assert.deepEqual(result.boxAfter,result.boxBefore,'Interior transforms must preserve the world viewport.');
    for(const field of ['geometryUploadBytes','textureUploadBytes'])
      assert.equal(result.after.statistics[field],result.before.statistics[field],
        'Transform-only authoring must retain '+field);
  }
  return result;
}

async function runWorkload(browser, url, side, pass, workload) {
  const directory=resolve(output,String(pass)+'-'+side+'-'+workload);
  await mkdir(directory,{recursive:true});
  process.env.SCREENSHOT_DIR=directory;
  const context=await browser.newContext({
    viewport:configuration.viewport,deviceScaleFactor:configuration.deviceScaleFactor,
    colorScheme:configuration.colorScheme,locale:configuration.locale,timezoneId:configuration.timezoneId
  });
  await context.addInitScript(installGpuObserver);
  const page=await context.newPage();
  page.setDefaultTimeout(35000);
  const consoleLines=[],errors=[];
  page.on('console',message=>{if(consoleLines.length<3000)consoleLines.push(message.type()+': '+message.text());});
  page.on('pageerror',error=>errors.push(error.message));
  const report={side,pass,workload,sha:side==='baseline'?baselineSha:candidateSha,
    directory,phases:{},warmups:{}};
  try{
    await page.goto(url,{waitUntil:'domcontentloaded'});
    await page.locator('.paper svg').first().waitFor({state:'visible',timeout:120000});
    await waitForGpuPresentation(page);
    const count=workload==='starter'?0:Number(workload.split('-')[1]);
    if(count)await setupClasses(page,count);
    else{
      const previous=await waitForGpuPresentation(page);
      const changed=await page.locator('.surface').first().evaluate(async (surface,scale)=>{
        const ui=await import(new URL('uml/viewport.js',document.baseURI).href);
        const id=surface.getRootNode().host.id;
        const current=new DOMMatrixReadOnly(getComputedStyle(surface.querySelector('.paper')).transform).a;
        if(Math.abs(current-scale)<=1e-9*Math.max(1,current,scale))return false;
        ui.zoom(id,scale/current);
        return true;
      },configuration.cameraScale);
      // A camera already at the requested scale deliberately emits no new
      // transaction. Accept its current frame rather than wait for a nonexistent revision.
      await waitForGpuPresentation(page,changed?previous.revision:-1);
    }
    report.initial=await waitForGpuPresentation(page);
    accepted(report.initial);
    report.environment=await page.evaluate(()=>({
      userAgent:navigator.userAgent,devicePixelRatio,
      observer:globalThis.__unoGpuBenchmark.snapshot(),
      fixture:globalThis.__unoBenchmarkFixture ? {
        nodeId:globalThis.__unoBenchmarkFixture.nodeId,
        nodes:globalThis.__unoBenchmarkFixture.state.nodes.length
      } : null
    }));
    validateObserver(report.environment.observer);
    assert.ok(report.environment.observer.requests.some(request=>request.deviceId),
      'The observer must identify a device requested by the application itself.');
    // Preserve the exact semantic/camera/font inputs before the reference
    // capture, without changing the scene or adding another rendered frame.
    const fixtureSnapshot=await page.locator('.surface').first().evaluate(surface=>{
      const paper=surface.querySelector('.paper'),svg=paper.querySelector('svg');
      const box=svg.viewBox.baseVal,rect=surface.getBoundingClientRect();
      const matrix=value=>value?[value.a,value.b,value.c,value.d,value.e,value.f]:null;
      return {
        svg:svg.outerHTML,viewBox:{x:box.x,y:box.y,width:box.width,height:box.height},
        paperMatrix:matrix(new DOMMatrixReadOnly(getComputedStyle(paper).transform)),
        svgScreenMatrix:matrix(svg.getScreenCTM()),
        surface:{x:rect.x,y:rect.y,width:rect.width,height:rect.height},
        fonts:{status:document.fonts?.status??null,
          faces:document.fonts?[...document.fonts].map(font=>({
            family:font.family,style:font.style,weight:font.weight,status:font.status
          })):[]}
      };
    });
    report.fixtureSnapshot='initial-fixture.json';
    await writeFile(resolve(directory,report.fixtureSnapshot),JSON.stringify(fixtureSnapshot,null,2));
    report.quality=await compareGpuPaint(page,'initial');
    for(let index=0;index<configuration.cameraWarmups+configuration.cameraSamples;index++){
      const result=await sample(page,'camera',index%2?1/1.002:1.002);
      const key=index<configuration.cameraWarmups?'warmups':'phases';
      (report[key].camera??=[]).push(result);
    }
    if(count){
      for(let index=0;index<configuration.editWarmups+configuration.editSamples;index++){
        const result=await sample(page,'source',classSource(count,index+1));
        const key=index<configuration.editWarmups?'warmups':'phases';
        (report[key].source??=[]).push(result);
      }
      // Restore a common source and normalize every saved position before the
      // transform workload. Preparation and restoration are excluded from samples.
      await sample(page,'source',classSource(count));
      for(let index=0;index<configuration.transformWarmups+configuration.transformSamples;index++){
        const result=await sample(page,'transform',index%2?0:6);
        const key=index<configuration.transformWarmups?'warmups':'phases';
        (report[key].transform??=[]).push(result);
      }
    }else{
      // Real pointer selection and Escape exercise the actual shared-device
      // overlay path, with unchanged source/history and negative pixel controls.
      report.overlay=await verifySelectionOverlay(page);
    }
    report.final=await waitForGpuPresentation(page);
    accepted(report.final);
    report.finalQuality=await compareGpuPaint(page,'final');
    report.observer=await page.evaluate(()=>globalThis.__unoGpuBenchmark.snapshot());
    validateObserver(report.observer);
    assert.deepEqual(errors,[]);
    report.summary=Object.fromEntries(Object.entries(report.phases)
      .map(([name,samples])=>[name,phaseSummary(samples)]));
    report.status='passed';
    console.log('GPU_BENCHMARK_WORKLOAD '+JSON.stringify({
      side,pass,workload,sha:report.sha,status:report.status,
      devices:report.environment.observer.requests,
      initial:resourceSummary(report.initial),final:resourceSummary(report.final),
      quality:report.quality,finalQuality:report.finalQuality,phases:report.summary,
      overlay:report.overlay?{quality:report.overlay.quality,retention:report.overlay.retention,
        selected:resourceSummary(report.overlay.selected),cleared:resourceSummary(report.overlay.cleared)}:null
    }));
  }catch(error){
    report.status='failed';
    report.error={name:error.name,message:error.message,stack:error.stack};
    throw Object.assign(error,{benchmarkReport:report});
  }finally{
    await writeFile(resolve(directory,'report.json'),JSON.stringify(report,null,2));
    await writeFile(resolve(directory,'console.txt'),consoleLines.join('\n'));
    await writeFile(resolve(directory,'browser-errors.json'),JSON.stringify(errors,null,2));
    await page.screenshot({path:resolve(directory,'workspace.png')}).catch(()=>{});
    await context.close();
  }
  return report;
}

async function pairedPixels(page, first, second) {
  const [left,right]=await Promise.all([readFile(first),readFile(second)]);
  return page.evaluate(async([a,b])=>{
    async function decode(bytes){
      const bitmap=await createImageBitmap(new Blob([new Uint8Array(bytes)],{type:'image/png'}));
      const canvas=new OffscreenCanvas(bitmap.width,bitmap.height);
      const context=canvas.getContext('2d',{willReadFrequently:true});
      context.drawImage(bitmap,0,0);
      const data=context.getImageData(0,0,bitmap.width,bitmap.height);
      bitmap.close();return data;
    }
    const aImage=await decode(a),bImage=await decode(b);
    if(aImage.width!==bImage.width||aImage.height!==bImage.height)
      throw new Error('Paired screenshot dimensions changed.');
    let absolute=0,changed=0,major=0,maximum=0;
    for(let index=0;index<aImage.data.length;index+=4){
      let distance=0;
      for(let channel=0;channel<3;channel++){
        const delta=Math.abs(aImage.data[index+channel]-bImage.data[index+channel]);
        absolute+=delta;distance=Math.max(distance,delta);
      }
      maximum=Math.max(maximum,distance);if(distance)changed++;if(distance>64)major++;
    }
    const pixels=aImage.width*aImage.height;
    return {width:aImage.width,height:aImage.height,meanAbsoluteChannelError:absolute/(pixels*3),
      maxChannelDifference:maximum,changedFraction:changed/pixels,majorDifferenceFraction:major/pixels};
  },[Array.from(left),Array.from(right)]);
}

const report={
  schema:1,baselineSha,candidateSha,configuration,startedAt:new Date().toISOString(),
  runtime:{node:process.version,platform:process.platform,architecture:process.arch,
    runnerImage:process.env.ImageOS??null,runnerImageVersion:process.env.ImageVersion??null},
  interpretation:{
    wall:'Operation to validated native acknowledgment, including timer polling, WebAssembly, shared-device scope completion and dispatcher latency.',
    operation:'Synchronous/asynchronous public viewport operation, including native source parsing/layout on uncached edits; excludes later presentation.',
    encode:'Native CPU command encoding for the acknowledged frame; not GPU execution time.',
    compilation:'Elapsed wall time for the last accepted scene compilation, including awaited raster work but excluding the preceding document.fonts.ready wait; not a sum of intermediate/canceled work. Camera samples report null instead of stale compilation times.',
    scopes:'Passive original-promise popErrorScope calls started within each sample on the application shared device; can also include Uno UI scopes. Durations include only promises completed by the acknowledgment snapshot; observed/completed/pending counts expose that censoring. Pending promises have no measured completion duration.',
    rasterCache:'Per-operation differences of the lifetime hit/miss counters in before/after compiler snapshots. Camera distributions remain null. Raw lifetime snapshots are preserved.',
    nativeValidation:'Candidate-only native callback/dispatcher fields are additive diagnostics; absent baseline fields stay null.',
    source:'Repeated new same-length label values through the public native viewport renderer and saved canvas projection; not editor keystroke latency.',
    transform:'Public saved-state authoring of one interior unconnected node with unchanged world bounds.',
    limits:'Small warmed ABBA samples on one runner. Software-driver latency is not hardware FPS. No timing pass/fail gate.'
  },
  runs:[],pairs:[],failures:[]
};
const servers={};
let browser;
try{
  servers.baseline=await servePublishedSite(baselineDirectory);
  servers.candidate=await servePublishedSite(candidateDirectory);
  browser=await chromium.launch(configuration.launch);
  report.browserVersion=browser.version();
  for(let pass=0;pass<configuration.order.length;pass++){
    const side=configuration.order[pass];
    const url='http://127.0.0.1:'+servers[side].address().port+publishedPrefix;
    for(const workload of configuration.workloads){
      console.log('GPU benchmark',pass,side,workload,side==='baseline'?baselineSha:candidateSha);
      try{report.runs.push(await runWorkload(browser,url,side,pass,workload));}
      catch(error){
        if(error.benchmarkReport)report.runs.push(error.benchmarkReport);
        report.failures.push({pass,side,workload,message:error.message});
        console.error('Benchmark workload failed:',error);
      }
      await writeFile(resolve(output,'gpu-performance.json'),JSON.stringify(report,null,2));
    }
  }
  const comparisonPage=await browser.newPage();
  for(const [baselinePass,candidatePass] of [[0,1],[3,2]]){
    for(const workload of configuration.workloads){
      const baseline=report.runs.find(run=>run.pass===baselinePass&&run.workload===workload);
      const candidate=report.runs.find(run=>run.pass===candidatePass&&run.workload===workload);
      if(baseline?.status!=='passed'||candidate?.status!=='passed')continue;
      const pair={baselinePass,candidatePass,workload,phases:{}};
      try{
        pair.referencePixels=await pairedPixels(comparisonPage,
          resolve(baseline.directory,'initial-svg.png'),resolve(candidate.directory,'initial-svg.png'));
        pair.gpuPixels=await pairedPixels(comparisonPage,
          resolve(baseline.directory,'initial-webgpu.png'),resolve(candidate.directory,'initial-webgpu.png'));
        assert.equal(pair.referencePixels.maxChannelDifference,0,
          'Baseline/candidate must have identical SVG references and cameras.');
        assert.ok(pair.gpuPixels.maxChannelDifference<=1,
          'Baseline/candidate GPU paint may differ by at most one channel quantization unit.');
        for(const phase of Object.keys(baseline.phases)){
          const before=baseline.summary[phase],after=candidate.summary[phase];
          pair.phases[phase]={baseline:before,candidate:after,
            candidateToBaselineWallMedian:after.wallMilliseconds.median/before.wallMilliseconds.median,
            candidateToBaselineEncodeMedian:before.cpuEncodeMilliseconds.median
              ?after.cpuEncodeMilliseconds.median/before.cpuEncodeMilliseconds.median:null};
        }
        pair.status='passed';
      }catch(error){
        pair.status='failed';pair.error=error.message;
        report.failures.push({baselinePass,candidatePass,workload,message:error.message});
      }
      report.pairs.push(pair);
      console.log('GPU_BENCHMARK_PAIR '+JSON.stringify({
        baselinePass,candidatePass,workload,status:pair.status,error:pair.error??null,
        referencePixels:pair.referencePixels,gpuPixels:pair.gpuPixels,
        phases:Object.fromEntries(Object.entries(pair.phases).map(([phase,value])=>[phase,{
          baselineWallMedian:value.baseline.wallMilliseconds.median,
          candidateWallMedian:value.candidate.wallMilliseconds.median,
          candidateToBaselineWallMedian:value.candidateToBaselineWallMedian,
          baselineEncodeMedian:value.baseline.cpuEncodeMilliseconds.median,
          candidateEncodeMedian:value.candidate.cpuEncodeMilliseconds.median,
          candidateToBaselineEncodeMedian:value.candidateToBaselineEncodeMedian
        }]))
      }));
    }
  }
  await comparisonPage.close();
}catch(error){
  report.failures.push({stage:'setup-or-runner',message:error.message,stack:error.stack});
}finally{
  report.completedAt=new Date().toISOString();
  report.status=report.failures.length?'failed':'passed';
  await writeFile(resolve(output,'gpu-performance.json'),JSON.stringify(report,null,2));
  const rows=['# Paired shared-device WebGPU measurements','',
    'Baseline: '+baselineSha,'Candidate: '+candidateSha,'',
    'Order: baseline → candidate → candidate → baseline. All frames require 4× MSAA and the existing pixel/error/resource gates.',
    'Software-driver wall latency is not hardware FPS. Ratios below 1 indicate lower measured latency; small samples are descriptive.','',
    '| Pair | Workload | Phase | Baseline wall median (ms) | Candidate wall median (ms) | Ratio | Baseline encode median (ms) | Candidate encode median (ms) |',
    '| --- | --- | --- | ---: | ---: | ---: | ---: | ---: |'];
  const number=value=>Number.isFinite(value)?value.toFixed(3):'—';
  for(const pair of report.pairs)for(const [phase,value]of Object.entries(pair.phases))
    rows.push('| '+pair.baselinePass+'/'+pair.candidatePass+' | '+pair.workload+' | '+phase+' | '+
      number(value.baseline.wallMilliseconds.median)+' | '+number(value.candidate.wallMilliseconds.median)+' | '+
      number(value.candidateToBaselineWallMedian)+' | '+number(value.baseline.cpuEncodeMilliseconds.median)+' | '+
      number(value.candidate.cpuEncodeMilliseconds.median)+' |');
  rows.push('','Status: **'+report.status+'**. Raw samples, actual device observations, scopes, uploads, draw counts and paired images are in the artifact.');
  for(const failure of report.failures)rows.push('- '+JSON.stringify(failure));
  const markdown=rows.join('\n')+'\n';
  await writeFile(resolve(output,'summary.md'),markdown);
  if(process.env.GITHUB_STEP_SUMMARY)await appendFile(process.env.GITHUB_STEP_SUMMARY,markdown);
  console.log(markdown);
  console.log('GPU_BENCHMARK_RESULT '+JSON.stringify({
    status:report.status,baselineSha,candidateSha,browserVersion:report.browserVersion,
    runtime:report.runtime,configuration,startedAt:report.startedAt,completedAt:report.completedAt,
    workloadsCompleted:report.runs.filter(run=>run.status==='passed').length,
    pairsCompleted:report.pairs.filter(pair=>pair.status==='passed').length,failures:report.failures
  }));
  await browser?.close();
  for(const server of Object.values(servers))await new Promise(resolve=>server.close(resolve));
}
if(report.failures.length)throw new Error('Paired WebGPU benchmark failed '+report.failures.length+' checks; inspect gpu-performance.json.');
