import assert from 'node:assert/strict';
import {resolve} from 'node:path';
import {pathToFileURL} from 'node:url';
import {verifyNativeModal} from './native-modal.mjs';
import {verifyNativeTextBoxFocus} from './native-textbox-focus.mjs';
import {verifySourceInput} from './source-input.mjs';
import {verifyCanvasCoordinates} from './canvas-coordinates.mjs';

// Run against attached SVGs so Chromium supplies real CSS, CTMs, text shaping,
// clipping and SVG image decoding. Fixtures never alter the editor document.
export async function verifyGpuScene(page,{artifactDir=process.env.SCREENSHOT_DIR}={}) {
  const fixtures=[];
  for(const [name,verify] of [
    ['native semantic lifecycle',verifyNativeModal],
    ['native TextBox replacement focus',verifyNativeTextBoxFocus],
    ['native source input',verifySourceInput],
    ['canvas coordinate agreement',verifyCanvasCoordinates]
  ]) {
    try {
      const result=await verify(page,{artifactDir});
      fixtures.push({name,pass:true,assertions:result?.assertions});
    } catch(error) {
      fixtures.push({name,pass:false,error:error.message,stack:error.stack});
    }
  }
  const report=await page.evaluate(async()=>{
    const {compileScene,diffCommands,ScenePresentation}=await import(new URL('uml/gpu/scene.js',document.baseURI).href);
    const {RasterCache,bytesToBase64}=await import(new URL('uml/gpu/resources.js',document.baseURI).href);
    await document.fonts?.ready;
    const results=[];let assertions=0;
    const check=(condition,message)=>{assertions++;if(!condition)throw new Error(message);};
    const equal=(actual,expected,message)=>check(JSON.stringify(actual)===JSON.stringify(expected),message+'; got '+JSON.stringify(actual)+'; expected '+JSON.stringify(expected));
    const near=(actual,expected,message,epsilon=1e-5)=>check(actual.length===expected.length&&actual.every((v,i)=>Math.abs(v-expected[i])<=epsilon),message+'; got '+actual+'; expected '+expected);
    const nextFrame=()=>new Promise(resolve=>requestAnimationFrame(resolve));
    async function until(predicate,message) {
      const deadline=performance.now()+8000;
      while(!predicate()){if(performance.now()>deadline)throw new Error(message);await nextFrame();}
    }
    function fixture(markup,{overflow='visible'}={}) {
      const host=document.createElement('div'),camera=document.createElement('div');
      host.dataset.gpuCompilerFixture='true';
      host.style.cssText='position:fixed;left:-12000px;top:0;width:400px;height:240px;pointer-events:none;contain:layout style;';
      camera.style.cssText='width:400px;height:240px;transform-origin:0 0;';
      camera.innerHTML='<svg xmlns="http://www.w3.org/2000/svg" width="400" height="240" viewBox="0 0 400 240" style="overflow:'+overflow+';width:400px;height:240px;max-width:none;max-height:none">'+markup+'</svg>';
      host.append(camera);document.body.append(host);
      const svg=camera.firstElementChild;
      check(svg.getCTM()!==null,'Fixture must have a browser CTM');
      return {host,camera,svg,close:()=>host.remove()};
    }
    async function run(name,body) {
      try{await body();results.push({name,pass:true});}
      catch(error){results.push({name,pass:false,error:error instanceof Error?error.message:String(error),stack:error instanceof Error?error.stack:undefined});}
    }
    function triangleHit(vertices,x,y) {
      for(let i=0;i<vertices.length;i+=6){
        const a=[vertices[i],vertices[i+1]],b=[vertices[i+2],vertices[i+3]],c=[vertices[i+4],vertices[i+5]];
        const cross=(u,v,p)=>(v[0]-u[0])*(p[1]-u[1])-(v[1]-u[1])*(p[0]-u[0]);
        const area=cross(a,b,c),ab=cross(a,b,[x,y]),bc=cross(b,c,[x,y]),ca=cross(c,a,[x,y]);
        if(Math.abs(area)>1e-10&&((ab>=-1e-8&&bc>=-1e-8&&ca>=-1e-8)||(ab<=1e-8&&bc<=1e-8&&ca<=1e-8)))return true;
      }return false;
    }
    function windingAt(vertices,x,y) {
      let winding=0;
      for(let i=0;i<vertices.length;i+=6){
        const ax=vertices[i],ay=vertices[i+1],bx=vertices[i+2],by=vertices[i+3],cx=vertices[i+4],cy=vertices[i+5];
        const cross=(a,b,c,d)=>(a*d-b*c);
        const area=cross(bx-ax,by-ay,cx-ax,cy-ay);
        const edges=[cross(bx-ax,by-ay,x-ax,y-ay),cross(cx-bx,cy-by,x-bx,y-by),cross(ax-cx,ay-cy,x-cx,y-cy)];
        if(Math.abs(area)>1e-10&&(edges.every(v=>v>1e-8)||edges.every(v=>v< -1e-8)))winding+=Math.sign(area);
      }return winding;
    }
    function pixels(texture) {return Uint8Array.from(atob(texture.rgba),c=>c.charCodeAt(0));}
    function sample(scene,command,x,y) {
      const texture=scene.textures.find(t=>t.id===command.image),data=pixels(texture),b=command.bounds;
      const m=command.matrix||[1,0,0,1,0,0],det=m[0]*m[3]-m[1]*m[2],dx=x-m[4],dy=y-m[5];
      check(Number.isFinite(det)&&Math.abs(det)>1e-14,'Retained image transform is invertible');
      const localX=(m[3]*dx-m[2]*dy)/det,localY=(-m[1]*dx+m[0]*dy)/det;
      const px=Math.floor((localX-b[0])/b[2]*texture.width),py=Math.floor((localY-b[1])/b[3]*texture.height);
      check(px>=0&&px<texture.width&&py>=0&&py<texture.height,'Sample lies inside retained texture');
      const at=(py*texture.width+px)*4;return Array.from(data.slice(at,at+4));
    }

    await run('even-odd and nonzero holes preserve winding geometry',async()=>{
      const f=fixture('<path d="M0 0H80V80H0Z M20 20H60V60H20Z" fill="#e31" fill-rule="evenodd"/>'+
        '<path transform="translate(100 0)" d="M0 0H80V80H0Z M20 20V60H60V20Z" fill="#16a" fill-rule="nonzero"/>');
      try{
        const scene=await compileScene(f.svg);
        equal(scene.commands.map(c=>c.kind),['fill','fill'],'Holes remain native vector commands');
        equal(scene.commands.map(c=>c.rule),['evenodd','nonzero'],'Fill rules retained');
        check(Math.abs(windingAt(scene.commands[0].vertices,9,13))%2===1,'Even-odd outer area filled');
        check(Math.abs(windingAt(scene.commands[0].vertices,40,45))%2===0,'Even-odd inner area empty');
        check(windingAt(scene.commands[1].vertices,9,13)!==0,'Nonzero outer area filled');
        equal(windingAt(scene.commands[1].vertices,40,45),0,'Opposite contour clears nonzero hole');
        equal(scene.textures.length,0,'Native holes require no texture');
      }finally{f.close();}
    });

    await run('dashes and round/square caps retain visible stroke coverage',async()=>{
      const f=fixture('<line x1="0" y1="40" x2="100" y2="40" stroke="#123" stroke-width="8" stroke-dasharray="20 10" stroke-linecap="round"/>'+
        '<line x1="0" y1="80" x2="100" y2="80" stroke="#456" stroke-width="8" stroke-linecap="square"/>');
      try{
        const scene=await compileScene(f.svg),[round,square]=scene.commands;
        equal(scene.commands.map(c=>c.kind),['mesh','mesh'],'Strokes use union meshes');
        check(triangleHit(round.vertices,10,40),'First dash is filled');
        check(!triangleHit(round.vertices,25,40),'Dash gap remains empty beyond caps');
        check(triangleHit(round.vertices,35,40),'Second dash is filled');
        check(triangleHit(round.vertices,-2,40),'Round start cap extends past endpoint');
        check(!triangleHit(round.vertices,-3.5,43.5),'Round cap excludes square corner');
        check(triangleHit(square.vertices,-3.5,83.5),'Square cap retains its corner');
        check(scene.commands.every(c=>c.vertices.every(Number.isFinite)),'All tessellated stroke vertices finite');
      }finally{f.close();}
    });

    await run('computed styles and inherited gradient definitions compile without rasterization',async()=>{
      const f=fixture('<defs><linearGradient id="base" x1="25%" x2="75%" spreadMethod="reflect"><stop offset="0" stop-color="#f00"/><stop offset=".7" stop-color="#00f" stop-opacity=".4"/></linearGradient>'+
        '<linearGradient id="derived" href="#base" gradientTransform="translate(.25 .5) scale(2)"/>'+
        '<radialGradient id="radial" cx=".5" cy=".5" fx=".6" fy=".5" r=".5" fr=".1"><stop stop-color="#fff"/><stop offset="1" stop-color="#000"/></radialGradient></defs>'+
        '<style>.compiler-gradient{fill:url(#derived);fill-opacity:.6}</style>'+
        '<rect class="compiler-gradient" x="10" y="20" width="80" height="40"/><circle cx="180" cy="50" r="30" fill="url(#radial)"/>');
      try{
        const scene=await compileScene(f.svg),[linear,radial]=scene.commands;
        equal(scene.textures.length,0,'Supported gradients stay in shaders');
        equal([linear.paint.kind,radial.paint.kind],['linear','radial'],'Gradient kinds retained');
        equal(linear.paint.spread,'reflect','Inherited spread retained');
        near(linear.paint.start,[.25,0],'Inherited gradient start');
        near(linear.paint.end,[.75,0],'Inherited gradient end');
        check(Math.abs(linear.paint.opacity-.6)<1e-6,'CSS fill opacity retained');
        near(linear.paint.stops[1].color,[0,0,1,.4],'Stop alpha remains straight');
        near(linear.paint.transform,[1/160,0,0,1/80,-30/160,-40/80],'Object-box inverse transform');
        near(radial.paint.focal,[.6,.5],'Radial focal position');
        check(Math.abs(radial.paint.focalRadius-.1)<1e-6,'Radial focal radius retained');
      }finally{f.close();}
    });

    await run('overflow-visible text preserves inherited fonts, nested spans and colored glyph pixels',async()=>{
      const f=fixture('<g fill="#263238" font-family="Arial" font-size="20" transform="translate(12 18)">'+
        '<text x="5" y="25">Alpha<tspan dx="4" fill="#dd1111">β</tspan><tspan x="5" dy="24" font-weight="700">שלום</tspan></text></g>');
      try{
        const scene=await compileScene(f.svg),command=scene.commands[0];
        equal(scene.stats.rasterReasons.text,1,'Text shaped as one bounded layer');
        equal(scene.commands.length,1,'Text span tree is not duplicated');
        equal(command.kind,'image','Text is retained RGBA resource');
        near(command.matrix,[1,0,0,1,12,18],'Local text texture retains ancestor transform');
        const data=pixels(scene.textures[0]);let red=0,dark=0;
        for(let i=0;i<data.length;i+=4)if(data[i+3]>96){if(data[i]>150&&data[i+1]<80&&data[i+2]<80)red++;if(data[i]<100&&data[i+1]<120&&data[i+2]<130)dark++;}
        check(red>5,'Nested red span produced actual shaped pixels');
        check(dark>40,'Inherited dark glyphs produced actual shaped pixels');
        check(scene.stats.textureBytes<400*240*4,'Text raster is bounded to glyph content');
      }finally{f.close();}
    });

    await run('group opacity is isolated once across overlapping children',async()=>{
      const f=fixture('<g opacity=".5"><rect x="10" y="10" width="40" height="40" fill="#f00"/><rect x="30" y="10" width="40" height="40" fill="#00f"/></g>');
      try{
        const scene=await compileScene(f.svg);
        equal(scene.stats.rasterReasons['group-opacity'],1,'Group has a single isolated layer');
        equal(scene.commands.length,1,'Overlapping children are not independently faded');
        const a=sample(scene,scene.commands[0],20,20),b=sample(scene,scene.commands[0],40,20);
        check(a[0]>250&&a[1]<2&&a[2]<2,'Uncovered red child remains red');
        check(b[2]>250&&b[0]<2&&b[1]<2,'Later blue child wins painter order');
        check(Math.abs(a[3]-128)<=1&&Math.abs(b[3]-128)<=1,'Overlap opacity is 50%, not accumulated 75%');
      }finally{f.close();}
    });

    await run('filter regions preserve negative and outside-viewBox effect bounds',async()=>{
      for(const units of ['userSpaceOnUse','objectBoundingBox']){
        const region=units==='userSpaceOnUse'?'x="-20" y="-30" width="120" height="100"':'x="-50%" y="-100%" width="300%" height="300%"';
        const f=fixture('<defs><filter id="shadow" filterUnits="'+units+'" '+region+'><feDropShadow dx="10" dy="-8" stdDeviation="3" flood-color="#000"/></filter></defs>'+
          '<rect x="0" y="0" width="40" height="30" fill="#f00" filter="url(#shadow)"/>');
        try{
          const scene=await compileScene(f.svg),command=scene.commands[0];
          equal(scene.stats.rasterReasons.filter,1,'Filter is retained as an isolated resource');
          check(command.bounds[0]<=-20&&command.bounds[1]<=-30,'Declared negative filter bounds retained for '+units);
          check(command.bounds[0]+command.bounds[2]>=100,'Filter extends beyond source geometry for '+units);
          check(sample(scene,command,30,-7)[3]>10,'Shadow above source geometry is not cropped for '+units);
          check(scene.stats.textureBytes<400*240*4,'Filter uses bounded region instead of full viewport');
        }finally{f.close();}
      }
    });

    await run('rectangular clips stay vector while complex clips preserve exact alpha',async()=>{
      const f=fixture('<defs><clipPath id="rectClip"><rect x="5" y="5" width="30" height="20"/></clipPath></defs>'+
        '<g transform="translate(10 20)" clip-path="url(#rectClip)"><rect width="80" height="80" fill="#c21"/></g>');
      try{
        const scene=await compileScene(f.svg);
        equal(scene.commands.length,1,'One clipped vector command');
        equal(scene.commands[0].kind,'fill','Axis-aligned rectangle clip stays vector');
        near(scene.commands[0].clip,[15,25,30,20],'Inherited clip in root coordinates');
        equal(scene.textures.length,0,'Rectangle clip allocates no raster');
      }finally{f.close();}
      const c=fixture('<defs><clipPath id="circleClip"><circle cx="40" cy="40" r="20"/></clipPath></defs>'+
        '<rect width="80" height="80" fill="#f00" clip-path="url(#circleClip)"/>');
      try{
        const scene=await compileScene(c.svg);
        equal(scene.stats.rasterReasons['complex-clip'],1,'Complex clip has an explicit compatibility reason');
        check(sample(scene,scene.commands[0],40,40)[3]>250,'Circle center retained');
        equal(sample(scene,scene.commands[0],10,10)[3],0,'Circle exterior stays transparent');
      }finally{c.close();}
    });

    await run('patterns, masks and markers retain references and compositing semantics',async()=>{
      const p=fixture('<defs><pattern id="tiles" width="8" height="8" patternUnits="userSpaceOnUse"><rect width="8" height="8" fill="#fff"/><rect width="4" height="4" fill="#f00"/></pattern></defs>'+
        '<rect width="40" height="40" fill="url(#tiles)"/>');
      try{
        const scene=await compileScene(p.svg);
        equal(scene.stats.rasterReasons.pattern,1,'Pattern has a retained layer');
        const red=sample(scene,scene.commands[0],2,2),white=sample(scene,scene.commands[0],6,2);
        check(red[0]>250&&red[1]<2&&red[3]>250,'Pattern colored cell retained');
        check(white[0]>250&&white[1]>250&&white[2]>250,'Pattern background cell retained');
      }finally{p.close();}
      const m=fixture('<defs><mask id="half" maskUnits="userSpaceOnUse" x="0" y="0" width="80" height="80"><rect width="40" height="80" fill="#fff"/></mask></defs>'+
        '<rect width="80" height="80" fill="#00f" mask="url(#half)"/>');
      try{
        const scene=await compileScene(m.svg);
        equal(scene.stats.rasterReasons.mask,1,'Mask has a retained layer');
        check(sample(scene,scene.commands[0],10,10)[3]>250,'Mask white area retained');
        equal(sample(scene,scene.commands[0],60,10)[3],0,'Mask empty area transparent');
      }finally{m.close();}
      const arrow=fixture('<defs><marker id="arrow" viewBox="0 0 10 10" refX="10" refY="5" markerWidth="6" markerHeight="6" orient="auto"><path d="M0 0L10 5L0 10Z" fill="#f0f"/></marker></defs>'+
        '<line x1="10" y1="110" x2="110" y2="110" stroke="#000" stroke-width="3" marker-end="url(#arrow)"/>',{overflow:'hidden'});
      try{
        const scene=await compileScene(arrow.svg);
        equal(scene.stats.rasterReasons.marker,1,'Marker reference has explicit compatibility layer');
        const marker=sample(scene,scene.commands[0],101,110);
        check(marker[0]>200&&marker[2]>200&&marker[1]<20&&marker[3]>200,'Referenced arrow geometry is visibly decoded');
      }finally{arrow.close();}
    });

    await run('data images and use instances remain visible with inherited paint',async()=>{
      const bitmap=document.createElement('canvas');bitmap.width=bitmap.height=2;
      const context=bitmap.getContext('2d');context.fillStyle='#11cc33';context.fillRect(0,0,2,2);
      const f=fixture('<defs><g id="symbol"><rect width="30" height="25"/></g></defs>'+
        '<image x="10" y="10" width="30" height="25" href="'+bitmap.toDataURL('image/png')+'"/>'+
        '<use href="#symbol" x="80" y="10" fill="#3355dd"/>');
      try{
        const scene=await compileScene(f.svg);
        equal(scene.commands.length,2,'Both image and instance are represented');
        equal(scene.stats.rasterReasons.image,1,'Image retained');equal(scene.stats.rasterReasons.use,1,'Use instance retained');
        const image=sample(scene,scene.commands[0],20,20),instance=sample(scene,scene.commands[1],90,20);
        check(image[1]>180&&image[0]<40&&image[3]>250,'Decoded embedded image is green');
        check(instance[2]>180&&instance[0]<80&&instance[3]>250,'Use inherits blue instance fill');
      }finally{bitmap.width=bitmap.height=1;f.close();}
    });


    await run('stylesheet geometry overrides preserve computed rectangle and path pixels',async()=>{
      const f=fixture('<style>.styled-rect{width:70px;height:30px;rx:10px}.styled-path{d:path("M100 10H150V50H100Z")}</style>'+
        '<rect class="styled-rect" x="10" y="10" width="20" height="20" fill="#f00"/>'+
        '<path class="styled-path" d="M180 10H200V30H180Z" fill="#00f"/>');
      try{
        const scene=await compileScene(f.svg);
        equal(scene.commands.map(c=>c.kind),['image','image'],'Stylesheet geometry uses faithful retained layers');
        equal(scene.stats.rasterReasons['css-geometry'],2,'Both stylesheet geometry overrides are diagnosed');
        const rectangle=sample(scene,scene.commands[0],75,25),corner=sample(scene,scene.commands[0],11,11);
        check(rectangle[0]>250&&rectangle[3]>250,'CSS width extends the rectangle past its width attribute');
        equal(corner[3],0,'CSS corner radius is retained');
        const path=sample(scene,scene.commands[1],110,20);
        check(path[2]>250&&path[3]>250,'CSS path geometry is retained at the computed position');
        const b=scene.commands[1].bounds;
        check(b[0]<=100&&b[0]+b[2]<160,'Retained path bounds follow computed d rather than obsolete attribute');
      }finally{f.close();}
    });

    await run('geometry cache and matrix patches retain arrays and preserve painter order',async()=>{
      const f=fixture('<rect id="one" width="40" height="30" fill="#f00"/><rect id="two" x="60" width="20" height="20" fill="#00f"/>');
      const geometryCache=new WeakMap(),rasterCache=new RasterCache();
      try{
        const first=await compileScene(f.svg,{geometryCache,rasterCache}),node=f.svg.querySelector('#one');
        equal(first.stats.geometryBuilds,2,'Initial shapes tessellated once');
        node.setAttribute('transform','translate(12 7)');
        const moved=await compileScene(f.svg,{geometryCache,rasterCache}),delta=diffCommands(first.commands,moved.commands);
        equal(moved.stats.geometryBuilds,0,'Moving does not rebuild geometry');equal(moved.stats.geometryCacheHits,2,'Both retained geometries reused');
        check(first.commands[0].vertices===moved.commands[0].vertices,'Exact vertex array survives move');
        equal(delta.commands.length,1,'Only changed shape is patched');
        equal(Object.keys(delta.commands[0]).sort(),['id','matrix'],'Movement emits a matrix-only patch');
        near(delta.commands[0].matrix,[1,0,0,1,12,7],'Movement patch is exact');
        node.setAttribute('fill','#0f0');
        const painted=await compileScene(f.svg,{geometryCache,rasterCache});
        equal(painted.stats.geometryBuilds,0,'Paint-only mutation retains tessellation');
        check(painted.commands[0].vertices===first.commands[0].vertices,'Paint change retains vertices');
        near(painted.commands[0].paint.color,[0,1,0,1],'Paint mutation compiled');
        node.setAttribute('width','55');
        const resized=await compileScene(f.svg,{geometryCache,rasterCache});
        equal(resized.stats.geometryBuilds,1,'Geometry mutation rebuilds only its owner');
        check(resized.commands[0].vertices!==first.commands[0].vertices,'Changed mesh replaced');
        f.svg.append(node);
        const reordered=await compileScene(f.svg,{geometryCache,rasterCache}),orderPatch=diffCommands(resized.commands,reordered.commands);
        equal(orderPatch.commands.length,0,'Pure painter reorder resends no mesh');
        equal(orderPatch.order,resized.commands.map(c=>c.id).reverse(),'Painter order follows SVG document order');
        node.remove();
        const removed=await compileScene(f.svg,{geometryCache,rasterCache}),removePatch=diffCommands(reordered.commands,removed.commands);
        equal(removePatch.remove,[resized.commands[0].id],'Removed owner is explicitly deleted');
      }finally{rasterCache.dispose();f.close();}
    });

    await run('presentation camera updates avoid compilation and acknowledgements reject stale revisions',async()=>{
      const f=fixture('<rect width="50" height="30" fill="#246"/>'),view={version:1,surface:f.host};
      const updates=[],stats=[],errors=[],pending=[];
      const presentation=new ScenePresentation(view,update=>updates.push(update),{onStats:s=>stats.push(s),onError:e=>errors.push(e),onPending:p=>pending.push(p)});
      try{
        presentation.setScene(f.svg,1);
        await until(()=>updates.length===1||errors.length>0,'Initial retained scene did not compile');
        equal(errors,[],'Initial presentation has no compiler error');
        const first=updates[0],vertices=presentation.commands[0].vertices;
        check(presentation.presented(first.revision),'Current frame acknowledged');
        equal(pending.at(-1),false,'Pending state clears only on current presentation');
        f.camera.style.transform='translate(17px,9px)';
        presentation.setCamera();
        equal(updates.length,2,'Pan emits a single synchronous camera transaction');
        const camera=updates[1];
        check(!('commands' in camera)&&!('textures' in camera)&&!('order' in camera),'Pan emits no mesh, texture, or order payload');
        equal(stats.length,1,'Pan never invokes scene compilation');
        check(presentation.commands[0].vertices===vertices,'Pan retains exact vertex array');
        check(!presentation.presented(first.revision),'Previous revision cannot acknowledge current frame');
        check(presentation.presented(camera.revision),'Current camera frame can acknowledge');
        f.svg.firstElementChild.setAttribute('data-test-metadata','only');
        await nextFrame();await nextFrame();
        equal(updates.length,2,'Metadata changes do not invalidate presentation');
        f.svg.firstElementChild.setAttribute('fill','#f80');
        await until(()=>updates.length===3||errors.length>0,'Paint mutation was not observed');
        equal(errors,[],'Observed paint mutation has no error');
        equal(stats.at(-1).geometryBuilds,0,'Mutation observer preserves unchanged geometry');
        near(presentation.commands[0].paint.color,[1,136/255,0,1],'Observed mutation reaches retained scene');
        check(!presentation.presented(camera.revision),'Mutation makes old acknowledgement stale');
      }finally{presentation.dispose();f.close();}
    });

    await run('asynchronous source replacement and disposal suppress stale texture transactions',async()=>{
      const f=fixture('<text x="5" y="25" font-size="20">Old</text>'),view={version:1,surface:f.host};
      const updates=[],errors=[],jobs=[];
      const presentation=new ScenePresentation(view,update=>updates.push(update),{
        onError:e=>errors.push(e),
        rasterOptions:{raster:(_xml,width,height)=>new Promise(resolve=>jobs.push({
          complete:id=>resolve({id,width,height,rgba:bytesToBase64(new Uint8Array(width*height*4))})
        }))}
      });
      try{
        presentation.setScene(f.svg,1);
        await until(()=>jobs.length===1||errors.length>0,'First async raster never started');
        equal(errors,[],'First source starts rasterization');
        view.version=2;f.svg.firstElementChild.textContent='New';presentation.setScene(f.svg,2);
        jobs[0].complete('old-texture');
        await until(()=>jobs.length===2||errors.length>0,'Replacement async raster never started');
        equal(updates.length,0,'Old generation emits no scene transaction');
        jobs[1].complete('new-texture');
        await until(()=>updates.length===1||errors.length>0,'Replacement scene never committed');
        equal(errors,[],'Replacement raster has no error');
        equal(updates[0].textures.map(t=>t.id),['new-texture'],'Only current source texture uploaded');
        check(updates[0].reset,'Replacement source starts an atomic reset');
        f.svg.firstElementChild.textContent='Disposed';
        await until(()=>jobs.length===3||errors.length>0,'Third async raster never started');
        presentation.dispose();jobs[2].complete('disposed-texture');
        await nextFrame();await nextFrame();
        equal(updates.length,1,'Disposed presenter emits no late frame');
        equal(presentation.rasterCache.bytes,0,'Disposed presenter retains no raster bytes');
        equal(presentation.rasterCache.entries.size,0,'Late completion cannot resurrect resource cache');
      }finally{presentation.dispose();f.close();}
    });

    return {results,assertions,browser:navigator.userAgent,remainingFixtures:document.querySelectorAll('[data-gpu-compiler-fixture]').length};
  }).catch(error=>({results:[{name:'compiler fixture initialization',pass:false,error:error.message,stack:error.stack}],assertions:0}));
  report.fixtures=fixtures;
  report.nonScalingStroke=await measureNonScalingStroke(page,artifactDir).catch(error=>({pass:false,error:error.message,stack:error.stack}));
  report.results.push({
    name:'non-scaling strokes match native CSS/viewBox zoom and retained camera coverage',
    pass:report.nonScalingStroke.pass,
    ...(!report.nonScalingStroke.pass?{error:report.nonScalingStroke.error||JSON.stringify(report.nonScalingStroke.samples.map(s=>({
      phase:s.phase,native:s.nativeStrokeCssWidth,fresh:s.freshMeshCssWidth,retained:s.retainedMeshCssWidth
    })))}:{})
  });
  if(artifactDir){
    const {mkdir,writeFile}=await import('node:fs/promises');
    await mkdir(artifactDir,{recursive:true});
    await writeFile(resolve(artifactDir,'gpu-scene.json'),JSON.stringify(report,null,2));
  }
  for(const result of fixtures)console.log((result.pass?'PASS ':'FAIL ')+'Browser fixture: '+result.name+(result.error?': '+result.error:''));
  for(const result of report.results)console.log((result.pass?'PASS ':'FAIL ')+'GPU compiler: '+result.name+(result.error?': '+result.error:''));
  const failed=[...fixtures,...report.results].filter(result=>!result.pass);
  assert.deepEqual(failed,[],'Attached-browser native, canvas and GPU compiler regressions');
  assert.equal(report.remainingFixtures,0,'Every compiler fixture must be removed');
  assert.ok(report.assertions>=100,'Compiler fixtures must execute their semantic assertions');
  return report;
}


