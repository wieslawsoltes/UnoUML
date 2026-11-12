import assert from 'node:assert/strict';
import {mkdir,writeFile} from 'node:fs/promises';
import {resolve} from 'node:path';

// Verify the production DOM-to-vector overlay producer and its retained-scene
// hand-off. Actual viewport-space WGSL pixels are checked by gpu-shaders.mjs.
export async function verifyGpuOverlays(page,{artifactDir=process.env.SCREENSHOT_DIR}={}) {
  let report;
  try {
    report=await page.evaluate(async()=>{
      const [{CanvasOverlayScene},{GpuPresentationHost},{diffCommands},css]=await Promise.all([
        import(new URL('uml/gpu/overlays.js',document.baseURI)),
        import(new URL('uml/gpu/host.js',document.baseURI)),
        import(new URL('uml/gpu/scene.js',document.baseURI)),
        fetch(new URL('uml/preview.css',document.baseURI)).then(response=>{
          if(!response.ok)throw new Error('Overlay fixture stylesheet failed: '+response.status);
          return response.text();
        })
      ]);
      await document.fonts?.ready;
      const report={cases:[],assertions:0};
      const check=(condition,message)=>{report.assertions++;if(!condition)throw new Error(message);};
      const equal=(actual,expected,message)=>check(JSON.stringify(actual)===JSON.stringify(expected),
        message+'; got '+JSON.stringify(actual)+'; expected '+JSON.stringify(expected));
      const near=(actual,expected,message,epsilon=.06)=>check(actual.length===expected.length&&
        actual.every((value,index)=>Math.abs(value-expected[index])<=epsilon),
        message+'; got '+actual+'; expected '+expected);
      const frame=()=>new Promise(resolve=>requestAnimationFrame(resolve));
      const settle=async()=>{await frame();await frame();};
      async function until(predicate,message) {
        const deadline=performance.now()+8000;
        while(!predicate()){if(performance.now()>=deadline)throw new Error(message);await frame();}
      }
      async function run(name,action) {
        try{const details=await action();report.cases.push({name,pass:true,...details});}
        catch(error){report.cases.push({name,pass:false,error:error.message,stack:error.stack});}
      }
      function fixture({visible=false,text=false}={}) {
        const host=document.createElement('div');
        host.dataset.gpuOverlayFixture='true';
        host.style.cssText='position:fixed;left:'+(visible?'24px':'-12000px')+
          ';top:24px;width:400px;height:260px;z-index:2147483647;';
        const root=host.attachShadow({mode:'open'}),style=document.createElement('style');
        style.textContent=css;
        root.innerHTML='<div class="surface" tabindex="0" data-tool="select">'+
          '<div class="paper"><svg xmlns="http://www.w3.org/2000/svg" width="300" height="220" viewBox="0 0 300 220">'+
          '<rect x="20" y="24" width="72" height="36" fill="#c8d1ef"/>'+
          (text?'<text x="28" y="46" font-family="Arial" font-size="13" fill="#25345b">Retained label</text>':'')+
          '</svg></div>'+
          '<div class="selection" hidden></div><div class="target" hidden></div>'+
          '<svg class="link-guide" hidden><line x1="12" y1="14" x2="100" y2="80"/></svg>'+
          '<div class="canvas-controls"><button type="button" class="canvas-handle" '+
          'data-canvas-handle="resize:se" tabindex="-1" aria-label="Overlay fixture resize" style="left:120px;top:90px"></button></div>'+
          '<div class="canvas-guides"></div><div class="gantt-controls" hidden></div></div>';
        root.prepend(style);document.body.append(host);
        const surface=root.querySelector('.surface'),paper=root.querySelector('.paper'),svg=paper.firstElementChild;
        const handle=root.querySelector('[data-canvas-handle]'),updates=[],events=[];
        const view={host,surface,paper,svg,version:1,z:1,x:0,y:0,marked:null,additional:[],
          selection:root.querySelector('.selection'),target:root.querySelector('.target'),
          guide:root.querySelector('.link-guide'),line:root.querySelector('.link-guide line'),
          rect:()=>surface.getBoundingClientRect(),pick:element=>element?.closest?.('[data-uml-hit]')??null,
          emit:(kind,value)=>{events.push({kind,...value});if(kind==='gpuScene')updates.push(value.update);},
          outline:(box,element)=>{
            box.hidden=!element;if(!element)return;
            const r=element.getBoundingClientRect(),s=surface.getBoundingClientRect();
            Object.assign(box.style,{left:r.left-s.left-3+'px',top:r.top-s.top-3+'px',
              width:r.width+6+'px',height:r.height+6+'px'});
          }};
        let clicks=0;handle.addEventListener('click',()=>clicks++);
        return {host,root,surface,paper,svg,handle,view,updates,events,get clicks(){return clicks;},
          bounds(element=this.handle){const a=element.getBoundingClientRect(),s=surface.getBoundingClientRect();
            return [a.left-s.left,a.top-s.top,a.right-s.left,a.bottom-s.top];},
          close(){this.gpu?.dispose();this.producer?.dispose();host.remove();}};
      }
      function extent(commands) {
        const values=[];
        for(const command of commands){
          const m=command.matrix??[1,0,0,1,0,0];
          for(let i=0;i<command.vertices.length;i+=2){
            const x=command.vertices[i],y=command.vertices[i+1];
            values.push([m[0]*x+m[2]*y+m[4],m[1]*x+m[3]*y+m[5]]);
          }
        }
        if(!values.length)throw new Error('Overlay has no retained vector vertices');
        return [Math.min(...values.map(p=>p[0])),Math.min(...values.map(p=>p[1])),
          Math.max(...values.map(p=>p[0])),Math.max(...values.map(p=>p[1]))];
      }
      const geometry=commands=>commands.map(command=>({id:command.id,kind:command.kind,
        vertices:command.vertices,bounds:command.bounds,paint:command.paint}));
      function alpha(element,surface) {
        let value=1;
        for(let current=element;current;current=current.parentElement){
          value*=Number(getComputedStyle(current).opacity);
          if(current===surface)break;
        }
        return value;
      }
      await run('coordinate-space changes remain full retained commands',async()=>{
        const viewport={id:'overlay:space-fixture',kind:'mesh',space:'viewport',
          vertices:[0,0,10,0,0,10],bounds:[0,0,10,10],matrix:[1,0,0,1,20,30],
          paint:{kind:'solid',color:[.4,.3,.8,1]},clip:null};
        const world={...viewport};delete world.space;
        const toViewport=diffCommands([world],[viewport]).commands;
        check(toViewport.length===1&&toViewport[0].kind==='mesh'&&toViewport[0].space==='viewport',
          'World-to-viewport changes carry the full replacement coordinate-space contract');
        const toWorld=diffCommands([viewport],[world]).commands;
        check(toWorld.length===1&&toWorld[0].kind==='mesh'&&toWorld[0].space===undefined,
          'Omitted space on a full replacement restores world coordinates');
        const translated={...viewport,matrix:[1,0,0,1,25,35]};
        equal(diffCommands([viewport],[translated]).commands,[{id:viewport.id,matrix:translated.matrix}],
          'Viewport position-only changes retain geometry and inherit their coordinate space');
      });
      await run('constant-pixel handles retain geometry across camera changes',async()=>{
        const f=fixture();f.producer=new CanvasOverlayScene(f.view);
        try{
          const first=f.producer.snapshot();
          check(first.length>0&&first.every(c=>c.id.startsWith('overlay:')&&c.space==='viewport'&&c.kind!=='image'),
            'Editor handles are retained viewport vectors with isolated IDs');
          near(extent(first),f.bounds(),'Native overlay geometry matches the CSS handle border box');
          near([f.bounds()[2]-f.bounds()[0],f.bounds()[3]-f.bounds()[1]],[10,10],
            'Desktop resize target starts at ten CSS pixels');
          const stable=f.producer.snapshot();
          equal(stable.map(c=>c.id),first.map(c=>c.id),'Repeated snapshots preserve overlay command identity');
          f.paper.style.transform='translate(34px,18px) scale(2)';
          f.view.x=34;f.view.y=18;f.view.z=2;
          const replacement=f.handle.cloneNode(true);
          replacement.style.left='274px';replacement.style.top='198px';
          f.handle.replaceWith(replacement);f.handle=replacement;
          const zoomed=f.producer.snapshot();
          near(extent(zoomed),f.bounds(),'Zoomed overlay keeps the actual DOM hit-target position');
          near([extent(zoomed)[2]-extent(zoomed)[0],extent(zoomed)[3]-extent(zoomed)[1]],[10,10],
            'Camera zoom does not scale the handle footprint');
          equal(geometry(zoomed),geometry(first),'Camera relayout replaces DOM handles while retaining their semantic GPU geometry and paint');
          f.handle.style.left='307px';f.handle.style.top='176px';
          const panned=f.producer.snapshot();
          equal(geometry(panned),geometry(first),'Pan changes positional matrices without rebuilding handle geometry');
          near(extent(panned),f.bounds(),'Panned viewport commands stay aligned with their semantic target');
          return {commands:first.length,initial:extent(first),zoomed:extent(zoomed),panned:extent(panned)};
        }finally{f.close();}
      });
      await run('rotated waypoint handle preserves its CSS diamond',async()=>{
        const f=fixture();f.handle.classList.add('point');f.handle.dataset.canvasHandle='point:1';
        f.producer=new CanvasOverlayScene(f.view);
        try{
          const commands=f.producer.snapshot(),box=extent(commands),cssBox=f.bounds();
          near(box,cssBox,'Rotated vector extent matches the actual transformed DOM box');
          near([box[2]-box[0],box[3]-box[1]],[Math.SQRT2*10,Math.SQRT2*10],
            'Waypoint rotation retains the ten-pixel diamond diagonal');
          check(commands.some(c=>Math.abs(c.matrix[1])>.5&&Math.abs(c.matrix[2])>.5),
            'Waypoint vectors carry their rotation instead of a flattened axis-aligned rectangle');
          return {commands:commands.length,bounds:box};
        }finally{f.close();}
      });
      await run('the finite decoration inventory uses bounded vector paint',async()=>{
        const f=fixture();
        const rectangle=(element,left,top,width,height)=>{
          element.hidden=false;
          Object.assign(element.style,{left:left+'px',top:top+'px',width:width+'px',height:height+'px'});
          return element;
        };
        rectangle(f.view.selection,20,20,50,30);
        rectangle(f.view.target,110,20,50,30);f.view.target.dataset.side='left';
        const marquee=document.createElement('div');marquee.className='marquee';f.surface.append(marquee);
        rectangle(marquee,200,20,70,44);
        const port=document.createElement('button');port.className='connection-port connect';port.dataset.wire='connect';
        f.view.selection.append(port);
        const gantt=f.root.querySelector('.gantt-controls');gantt.hidden=false;
        const end=document.createElement('button');end.className='gantt-handle';end.dataset.ganttHandle='start';
        end.style.cssText='left:70px;top:140px;height:22px';gantt.append(end);
        const snap=document.createElement('div');snap.className='canvas-guide vertical';snap.style.left='180px';
        f.root.querySelector('.canvas-guides').append(snap);
        const hover=document.createElement('div');hover.className='gpu-hover';f.surface.append(hover);
        rectangle(hover,225,135,60,30);
        f.view.guide.removeAttribute('hidden');
        f.producer=new CanvasOverlayScene(f.view);
        try{
          const commands=f.producer.snapshot();
          const prefixes=['selection','target','marquee','port:connect','gantt:start','snap-guide','hover','link-guide'];
          check(prefixes.every(prefix=>commands.some(c=>c.id==='overlay:'+prefix||c.id.startsWith('overlay:'+prefix+':'))),
            'Selection, target, marquee, connection symbol, timeline, snap, hover and guide all produce retained paint');
          check(commands.every(c=>c.space==='viewport'&&['fill','mesh'].includes(c.kind)&&
            c.vertices.every(Number.isFinite)&&c.matrix.length===6&&c.matrix.every(Number.isFinite)),
            'Every decoration is finite viewport vector geometry without an image compositor');
          equal(new Set(commands.map(c=>c.id)).size,commands.length,'Decoration parts have collision-free retained IDs');
          const gradients=commands.filter(c=>['linear','radial'].includes(c.paint.kind));
          check(gradients.some(c=>c.paint.kind==='linear')&&gradients.some(c=>c.paint.kind==='radial'),
            'Soft shadows execute both retained linear sides and radial corners');
          check(gradients.every(c=>c.paint.stops.length<=64&&c.paint.stops.every((stop,index,stops)=>
            Number.isFinite(stop.offset)&&stop.offset>=0&&stop.offset<=1&&(!index||stop.offset>=stops[index-1].offset)&&
            stop.color.length===4&&stop.color.every(value=>Number.isFinite(value)&&value>=0&&value<=1))),
            'Shadow stops obey the native shader budget and ordered finite-color contract');
          check(gradients.every(c=>/^(.*:shadow:\d+):/.test(c.id)),
            'Gradient shadow parts retain their owning decoration identities');
          const patches=new Map();
          for(const command of gradients){const id=command.id.match(/^(.*:shadow:\d+):/)[1];
            patches.set(id,(patches.get(id)??0)+1);}
          check([...patches.values()].every(count=>count<=8),'Each soft shadow uses at most eight disjoint retained patches');
          f.view.selection.hidden=f.view.target.hidden=marquee.hidden=gantt.hidden=hover.hidden=true;
          f.view.guide.setAttribute('hidden','');snap.remove();
          const hidden=f.producer.snapshot();
          check(hidden.length>0&&hidden.every(c=>c.id.startsWith('overlay:handle:resize:se:')),
            'Hidden and removed decorations leave only the independently visible resize handle');
          return {commands:commands.length,gradientPatches:gradients.length,shadows:[...patches]};
        }finally{f.close();}
      });
      await run('decoration observer ignores paper and GPU paint feedback',async()=>{
        const f=fixture();let changes=0;f.producer=new CanvasOverlayScene(f.view,()=>changes++);
        try{
          f.producer.snapshot();await settle();const before=changes;
          f.paper.style.transform='translate(6px,7px)';f.svg.firstElementChild.setAttribute('fill','#dce0f4');
          f.surface.classList.add('gpu-enabled','gpu-presented','gpu-overlays-presented');f.surface.dataset.presentation='webgpu';
          await settle();
          equal(changes,before,'Diagram and presentation-class mutations do not invalidate editor overlays');
          const hiddenPaint=f.producer.snapshot();
          check(hiddenPaint.length>0,'GPU opacity suppression does not remove semantic overlay geometry');
          f.handle.style.left='140px';await until(()=>changes>before,'Decoration position mutation was not observed');
          const after=changes;
          f.producer.dispose();f.handle.style.left='150px';await settle();
          equal(changes,after,'Disposed overlay observers cannot schedule another scene');
          return {changes:after};
        }finally{f.close();}
      });
      await run('visible connector previews observe endpoint-only changes',async()=>{
        const f=fixture();f.view.guide.removeAttribute('hidden');
        let changes=0;f.producer=new CanvasOverlayScene(f.view,()=>changes++);
        try{
          const first=f.producer.snapshot().find(c=>c.id.startsWith('overlay:link-guide'));
          check(!!first,'Visible connector guide starts as a viewport vector command');
          await settle();const before=changes;
          f.view.line.setAttribute('x2','163');
          await until(()=>changes>before,'A line endpoint attribute change did not invalidate its overlay');
          check(changes>before,'Endpoint-only preview changes schedule an overlay transaction');
          const next=f.producer.snapshot().find(c=>c.id===first.id);
          check(!!next&&JSON.stringify([next.vertices,next.matrix])!==JSON.stringify([first.vertices,first.matrix]),
            'Updated line endpoints change the retained vector preview under the same semantic identity');
          return {id:first.id,changes};
        }finally{f.close();}
      });
      await run('overlay transactions retain base buffers, textures and export source',async()=>{
        const f=fixture({text:true});f.gpu=new GpuPresentationHost(f.view);
        try{
          const exported=new XMLSerializer().serializeToString(f.svg.cloneNode(true));
          f.gpu.setAvailability(true);
          await until(()=>f.updates.some(u=>u.commands?.length&&u.textures?.length),'Initial combined scene did not compile');
          const initial=f.updates.find(u=>u.reset&&u.commands?.length&&u.textures?.length);
          check(!!initial,'The first resource-bearing combined scene initializes retained state');
          equal(initial.version,2,'Combined scenes declare viewport-aware protocol version two');
          await until(()=>f.gpu.present(f.updates.at(-1).revision,{}),'Initial combined frame was not acceptable');
          const unchangedRevision=f.gpu.state().presentedRevision,unchangedCount=f.updates.length;
          const baseGeneration=f.gpu.state().compilation.generation;
          f.handle.style.left=f.handle.style.left;
          check(f.gpu.present(unchangedRevision,{}),
            'An unchanged decoration attribute cannot reject the only submitted frame acknowledgement');
          await settle();
          equal(f.updates.length,unchangedCount,'Unchanged decoration mutations emit no replacement transaction');
          const diagram=initial.commands.filter(c=>c.space!=='viewport'),overlays=initial.commands.filter(c=>c.space==='viewport');
          check(diagram.length>0&&overlays.length>0&&initial.textures.length>0,
            'One initial transaction contains diagram vectors/text resources and viewport overlays');
          const baseIds=diagram.map(c=>c.id),oldRevision=f.gpu.state().presentedRevision,count=f.updates.length;
          check(alpha(f.handle,f.surface)===0,'Accepted GPU frame suppresses DOM handle paint without detaching its target');
          f.handle.style.left='165px';
          await until(()=>f.updates.length>count,'Overlay move did not produce a retained transaction');
          const moved=f.updates.at(-1);
          equal(moved.version,2,'Overlay-only transactions retain the viewport-aware protocol version');
          check(!moved.reset,'Overlay-only changes do not reset the retained diagram');
          check((moved.commands??[]).length>0&&(moved.commands??[]).every(c=>!baseIds.includes(c.id)),
            'Overlay-only changes never resend the original diagram commands');
          equal(moved.textures??[],[],'Overlay-only changes never resend the retained text texture');
          equal(f.gpu.state().compilation.generation,baseGeneration,'Overlay changes never invalidate the base SVG compilation');
          check((moved.order??initial.order).filter(id=>baseIds.includes(id)).length===baseIds.length,
            'Effective retained painter order keeps every original diagram command');
          check(!moved.order,'Position-only overlay changes do not resend the retained painter order');
          check(moved.commands.every(c=>c.matrix&&c.kind===undefined&&c.vertices===undefined),
            'Moving a handle sends matrix patches while retaining its viewport vertex buffers');
          check(f.gpu.state().backend==='webgpu'&&alpha(f.handle,f.surface)===0,
            'Ordinary overlay latency retains the complete accepted GPU frame without mixed DOM paint');
          check(!f.gpu.present(oldRevision,{}),'Stale combined-frame acknowledgement cannot accept a changed overlay');
          check(f.gpu.present(moved.revision,{}),'Latest combined frame accepts the overlay and base atomically');
          equal(new XMLSerializer().serializeToString(f.svg.cloneNode(true)),exported,
            'Selection and handle presentation cannot enter SVG, PNG or print export source');
          const removing=f.updates.length;f.handle.remove();
          await until(()=>f.updates.length>removing,'Removing the handle did not update retained overlay order');
          const removed=f.updates.at(-1);
          check((removed.remove??[]).some(id=>id.startsWith('overlay:')),'Removed handles release their retained commands');
          check((removed.commands??[]).every(c=>!baseIds.includes(c.id)),
            'Overlay removal also leaves the original diagram geometry retained');
          check(f.gpu.present(removed.revision,{}),'Overlay removal transaction can be presented');
          const cameraCount=f.updates.length;
          f.paper.style.transform='translate(25px,11px)';f.view.x=25;f.view.y=11;f.gpu.setCamera();
          await until(()=>f.updates.length>cameraCount,'Camera-only update was not emitted');
          const camera=f.updates.at(-1);
          equal(camera.version,2,'Camera-only transactions also declare the current protocol version');
          check(!camera.commands&&!camera.textures&&!camera.order&&!camera.remove&&!camera.reset,
            'A camera update with no overlays preserves the uniform-only fast path');
          check(Array.isArray(camera.camera)&&camera.camera.length===6,'Camera-only update retains the actual SVG transform');
          const sourceCount=f.updates.length;
          f.svg.firstElementChild.setAttribute('fill','#b9caec');
          await until(()=>f.updates.length>sourceCount,'Changed source geometry did not compile');
          check(f.gpu.state().backend==='svg'&&getComputedStyle(f.paper).opacity==='1',
            'A real diagram paint change restores the complete SVG fallback until a current frame is accepted');
          check(!f.gpu.present(camera.revision,{}),'A preceding camera frame cannot hide newly changed source paint');
          check(f.gpu.present(f.updates.at(-1).revision,{}),'The new complete source frame restores GPU presentation');
          return {baseCommands:diagram.length,overlayCommands:overlays.length,textures:initial.textures.length,
            initialRevision:initial.revision,overlayRevision:moved.revision,cameraRevision:camera.revision};
        }finally{f.close();}
      });
      await run('frame acceptance reads current camera and viewport dimensions',async()=>{
        const f=fixture();f.gpu=new GpuPresentationHost(f.view);
        try{
          f.gpu.setAvailability(true);
          await until(()=>f.updates.length>0&&f.gpu.present(f.updates.at(-1).revision,{}),
            'Camera acknowledgement fixture did not compile');
          const prior=f.gpu.state().presentedRevision,count=f.updates.length;
          f.paper.style.transform='translate(42px,19px)';
          f.host.style.width='420px';
          check(!f.gpu.present(prior,{}),'CSS camera changes before setCamera reject the preceding frame acknowledgement');
          check(f.updates.length===count+1,'Current camera is published once while reconciling a stale acknowledgement');
          const changed=f.updates.at(-1);
          near(changed.camera,[1,0,0,1,42,19],'The successor uses the current attached SVG transform');
          near([changed.width,changed.height],[420,260],'The successor uses current surface dimensions');
          check(!changed.commands&&!changed.textures&&!changed.order,
            'Late camera reconciliation retains diagram and unchanged viewport geometry');
          f.host.style.display='none';
          check(!f.gpu.present(changed.revision,{}),'A zero-size surface cannot accept stale GPU paint');
          f.host.style.removeProperty('display');
          check(!f.gpu.present(changed.revision,{}),
            'Visibility restoration requests a fresh native frame instead of accepting the pre-zero-size acknowledgement');
          const restored=f.updates.at(-1);
          check(restored.revision>changed.revision&&f.gpu.present(restored.revision,{}),
            'The restored current viewport accepts its newly submitted frame');
          return {prior,changed:changed.revision,restored:restored.revision,camera:restored.camera};
        }finally{f.close();}
      });
      await run('accepted overlay keeps its real DOM pointer target',async()=>{
        const f=fixture({visible:true});f.host.id='unouml-gpu-overlay-pointer-fixture';
        f.gpu=new GpuPresentationHost(f.view);globalThis.__unoumlGpuOverlayPointerFixture=f;
        try{
          f.gpu.setAvailability(true);
          await until(()=>f.updates.length>0&&f.gpu.present(f.updates.at(-1).revision,{}),
            'Pointer fixture combined frame did not become presentable');
          check(alpha(f.handle,f.surface)===0,'Pointer fixture paints its handle exclusively through GPU commands');
          const r=f.handle.getBoundingClientRect();
          report.pointer={x:r.left+r.width/2,y:r.top+r.height/2};
          return {revision:f.updates.at(-1).revision};
        }catch(error){f.close();delete globalThis.__unoumlGpuOverlayPointerFixture;throw error;}
      });
      return report;
    });
    if(report.pointer) {
      try {
        await page.mouse.click(report.pointer.x,report.pointer.y);
        const result=await page.evaluate(()=>{
          const f=globalThis.__unoumlGpuOverlayPointerFixture;
          return {clicks:f.clicks,connected:f.handle.isConnected,
            focused:f.root.activeElement===f.handle,backend:f.gpu.state().backend};
        });
        report.assertions++;assert.equal(result.clicks,1,'A real pointer activates the retained semantic handle exactly once');
        report.assertions++;assert.equal(result.connected,true,'GPU overlay presentation preserves the DOM hit target');
        report.cases.push({name:'real pointer input reaches the GPU-presented handle',pass:true,...result});
      }catch(error){report.cases.push({name:'real pointer input reaches the GPU-presented handle',pass:false,error:error.message,stack:error.stack});}
    }
    const failures=report.cases.filter(item=>!item.pass);
    console.log('GPU overlay fixture results: '+JSON.stringify(report));
    assert.equal(failures.length,0,failures.map(item=>item.name+': '+item.error).join('\n'));
    console.log('Verified '+report.assertions+' retained GPU overlay assertions across '+report.cases.length+' cases.');
    return report;
  } finally {
    await page.evaluate(()=>{
      const f=globalThis.__unoumlGpuOverlayPointerFixture;f?.close();delete globalThis.__unoumlGpuOverlayPointerFixture;
    }).catch(()=>{});
    if(artifactDir&&report){await mkdir(artifactDir,{recursive:true});await writeFile(resolve(artifactDir,'gpu-overlays.json'),JSON.stringify(report,null,2));}
  }
}
