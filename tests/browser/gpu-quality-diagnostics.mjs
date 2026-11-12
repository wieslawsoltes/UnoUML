import assert from 'node:assert/strict';
import { appendFile, mkdir, readFile, writeFile } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import { resolve } from 'node:path';
import { chromium } from 'playwright';
import { graphicsLaunchOptions } from './graphics-options.mjs';
import { waitForGpuPresentation, compareGpuPaint } from './gpu-presentation.mjs';

// Diagnostics only: the application, shared quality gates and paired benchmark
// are unchanged. No benchmark-owned GPU device or rendering path is created.
const applicationUrl=process.env.UNOUML_DIAGNOSTIC_URL||'https://wieslawsoltes.github.io/UnoUML/';
const deployedSha='b25c990167b13db19dca318ca2f25420085e3518';
const harnessSha='d304eab058143c49f8274a1cd98da5a1f4e41771';
const sceneHelpersSha='c119325f44af7b01a6a9d6dd1a0d892bd0684e0b';
const output=resolve(process.env.SCREENSHOT_DIR||'gpu-quality-diagnostics');
const configuration={
  applicationUrl,deployedSha,harnessSha,sceneHelpersSha,
  viewport:{width:1390,height:860},deviceScaleFactor:1,
  colorScheme:'light',locale:'en-US',timezoneId:'UTC',sampleCount:4,
  scales:[.9,1,1.25,1.5,1.65],
  driver:process.env.UNOUML_GPU_DRIVER||'software',launch:graphicsLaunchOptions(true),
  gates:'Unmodified compareGpuPaint export: reference ink >100, GPU ink strictly between 0.9× and 1.1× reference, MAE<3, major fraction<0.015.'
};
assert.equal(process.env.UNOUML_REQUIRE_WEBGPU,'1');
await mkdir(output,{recursive:true});
const report={
  schema:1,configuration,startedAt:new Date().toISOString(),cases:[],repetitions:[],failures:[],
  interpretation:{
    quality:'Every comparison invokes the unchanged shared quality helper. Failed assertions remain failures; later cases still execute to collect the full scale sweep.',
    histograms:'Independent minimum-RGB pixel distributions and cumulative ink counts. They characterize the existing threshold without replacing it.',
    coverage:'For a visible axis-aligned opaque rectangle top edge, sampled RGB is projected onto the segment between the live CSS stroke color and either the exterior background or interior fill. The grid/background texture can add residuals, so this is a diagnostic profile, not a new quality criterion.',
    resolution:'DPR1, native 4×MSAA, output dimensions, quality bucket, exact camera and local/screen matrices are recorded separately.',
    claims:'No timing comparison, FPS claim, combined score, threshold relaxation or production change.'
  }
};
const pngs=[];
const sha256=bytes=>createHash('sha256').update(bytes).digest('hex');

async function verifyDeployedSource(){
  const files=['uml/viewport.js','uml/gpu/scene.js','uml/gpu/resources.js'];
  const rows=await Promise.all(files.map(async path=>{
    const actualUrl=new URL(path,applicationUrl).href;
    const expectedUrl='https://raw.githubusercontent.com/wieslawsoltes/UnoUML/'+deployedSha+'/src/UnoUML.App/wwwroot/'+path;
    const [actualResponse,expectedResponse]=await Promise.all([fetch(actualUrl,{cache:'no-store'}),fetch(expectedUrl)]);
    assert.ok(actualResponse.ok&&expectedResponse.ok,'Unable to verify deployed '+path);
    const actual=Buffer.from(await actualResponse.arrayBuffer()),expected=Buffer.from(await expectedResponse.arrayBuffer());
    const row={path,actualUrl,sha256:sha256(actual),expectedSha256:sha256(expected),
      etag:actualResponse.headers.get('etag'),lastModified:actualResponse.headers.get('last-modified')};
    assert.equal(row.sha256,row.expectedSha256,'Deployed '+path+' is not the immutable baseline');
    return row;
  }));
  return rows;
}

function assertAccepted(state){
  assert.equal(state.backend,'webgpu');
  assert.equal(state.pending,false);
  assert.equal(state.presentedRevision,state.revision);
  assert.equal(state.statistics.revision,state.revision);
  assert.equal(state.statistics.sampleCount,4);
  assert.equal(state.statistics.backend,'Uno shared WebGPU');
  assert.ok(state.statistics.drawCalls>0&&state.statistics.visibleCommands>0);
}