// Measure the native page itself: serializing the SVG would remove the parent
// CSS transform and could incorrectly claim equivalence for non-scaling stroke.
async function measureNonScalingStroke(page,artifactDir) {
  await page.evaluate(async()=>{
    const {compileScene}=await import(new URL('uml/gpu/scene.js',document.baseURI).href);
    const {RasterCache,qualityBucket}=await import(new URL('uml/gpu/resources.js',document.baseURI).href);
    const host=document.createElement('div'),wrapper=document.createElement('div');
    host.id='unouml-stroke-probe';
    host.style.cssText='position:fixed;left:0;top:0;width:300px;height:140px;z-index:2147483647;background:#fff;pointer-events:none;contain:layout style;isolation:isolate;opacity:1;';
    wrapper.style.cssText='width:260px;height:100px;transform-origin:0 0;';
    // Whole 20/21/22 CSS-pixel coverage at the chosen scales separates camera
    // semantics from Chromium's fractional-pixel antialiasing quantization.
    wrapper.innerHTML='<svg xmlns="http://www.w3.org/2000/svg" width="260" height="100" viewBox="0 0 260 100" style="width:260px;height:100px;max-width:none;max-height:none;overflow:visible">'+
      '<line x1="20" y1="50" x2="220" y2="50" stroke="#000" stroke-width="20" fill="none" vector-effect="non-scaling-stroke"/></svg>';
    host.append(wrapper);document.body.append(host);
    globalThis.__unoumlStrokeProbe={host,wrapper,svg:wrapper.firstElementChild,compileScene,qualityBucket,rasterCache:new RasterCache(),geometryCache:new WeakMap(),scenes:new Map()};
  });
  const samples=[];
  try{
    const phases=[
      {phase:'baseline',cssScale:1,viewBoxScale:1},
      {phase:'css-scale-1.1',cssScale:1.1,viewBoxScale:1},
      {phase:'same-bucket-base-1.05',cssScale:1.05,viewBoxScale:1},
      {phase:'same-bucket-scale-1.1',cssScale:1.1,viewBoxScale:1,retained:'same-bucket-base-1.05'},
      {phase:'same-bucket-translation',cssScale:1.1,viewBoxScale:1,x:9,y:7,retained:'same-bucket-scale-1.1'},
      {phase:'internal-viewBox-scale-1.1',cssScale:1,viewBoxScale:1.1}
    ];
    for(const phase of phases){
      const state=await page.evaluate(async options=>{
        const p=globalThis.__unoumlStrokeProbe;
        p.wrapper.style.transform='translate('+(options.x||0)+'px,'+(options.y||0)+'px) scale('+options.cssScale+')';
        p.svg.setAttribute('viewBox','0 0 '+260/options.viewBoxScale+' '+100/options.viewBoxScale);
        const values=m=>[m.a,m.b,m.c,m.d,m.e,m.f],screen=p.svg.getScreenCTM();
        const bucket=p.qualityBucket(Math.hypot(screen.a,screen.b)*(devicePixelRatio||1));
        const fresh=await p.compileScene(p.svg,{scale:bucket,rasterCache:p.rasterCache,geometryCache:p.geometryCache});
        function projectedWidth(scene){
          let low=Infinity,high=-Infinity;
          for(const command of scene.commands){
            if(command.kind!=='mesh')throw new Error('Non-scaling stroke unexpectedly rasterized');
            const m=command.matrix;
            for(let i=0;i<command.vertices.length;i+=2){
              const x=command.vertices[i],y=command.vertices[i+1];
              const rootX=m[0]*x+m[2]*y+m[4],rootY=m[1]*x+m[3]*y+m[5];
              const py=screen.b*rootX+screen.d*rootY+screen.f;
              low=Math.min(low,py);high=Math.max(high,py);
            }
          }return high-low;
        }
        const retained=options.retained?p.scenes.get(options.retained):null;
        p.scenes.set(options.phase,fresh);
        await new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve)));
        return {phase:options.phase,cssScale:options.cssScale,viewBoxScale:options.viewBoxScale,
          bucket,rootCTM:values(p.svg.getCTM()),rootScreenCTM:values(p.svg.getScreenCTM()),
          lineCTM:values(p.svg.firstElementChild.getCTM()),lineScreenCTM:values(p.svg.firstElementChild.getScreenCTM()),
          freshMeshCssWidth:projectedWidth(fresh),retainedMeshCssWidth:retained?projectedWidth(retained):null,
          geometryBuilds:fresh.stats.geometryBuilds};
      },phase);
      const png=await page.locator('#unouml-stroke-probe').screenshot({animations:'disabled'});
      const native=await page.evaluate(async bytes=>{
        const bitmap=await createImageBitmap(new Blob([new Uint8Array(bytes)],{type:'image/png'}));
        const canvas=new OffscreenCanvas(bitmap.width,bitmap.height),context=canvas.getContext('2d',{willReadFrequently:true});
        context.drawImage(bitmap,0,0);bitmap.close();
        const pixels=context.getImageData(0,0,canvas.width,canvas.height).data,ratio=canvas.width/300;
        const from=Math.ceil(80*ratio),to=Math.floor(140*ratio);let coverage=0;
        for(let x=from;x<to;x++)for(let y=0;y<canvas.height;y++){
          const at=(y*canvas.width+x)*4;
          coverage+=1-(pixels[at]+pixels[at+1]+pixels[at+2])/(3*255);
        }
        return {nativeStrokeCssWidth:coverage/(to-from)/ratio,pixelRatio:ratio,width:canvas.width,height:canvas.height};
      },Array.from(png));
      samples.push({...state,...native});
      if(artifactDir){
        const {mkdir,writeFile}=await import('node:fs/promises');
        await mkdir(artifactDir,{recursive:true});
        await writeFile(resolve(artifactDir,'gpu-non-scaling-'+phase.phase+'.png'),png);
      }
    }
    const pass=samples.every(s=>Number.isFinite(s.nativeStrokeCssWidth)&&s.nativeStrokeCssWidth>0&&
      Math.abs(s.nativeStrokeCssWidth-s.freshMeshCssWidth)<.18&&
      (s.retainedMeshCssWidth===null||Math.abs(s.nativeStrokeCssWidth-s.retainedMeshCssWidth)<.18));
    const result={pass,method:'Native Chromium screenshot coverage averaged across 60 CSS pixels; mesh vertices projected through the actual root screen CTM.',samples};
    console.log('Non-scaling stroke measurement:',JSON.stringify(result));
    if(artifactDir){
      const {mkdir,writeFile}=await import('node:fs/promises');
      await mkdir(artifactDir,{recursive:true});
      await writeFile(resolve(artifactDir,'gpu-non-scaling-stroke.json'),JSON.stringify(result,null,2));
    }
    return result;
  }finally{
    await page.evaluate(()=>{
      const p=globalThis.__unoumlStrokeProbe;p?.rasterCache.dispose();p?.host.remove();delete globalThis.__unoumlStrokeProbe;
    });
  }
}

// The same contract suite can run before a WASM publish, using the production
// browser modules and a real Chromium document without requiring app startup.
if(process.argv[1]&&pathToFileURL(resolve(process.argv[1])).href===import.meta.url) {
  const {createServer}=await import('node:http');
  const {readFile}=await import('node:fs/promises');
  const {chromium}=await import('playwright');
  const {graphicsLaunchOptions}=await import('./graphics-options.mjs');
  const server=createServer(async(request,response)=>{
    try{
      const path=new URL(request.url,'http://127.0.0.1').pathname;
      if(path==='/'){
        response.writeHead(200,{'Content-Type':'text/html; charset=utf-8'});
        response.end('<!doctype html><meta charset="utf-8"><title>UnoUML SVG compiler validation</title>');
      }else if(path==='/uml/preview.css'){
        const content=await readFile(new URL('../../src/UnoUML.App/wwwroot'+path,import.meta.url));
        response.writeHead(200,{'Content-Type':'text/css; charset=utf-8'});response.end(content);
      }else if(/^\/uml\/gpu\/[a-z-]+\.js$/.test(path)||/^\/uml\/native-(accessibility|dialogs|combos|textboxes|input-diagnostics)\.js$/.test(path)
        ||/^\/uml\/canvas-(editing|scene|geometry)\.js$/.test(path)||path==='/uml/source-text.js'){
        const content=await readFile(new URL('../../src/UnoUML.App/wwwroot'+path,import.meta.url));
        response.writeHead(200,{'Content-Type':'text/javascript; charset=utf-8'});response.end(content);
      }else{response.writeHead(404);response.end();}
    }catch(error){response.writeHead(500);response.end(String(error));}
  });
  await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
  let browser;
  try{
    browser=await chromium.launch(graphicsLaunchOptions(false));
    const page=await browser.newPage({viewport:{width:1280,height:900}});
    const errors=[];page.on('pageerror',error=>errors.push(error.message));
    await page.goto('http://127.0.0.1:'+server.address().port);
    const report=await verifyGpuScene(page,{artifactDir:process.env.SCREENSHOT_DIR||resolve('artifacts/gpu-scene')});
    assert.deepEqual(errors,[],'Compiler fixtures must not leave uncaught page errors');
    console.log(report.assertions+' attached-browser SVG compiler assertions; '+report.results.length+' scenarios passed.');
  }finally{
    await browser?.close();
    await new Promise((resolve,reject)=>server.close(error=>error?reject(error):resolve()));
  }
}