async function setScale(page,scale){
  const change=await page.locator('.surface').first().evaluate(async(surface,scale)=>{
    await document.fonts.ready;
    const ui=await import(new URL('uml/viewport.js',document.baseURI).href),id=surface.getRootNode().host.id;
    const prior=ui.rendererState(id),matrix=new DOMMatrixReadOnly(getComputedStyle(surface.querySelector('.paper')).transform);
    if(Math.abs(matrix.b)>1e-12||Math.abs(matrix.c)>1e-12||Math.abs(matrix.a-matrix.d)>1e-12)
      throw new Error('Expected the benchmark uniform camera');
    if(Math.abs(matrix.a-scale)<=1e-12)return{changed:false,revision:prior.revision};
    ui.zoom(id,scale/matrix.a);
    return{changed:true,revision:prior.revision};
  },scale);
  const accepted=await waitForGpuPresentation(page,change.changed?change.revision:-1);
  assertAccepted(accepted);
  await page.evaluate(()=>document.fonts.ready);
  return accepted;
}

async function sceneSnapshot(page){
  return page.locator('.surface').first().evaluate(async surface=>{
    const ui=await import(new URL('uml/viewport.js',document.baseURI).href),id=surface.getRootNode().host.id;
    const svg=surface.querySelector('.paper>svg'),paper=svg.parentElement,rect=surface.getBoundingClientRect();
    // Measure the actual reference background synchronously: accepted GPU paint
    // intentionally makes the surface transparent. Restore before any paint/task.
    const hadGpuPaint=surface.classList.contains('gpu-presented');
    if(hadGpuPaint)surface.classList.remove('gpu-presented');
    const referenceBackground={color:getComputedStyle(surface).backgroundColor,image:getComputedStyle(surface).backgroundImage};
    if(hadGpuPaint)surface.classList.add('gpu-presented');
    const matrix=value=>value?[value.a,value.b,value.c,value.d,value.e,value.f]:null;
    const box=value=>value?{x:value.x,y:value.y,width:value.width,height:value.height}:null;
    const bbox=element=>{try{return box(element.getBBox());}catch{return null;}};
    const color=value=>{
      if(!/^rgba?\(/.test(value))return null;
      const values=value.match(/[\d.]+/g)?.map(Number);
      return values?.length>=3&&(values.length===3||values[3]===1)?values.slice(0,3):null;
    };
    const compositing=element=>Object.fromEntries(['will-change','transform','image-rendering','backface-visibility','filter','opacity','isolation','contain'].map(name=>[name,getComputedStyle(element).getPropertyValue(name)]));
    const geometryNames=['x','y','x1','x2','y1','y2','width','height','rx','ry','cx','cy','r','d','points','transform'];
    const shapes=[...svg.querySelectorAll('path,rect,line,polyline,polygon,circle,ellipse')];
    const strokes=[],edges=[];
    for(let index=0;index<shapes.length;index++){
      const element=shapes[index],style=getComputedStyle(element);
      if(style.stroke==='none'||Number(style.strokeOpacity)===0||style.display==='none')continue;
      const screen=element.getScreenCTM(),local=element.getCTM(),bounds=bbox(element);
      const attributes=Object.fromEntries(geometryNames.filter(name=>element.hasAttribute(name)).map(name=>[name,element.getAttribute(name)]));
      const row={index,tag:element.localName,id:element.id,owner:element.closest('[data-canvas-node],[data-uml-hit]')?.getAttribute('data-canvas-node')||element.closest('[data-uml-hit]')?.getAttribute('data-uml-hit')||null,
        attributes,bounds,ctm:matrix(local),screenCtm:matrix(screen),screenBounds:box(element.getBoundingClientRect()),
        style:Object.fromEntries(['stroke','strokeWidth','strokeOpacity','strokeLinecap','strokeLinejoin','strokeMiterlimit','strokeDasharray','strokeDashoffset',
          'vectorEffect','fill','fillOpacity','opacity','visibility','display','transform','transformOrigin','transformBox','filter','clipPath'].map(name=>[name,style[name]])),
        computedGeometry:style.getPropertyValue('d')};
      strokes.push(row);
      if(element.localName!=='rect'||!bounds||!screen||Math.abs(screen.b)>1e-9||Math.abs(screen.c)>1e-9||screen.a<=0||screen.d<=0)continue;
      const fill=color(style.fill),stroke=color(style.stroke),background=color(referenceBackground.color);
      if(!fill||!stroke||!background||Number(style.opacity)!==1||Number(style.strokeOpacity)!==1||Number(style.fillOpacity)!==1)continue;
      const x=screen.a*(bounds.x+bounds.width/2)+screen.e-rect.left;
      const y=screen.d*bounds.y+screen.f-rect.top;
      if(bounds.width*screen.a<34||x<20||x>rect.width-20||y<10||y>rect.height-10)continue;
      edges.push({shapeIndex:index,owner:row.owner,x,y,fill,stroke,background,strokeWidth:style.strokeWidth,vectorEffect:style.vectorEffect,
        rect:bounds,screenCtm:row.screenCtm,geometry:attributes,
        distance:(x-rect.width/2)**2+(y-rect.height/2)**2});
    }
    edges.sort((a,b)=>a.distance-b.distance);
    const texts=[...svg.querySelectorAll('text')].map(element=>{
      const style=getComputedStyle(element);
      return{text:element.textContent,bounds:bbox(element),screenCtm:matrix(element.getScreenCTM()),
        fontFamily:style.fontFamily,fontSize:style.fontSize,fontWeight:style.fontWeight,fontStyle:style.fontStyle,
        letterSpacing:style.letterSpacing,wordSpacing:style.wordSpacing};
    });
    return{
      renderer:ui.rendererState(id),devicePixelRatio,window:{width:innerWidth,height:innerHeight},
      surface:{bounds:box(rect),backgroundColor:getComputedStyle(surface).backgroundColor,backgroundImage:getComputedStyle(surface).backgroundImage,
        classes:[...surface.classList],referenceBackground,scrollLeft:surface.scrollLeft,scrollTop:surface.scrollTop},
      paper:{cssTransform:getComputedStyle(paper).transform,bounds:box(paper.getBoundingClientRect()),compositing:compositing(paper)},
      svg:{viewBox:svg.getAttribute('viewBox'),width:svg.getAttribute('width'),height:svg.getAttribute('height'),
        bounds:bbox(svg),screenBounds:box(svg.getBoundingClientRect()),ctm:matrix(svg.getCTM()),screenCtm:matrix(svg.getScreenCTM()),overflow:getComputedStyle(svg).overflow,compositing:compositing(svg)},
      fonts:{status:document.fonts.status,faces:[...document.fonts].map(face=>({family:face.family,status:face.status,weight:face.weight,style:face.style}))},
      shapeCount:shapes.length,strokes,texts,edge:edges[0]||null
    };
  });
}

function assertStable(before,after){
  for(const state of [before,after])assertAccepted(state.renderer);
  assert.equal(after.renderer.revision,before.renderer.revision,'Capture changed the acknowledged revision');
  for(const name of ['generation','sourceVersion','viewVersion'])
    assert.equal(after.renderer.compilation?.[name],before.renderer.compilation?.[name],'Capture changed '+name);
  assert.deepEqual(after.svg,before.svg,'Capture changed SVG geometry or camera');
  assert.deepEqual(after.paper,before.paper,'Capture changed the paper camera');
  assert.deepEqual(after.strokes,before.strokes,'Capture changed live stroke geometry/styles');
  assert.deepEqual(after.texts,before.texts,'Capture changed text geometry/styles');
  assert.equal(after.devicePixelRatio,1);
}

async function characterize(page,gpuBytes,svgBytes,edge){
  return page.evaluate(async({gpuBytes,svgBytes,edge})=>{
    async function decode(bytes){
      const bitmap=await createImageBitmap(new Blob([new Uint8Array(bytes)],{type:'image/png'}));
      const canvas=new OffscreenCanvas(bitmap.width,bitmap.height),context=canvas.getContext('2d',{willReadFrequently:true});
      context.drawImage(bitmap,0,0);const data=context.getImageData(0,0,canvas.width,canvas.height);bitmap.close();
      return{canvas,data};
    }
    const gpu=await decode(gpuBytes),svg=await decode(svgBytes);
    if(gpu.data.width!==svg.data.width||gpu.data.height!==svg.data.height)throw new Error('Diagnostic pixel dimensions changed');
    function distribution(image){
      const histogram=Array(256).fill(0);let darkness=0;
      for(let index=0;index<image.data.length;index+=4){
        const minimum=Math.min(image.data[index],image.data[index+1],image.data[index+2]);
        histogram[minimum]++;darkness+=Math.max(0,240-minimum)/240;
      }
      const thresholds=[32,64,96,128,150,176,192,208,224,240,248];
      return{minimumRgbHistogram:histogram,ink:thresholds.map(threshold=>({threshold,count:histogram.slice(0,threshold).reduce((sum,value)=>sum+value,0)})),
        integratedDarknessBelow240:darkness};
    }
    const result={width:gpu.data.width,height:gpu.data.height,gpu:distribution(gpu.data),svg:distribution(svg.data),edge:null,crop:null};
    if(edge){
      const halfWidth=10,radius=8,column=Math.floor(edge.x),row=Math.floor(edge.y);
      function profile(image){
        const rows=[];let integratedCoverage=0,integratedUnclampedCoverage=0;
        for(let offset=-radius;offset<=radius;offset++){
          const y=row+offset;if(y<0||y>=image.height)continue;
          const base=y+.5<edge.y?edge.background:edge.fill;
          const direction=base.map((value,index)=>value-edge.stroke[index]);
          const denominator=direction.reduce((sum,value)=>sum+value*value,0);
          if(denominator<1)throw new Error('Stroke profile has no color separation');
          const samples=[];
          for(let dx=-halfWidth;dx<=halfWidth;dx++){
            const x=column+dx;if(x<0||x>=image.width)continue;
            const at=(y*image.width+x)*4,rgb=Array.from(image.data.slice(at,at+3));
            const raw=rgb.reduce((sum,value,index)=>sum+(base[index]-value)*direction[index],0)/denominator;
            samples.push({x,rgb,coverage:Math.max(0,Math.min(1,raw)),unclampedCoverage:raw});
          }
          const meanRgb=[0,1,2].map(channel=>samples.reduce((sum,sample)=>sum+sample.rgb[channel],0)/samples.length);
          const coverage=samples.reduce((sum,sample)=>sum+sample.coverage,0)/samples.length;
          const raw=samples.reduce((sum,sample)=>sum+sample.unclampedCoverage,0)/samples.length;
          integratedCoverage+=coverage;integratedUnclampedCoverage+=raw;
          rows.push({y,offset,relativePixelCenter:y+.5-edge.y,base,meanRgb,coverage,unclampedCoverage:raw,samples});
        }
        return{rows,integratedCoverage,integratedUnclampedCoverage};
      }
      const width=144,height=80,left=Math.max(0,Math.min(gpu.data.width-width,Math.floor(edge.x-width/2)));
      const top=Math.max(0,Math.min(gpu.data.height-height,Math.floor(edge.y-20)));
      const crop=new OffscreenCanvas(width*2,height),ctx=crop.getContext('2d');
      ctx.drawImage(gpu.canvas,left,top,width,height,0,0,width,height);
      ctx.drawImage(svg.canvas,left,top,width,height,width,0,width,height);
      const png=new Uint8Array(await(await crop.convertToBlob({type:'image/png'})).arrayBuffer());
      result.edge={definition:edge,pixelWindow:{centerColumn:column,centerRow:row,halfWidth,radius},
        gpu:profile(gpu.data),svg:profile(svg.data)};
      result.crop={left,top,width,height,panelOrder:['GPU','SVG'],bytes:Array.from(png)};
    }
    return result;
  },{gpuBytes:Array.from(gpuBytes),svgBytes:Array.from(svgBytes),edge});
}

async function pixelDifference(page,first,second){
  return page.evaluate(async([first,second])=>{
    async function pixels(bytes){
      const image=await createImageBitmap(new Blob([new Uint8Array(bytes)],{type:'image/png'}));
      const canvas=new OffscreenCanvas(image.width,image.height),ctx=canvas.getContext('2d',{willReadFrequently:true});ctx.drawImage(image,0,0);
      image.close();return ctx.getImageData(0,0,canvas.width,canvas.height);
    }
    const a=await pixels(first),b=await pixels(second);
    if(a.width!==b.width||a.height!==b.height)throw new Error('Repetition dimensions changed');
    let changedPixels=0,changedChannels=0,absolute=0,maximum=0;
    for(let index=0;index<a.data.length;index+=4){
      let pixelChanged=false;
      for(let channel=0;channel<3;channel++){
        const delta=Math.abs(a.data[index+channel]-b.data[index+channel]);absolute+=delta;maximum=Math.max(maximum,delta);
        if(delta){changedChannels++;pixelChanged=true;}
      }
      if(pixelChanged)changedPixels++;
    }
    return{width:a.width,height:a.height,changedPixels,changedChannels,maxChannelDifference:maximum,meanAbsoluteChannelError:absolute/(a.width*a.height*3)};
  },[Array.from(first),Array.from(second)]);
}

async function capture(page,workload,scale,label){
  const name=workload+'-'+label,directory=resolve(output,name);
  await mkdir(directory,{recursive:true});process.env.SCREENSHOT_DIR=directory;
  const row={workload,scale,label,directory,status:'pending'};
  report.cases.push(row);
  try{
    await setScale(page,scale);
    row.before=await sceneSnapshot(page);
    assertAccepted(row.before.renderer);
    assert.equal(row.before.devicePixelRatio,1);
    try{row.quality=await compareGpuPaint(page,'paint');row.qualityPassed=true;}
    catch(error){
      row.qualityError={name:error.name,message:error.message};
      row.qualityPassed=false;
      row.quality=JSON.parse(await readFile(resolve(directory,'paint-quality.json'),'utf8'));
      report.failures.push({kind:'quality',workload,scale,label,message:error.message});
    }
    row.after=await sceneSnapshot(page);assertStable(row.before,row.after);
    const [gpu,svg]=await Promise.all([readFile(resolve(directory,'paint-webgpu.png')),readFile(resolve(directory,'paint-svg.png'))]);
    row.png={gpu:{bytes:gpu.length,sha256:sha256(gpu)},svg:{bytes:svg.length,sha256:sha256(svg)}};
    const details=await characterize(page,gpu,svg,row.before.edge);
    if(details.crop){
      const bytes=Buffer.from(details.crop.bytes);delete details.crop.bytes;
      await writeFile(resolve(directory,'edge-gpu-svg.png'),bytes);pngs.push({workload,scale,label,bytes});
      details.crop.pngBytes=bytes.length;details.crop.sha256=sha256(bytes);
    }
    row.characterization=details;
    row.inkRatio=row.quality.gpuInk/row.quality.referenceInk;
    row.stable=true;row.status=row.qualityPassed?'passed':'quality-failed';
  }catch(error){
    row.status='diagnostic-error';row.error={name:error.name,message:error.message,stack:error.stack};
    report.failures.push({kind:'diagnostic',workload,scale,label,message:error.message});
  }finally{
    await writeFile(resolve(directory,'diagnostic.json'),JSON.stringify(row,null,2));
    await writeFile(resolve(output,'gpu-quality-diagnostics.json'),JSON.stringify(report,null,2));
    console.log('GPU_QUALITY_SCALE '+JSON.stringify({
      workload,scale,label,status:row.status,stable:row.stable??false,error:row.error??row.qualityError??null,
      quality:row.quality??null,inkRatio:row.inkRatio??null,
      resolutionBucket:row.before?.renderer.compiler?.resolutionBucket??null,
      rootCtm:row.before?.svg.ctm,screenCtm:row.before?.svg.screenCtm,
      native:row.before?.renderer.statistics,
      inkHistogram:row.characterization?{gpu:row.characterization.gpu.ink,svg:row.characterization.svg.ink}:null,
      integratedDarknessBelow240:row.characterization?{gpu:row.characterization.gpu.integratedDarknessBelow240,svg:row.characterization.svg.integratedDarknessBelow240}:null,
      edge:row.characterization?.edge?{
        definition:row.characterization.edge.definition,
        gpuCoverage:row.characterization.edge.gpu.integratedCoverage,svgCoverage:row.characterization.edge.svg.integratedCoverage,
        gpuRows:row.characterization.edge.gpu.rows.map(r=>[r.relativePixelCenter,r.meanRgb,r.coverage]),
        svgRows:row.characterization.edge.svg.rows.map(r=>[r.relativePixelCenter,r.meanRgb,r.coverage])}:null
    }));
  }
  return row;
}

// Unmodified workload helpers copied from the immutable paired harness at c119325f44af7b01a6a9d6dd1a0d892bd0684e0b.
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
  await page.locator('.surface').first().evaluate(async (surface,{source,count}) => {
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
    ui.zoom(id,.9/current); // remain within one quality bucket during tiny camera updates
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
  },{source:classSource(count),count});
  return waitForGpuPresentation(page);
}



async function referenceCompositingProbe(page,sourceCase){
  if(!sourceCase?.stable)return;
  const {workload,scale}=sourceCase,directory=resolve(output,workload+'-reference-compositing');
  await mkdir(directory,{recursive:true});
  const result={workload,scale,diagnostic:'Reference-only paper will-change:auto; unchanged transform; original style restored.',
    status:'pending',sharedGateChanged:false};
  report.referenceCompositing??=[];report.referenceCompositing.push(result);
  const surface=page.locator('.surface').first(),before=await sceneSnapshot(page);
  const saved=await surface.evaluate(element=>{
    const paper=element.querySelector('.paper');
    const classes=['gpu-presented','gpu-overlays-presented'].filter(name=>element.classList.contains(name));
    const inline={value:paper.style.getPropertyValue('will-change'),priority:paper.style.getPropertyPriority('will-change')};
    element.classList.remove(...classes);return{classes,inline};
  });
  const settle=()=>page.evaluate(()=>new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve))));
  let defaultBefore,auto,defaultAfter;
  try{
    await settle();defaultBefore=await surface.screenshot({animations:'disabled'});
    result.defaultBefore=await sceneSnapshot(page);
    await surface.evaluate(element=>element.querySelector('.paper').style.setProperty('will-change','auto','important'));
    await settle();auto=await surface.screenshot({animations:'disabled'});
    result.auto=await sceneSnapshot(page);
    await surface.evaluate((element,inline)=>{
      const style=element.querySelector('.paper').style;
      if(inline.value)style.setProperty('will-change',inline.value,inline.priority);else style.removeProperty('will-change');
    },saved.inline);
    await settle();defaultAfter=await surface.screenshot({animations:'disabled'});
    result.defaultAfter=await sceneSnapshot(page);
    for(const snapshot of [result.auto,result.defaultAfter]){
      assert.deepEqual(snapshot.svg.screenCtm,result.defaultBefore.svg.screenCtm,'Reference probe changed SVG screen matrix');
      assert.deepEqual(snapshot.svg.ctm,result.defaultBefore.svg.ctm,'Reference probe changed root matrix');
      assert.equal(snapshot.paper.cssTransform,result.defaultBefore.paper.cssTransform,'Reference probe changed paper transform');
      assert.deepEqual(snapshot.strokes,result.defaultBefore.strokes,'Reference probe changed source stroke geometry');
      assert.deepEqual(snapshot.texts,result.defaultBefore.texts,'Reference probe changed source text geometry');
    }
    await Promise.all([
      writeFile(resolve(directory,'svg-default-before.png'),defaultBefore),
      writeFile(resolve(directory,'svg-will-change-auto.png'),auto),
      writeFile(resolve(directory,'svg-default-restored.png'),defaultAfter)
    ]);
    result.changed=await pixelDifference(page,defaultBefore,auto);
    result.restored=await pixelDifference(page,defaultBefore,defaultAfter);
    const details=await characterize(page,defaultBefore,auto,before.edge);
    result.profiles={defaultReference:details.gpu,autoReference:details.svg,
      edge:details.edge?{definition:details.edge.definition,defaultReference:details.edge.gpu,autoReference:details.edge.svg}:null};
    if(details.crop){
      const bytes=Buffer.from(details.crop.bytes);
      await writeFile(resolve(directory,'edge-default-auto.png'),bytes);
      pngs.push({workload,scale,label:'reference default → auto',bytes,panelOrder:['SVG default','SVG auto']});
    }
    result.status='complete';
  }catch(error){
    result.status='diagnostic-error';result.error={message:error.message,stack:error.stack};
    report.failures.push({kind:'reference-diagnostic',workload,scale,message:error.message});
  }finally{
    await surface.evaluate((element,saved)=>{
      const style=element.querySelector('.paper').style;
      if(saved.inline.value)style.setProperty('will-change',saved.inline.value,saved.inline.priority);else style.removeProperty('will-change');
      element.classList.add(...saved.classes);
    },saved);
    try{assertStable(before,await sceneSnapshot(page));result.restoredSceneStable=true;}
    catch(error){result.restoredSceneStable=false;result.restoreError=error.message;report.failures.push({kind:'reference-restore',workload,scale,message:error.message});}
    await writeFile(resolve(directory,'diagnostic.json'),JSON.stringify(result,null,2));
    console.log('GPU_QUALITY_REFERENCE_COMPOSITING '+JSON.stringify({
      workload,scale,status:result.status,error:result.error??result.restoreError??null,restoredSceneStable:result.restoredSceneStable,
      defaultCompositing:result.defaultBefore?.paper.compositing,autoCompositing:result.auto?.paper.compositing,
      changed:result.changed,restored:result.restored,
      defaultInk:result.profiles?.defaultReference.ink,autoInk:result.profiles?.autoReference.ink,
      edge:result.profiles?.edge?{definition:result.profiles.edge.definition,
        defaultCoverage:result.profiles.edge.defaultReference.integratedCoverage,autoCoverage:result.profiles.edge.autoReference.integratedCoverage}:null
    }));
  }
}

async function compareRepetition(page,first,second){
  if(!first?.stable||!second?.stable)return;
  const result={first:first.label,second:second.label,workload:first.workload,scale:first.scale};
  for(const mode of ['svg','webgpu']){
    const [a,b]=await Promise.all([readFile(resolve(first.directory,'paint-'+mode+'.png')),readFile(resolve(second.directory,'paint-'+mode+'.png'))]);
    result[mode]=await pixelDifference(page,a,b);
  }
  result.sameRootScreenMatrix=JSON.stringify(first.before.svg.screenCtm)===JSON.stringify(second.before.svg.screenCtm);
  result.sameSvgGeometry=JSON.stringify(first.before.strokes)===JSON.stringify(second.before.strokes)&&JSON.stringify(first.before.texts)===JSON.stringify(second.before.texts);
  result.ink={first:{gpu:first.quality.gpuInk,svg:first.quality.referenceInk},second:{gpu:second.quality.gpuInk,svg:second.quality.referenceInk}};
  report.repetitions.push(result);
  console.log('GPU_QUALITY_REPETITION '+JSON.stringify(result));
}

async function makeContactSheet(page){
  if(!pngs.length)return;
  const bytes=await page.evaluate(async rows=>{
    const columns=5,width=288,height=80,labelHeight=28,gap=6;
    const canvas=new OffscreenCanvas(columns*(width+gap)+gap,Math.ceil(rows.length/columns)*(height+labelHeight+gap)+gap),ctx=canvas.getContext('2d');
    ctx.fillStyle='#fff';ctx.fillRect(0,0,canvas.width,canvas.height);ctx.font='12px sans-serif';ctx.textBaseline='top';
    for(let index=0;index<rows.length;index++){
      const row=rows[index],x=gap+(index%columns)*(width+gap),y=gap+Math.floor(index/columns)*(height+labelHeight+gap);
      ctx.fillStyle='#111';ctx.fillText(row.workload+' · '+row.label,x,y);
      const labels=row.panelOrder||['GPU','SVG'];ctx.fillText(labels[0],x,y+14);ctx.fillText(labels[1],x+width/2,y+14);
      const bitmap=await createImageBitmap(new Blob([new Uint8Array(row.bytes)],{type:'image/png'}));
      ctx.drawImage(bitmap,x,y+labelHeight);bitmap.close();
    }
    return Array.from(new Uint8Array(await(await canvas.convertToBlob({type:'image/png'})).arrayBuffer()));
  },pngs.map(row=>({...row,bytes:Array.from(row.bytes)})));
  const png=Buffer.from(bytes);await writeFile(resolve(output,'coverage-edges.png'),png);
  report.contactSheet={bytes:png.length,sha256:sha256(png),panels:pngs.map(({bytes,...row})=>row)};
  if(png.length<=100*1024)console.log('GPU_QUALITY_PNG '+JSON.stringify({name:'coverage-edges.png',...report.contactSheet,base64:png.toString('base64')}));
  else console.log('GPU_QUALITY_PNG '+JSON.stringify({name:'coverage-edges.png',bytes:png.length,omitted:'Exceeds the100KiB log-image bound; full PNG is in the artifact.'}));
}

let browser;
async function openWorkload(workload){
  const context=await browser.newContext({
    viewport:configuration.viewport,deviceScaleFactor:1,colorScheme:'light',locale:'en-US',timezoneId:'UTC'
  });
  const page=await context.newPage();page.setDefaultTimeout(35000);
  const messages=[],errors=[];
  page.on('console',message=>{if(messages.length<3000)messages.push(message.type()+': '+message.text());});
  page.on('pageerror',error=>errors.push(error.message));
  try{
    await page.goto(applicationUrl,{waitUntil:'domcontentloaded'});
    await page.locator('.paper svg').first().waitFor({state:'visible',timeout:120000});
    await waitForGpuPresentation(page);
    if(workload!=='starter')await setupClasses(page,Number(workload.split('-')[1]));
    return{context,page,messages,errors};
  }catch(error){await context.close();throw error;}
}

let original256;
try{
  report.deployedSourceBefore=await verifyDeployedSource();
  browser=await chromium.launch(configuration.launch);report.browserVersion=browser.version();
  for(const workload of ['starter','classes-64','classes-256']){
    const session=await openWorkload(workload);
    try{
      let first;
      for(const scale of configuration.scales){
        const row=await capture(session.page,workload,scale,'scale-'+String(scale).replace('.','p'));
        if(scale===.9){
          first=row;
          if(workload==='classes-256'){
            original256=row;
            const repeated=await capture(session.page,workload,scale,'repeat-immediate-0p9');
            await compareRepetition(session.page,row,repeated);
          }
          // Reference-only CSS experiment is restored before the scale sweep continues.
          await referenceCompositingProbe(session.page,row);
        }
      }
      if(workload==='classes-256'){
        const repeated=await capture(session.page,workload,.9,'repeat-roundtrip-0p9');
        await compareRepetition(session.page,first,repeated);
      }
    }finally{
      const directory=resolve(output,workload);await mkdir(directory,{recursive:true});
      await writeFile(resolve(directory,'console.txt'),session.messages.join('\n'));
      await writeFile(resolve(directory,'browser-errors.json'),JSON.stringify(session.errors,null,2));
      for(const message of session.errors)report.failures.push({kind:'browser',workload,message});
      await session.context.close();
    }
  }
  const fresh=await openWorkload('classes-256');
  try{
    const repeated=await capture(fresh.page,'classes-256',.9,'repeat-fresh-context-0p9');
    await compareRepetition(fresh.page,original256,repeated);
    await makeContactSheet(fresh.page);
    for(const message of fresh.errors)report.failures.push({kind:'browser',workload:'classes-256-fresh-context',message});
  }finally{await fresh.context.close();}
  report.deployedSourceAfter=await verifyDeployedSource();
  assert.deepEqual(report.deployedSourceAfter.map(row=>row.sha256),report.deployedSourceBefore.map(row=>row.sha256),'Deployed sources changed during diagnostics');
}catch(error){report.failures.push({kind:'runner',message:error.message,stack:error.stack});}
finally{
  report.completedAt=new Date().toISOString();
  report.status=report.failures.length?'failed':'passed';
  await writeFile(resolve(output,'gpu-quality-diagnostics.json'),JSON.stringify(report,null,2));
  const number=value=>Number.isFinite(value)?value.toFixed(4):'—';
  const rows=['# Fixed-camera WebGPU/SVG quality diagnostics','',
    'Application: deployed baseline '+deployedSha+'. Shared quality helper: '+harnessSha+'.',
    'DPR1,1390×860 browser viewport, native4×MSAA. Failed hard gates remain failures.','',
    '| Workload | Case | Bucket | GPU ink | SVG ink | Ratio | MAE | Major fraction | Gate |',
    '| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | --- |'];
  for(const row of report.cases)rows.push('| '+row.workload+' | '+row.label+' | '+number(row.before?.renderer.compiler?.resolutionBucket)+' | '+
    (row.quality?.gpuInk??'—')+' | '+(row.quality?.referenceInk??'—')+' | '+number(row.inkRatio)+' | '+
    number(row.quality?.meanAbsoluteChannelError)+' | '+number(row.quality?.majorDifferenceFraction)+' | '+row.status+' |');
  rows.push('','Reference-only will-change:auto experiments are reported separately; they do not replace shared quality comparisons or change application code.',
    'Full matrix/style/geometry snapshots, RGB profiles, histograms, repetition differences, and original PNGs are in the artifact.',
    'Status: **'+report.status+'**.');
  for(const failure of report.failures)rows.push('- '+JSON.stringify(failure));
  const markdown=rows.join('\n')+'\n';await writeFile(resolve(output,'summary.md'),markdown);
  if(process.env.GITHUB_STEP_SUMMARY)await appendFile(process.env.GITHUB_STEP_SUMMARY,markdown);
  console.log(markdown);
  console.log('GPU_QUALITY_RESULT '+JSON.stringify({status:report.status,cases:report.cases.length,failures:report.failures,
    browserVersion:report.browserVersion,configuration,repetitions:report.repetitions}));
  await browser?.close();
}
if(report.failures.length)throw new Error('Quality diagnostics retained '+report.failures.length+' failures; inspect the complete scale report.');
