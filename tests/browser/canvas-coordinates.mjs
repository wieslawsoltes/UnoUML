import assert from 'node:assert/strict';
import {mkdir,writeFile} from 'node:fs/promises';
import {resolve} from 'node:path';

// A camera canvas must not acquire a second, implicit DOM scroll transform.
// Exercise the production CSS/geometry/handles with real browser CTMs and mouse
// events; the layout callback is identity because this fixture has no edges.
export async function verifyCanvasCoordinates(page,{artifactDir=process.env.SCREENSHOT_DIR}={}) {
  const report={cases:[],assertions:0};
  const check=(condition,message)=>{report.assertions++;assert.ok(condition,message);};
  const near=(actual,expected,message,epsilon=.025)=>check(Math.abs(actual-expected)<=epsilon,
    `${message}: ${actual} versus ${expected}`);
  try {
    const initial=await page.evaluate(async()=>{
      const [{CanvasEditing},{ScenePresentation},css]=await Promise.all([
        import(new URL('uml/canvas-editing.js',document.baseURI)),
        import(new URL('uml/gpu/scene.js',document.baseURI)),
        fetch(new URL('uml/preview.css',document.baseURI)).then(response=>{
          if(!response.ok)throw new Error('Canvas coordinate fixture stylesheet failed: '+response.status);
          return response.text();
        })
      ]);
      const host=document.createElement('div');host.id='unouml-canvas-coordinate-fixture';
      host.style.cssText='position:fixed;left:24px;top:24px;width:509px;height:360px;z-index:2147483647;';
      const root=host.attachShadow({mode:'open'}),style=document.createElement('style');style.textContent=css;
      root.innerHTML='<div class="surface" tabindex="0" data-tool="select"><div class="paper">'+
        '<svg xmlns="http://www.w3.org/2000/svg" width="480" height="360" viewBox="0 0 480 360">'+
        '<g class="entity" data-qualified-name="A" data-uml-hit="fixture"><rect x="32" y="24" width="92" height="40" fill="#d9d1ff"/></g>'+
        '</svg></div></div>';
      root.prepend(style);document.body.append(host);
      const surface=root.querySelector('.surface'),paper=root.querySelector('.paper'),svg=paper.firstElementChild;
      paper.style.transform='translate(360px,32px) scale(1.65)';
      const view={host,surface,paper,svg,z:1.65,x:360,y:32,version:1,autoFit:false,
        marked:svg.querySelector('.entity'),additional:[],index:{hits:new Map()},
        announcement:document.createElement('span'),rect:()=>surface.getBoundingClientRect()};
      const canvas=new CanvasEditing(view,json=>JSON.stringify(JSON.parse(json).state));view.canvas=canvas;
      const presentation=new ScenePresentation(view,()=>{});presentation.svg=svg;
      view.position=()=>{
        paper.style.transform=`translate(${view.x}px,${view.y}px) scale(${view.z})`;
        canvas.position();presentation.readCamera();
      };
      view.emit=(kind,value)=>{if(kind==='canvasLayout'){canvas.pendingCommit.accepted=true;view.committed=JSON.parse(value.json);}};
      view.hit=element=>{view.marked=element;canvas.position();};
      canvas.attach();canvas.scene.state.settings.snapToGrid=false;canvas.scene.state.settings.snapToObjects=false;
      const pointerDown=event=>{
        if(!event.target.closest('[data-canvas-handle]'))return;
        if(canvas.pointerDown(event,view.marked,'select'))surface.setPointerCapture(event.pointerId);
      };
      surface.addEventListener('pointerdown',pointerDown);
      surface.addEventListener('pointermove',event=>canvas.pointerMove(event));
      surface.addEventListener('pointerup',event=>canvas.pointerUp(event,false));
      surface.addEventListener('pointercancel',event=>canvas.pointerUp(event,true));
      const snapshot=()=>{
        const node=canvas.scene.state.nodes[0],bounds=view.marked.getBoundingClientRect();
        const handle=root.querySelector('[data-canvas-handle="resize:se"]').getBoundingClientRect();
        presentation.readCamera();const matrix=presentation.camera.camera,rect=surface.getBoundingClientRect();
        return {scroll:[surface.scrollLeft,surface.scrollTop],overflow:getComputedStyle(surface).overflow,
          node:{...node},bounds:{x:bounds.x,y:bounds.y,width:bounds.width,height:bounds.height},
          handle:{x:handle.x+handle.width/2,y:handle.y+handle.height/2},camera:[...matrix],
          projected:{x:rect.x+matrix[0]*(node.x+node.width)+matrix[2]*(node.y+node.height)+matrix[4],
            y:rect.y+matrix[1]*(node.x+node.width)+matrix[3]*(node.y+node.height)+matrix[5]}};
      };
      globalThis.__unoumlCanvasCoordinateFixture={host,root,surface,paper,view,canvas,presentation,snapshot,
        close(){canvas.cancel();presentation.dispose();host.remove();delete globalThis.__unoumlCanvasCoordinateFixture;}};
      return snapshot();
    });
    check(initial.overflow==='clip','Production surface clips without becoming a DOM scroll container');

    const hidden=await page.evaluate(()=>{
      const f=globalThis.__unoumlCanvasCoordinateFixture;
      f.surface.style.overflow='hidden';f.surface.scrollLeft=116;f.surface.scrollTop=27;f.view.position();
      return f.snapshot();
    });
    near(hidden.scroll[0],116,'Negative control has a real horizontal DOM scroll offset');
    near(hidden.scroll[1],27,'Negative control has a real vertical DOM scroll offset');
    near(hidden.projected.x-hidden.handle.x,116,'Hidden overflow applies horizontal scroll to absolute handles twice');
    near(hidden.projected.y-hidden.handle.y,27,'Hidden overflow applies vertical scroll to absolute handles twice');
    report.cases.push({name:'hidden-overflow negative control',...hidden});

    const isolated=await page.evaluate(()=>{
      const f=globalThis.__unoumlCanvasCoordinateFixture;
      f.surface.style.removeProperty('overflow');f.view.position();
      const before=f.snapshot();
      f.surface.scrollLeft=116;f.surface.scrollTop=27;
      const button=f.root.querySelector('[data-canvas-handle="resize:se"]');
      button.focus();button.scrollIntoView({block:'nearest',inline:'nearest'});
      f.view.position();return {before,after:f.snapshot()};
    });
    check(isolated.after.scroll.every(value=>value===0),'Script, focus and scrollIntoView cannot pan the camera surface');
    check(isolated.after.camera.every((value,index)=>value===isolated.before.camera[index]),
      'Implicit DOM scroll attempts preserve the compiler camera exactly');
    near(isolated.after.handle.x,isolated.after.projected.x,'Resize handle and compiler camera share horizontal screen coordinates');
    near(isolated.after.handle.y,isolated.after.projected.y,'Resize handle and compiler camera share vertical screen coordinates');
    near(isolated.after.bounds.x+isolated.after.bounds.width,isolated.after.projected.x,'Compiler camera matches SVG right edge');
    near(isolated.after.bounds.y+isolated.after.bounds.height,isolated.after.projected.y,'Compiler camera matches SVG bottom edge');
    report.cases.push({name:'camera owns all panning',...isolated});

    const before=await page.evaluate(()=>{
      const f=globalThis.__unoumlCanvasCoordinateFixture;
      f.view.x=40;f.view.y=35;f.paper.style.transform='translate(40px,35px) scale(1.65)';f.view.position();
      return f.snapshot();
    });
    await page.mouse.move(before.handle.x,before.handle.y);
    await page.mouse.down();
    await page.mouse.move(before.handle.x+48,before.handle.y+27,{steps:8});
    await page.mouse.up();
    const after=await page.evaluate(()=>{
      const f=globalThis.__unoumlCanvasCoordinateFixture;return {...f.snapshot(),committed:f.view.committed};
    });
    check(after.committed?.nodes.length===1,'A real resize commits exactly one node transaction');
    near(after.node.width,before.node.width+48/1.65,'Horizontal resize preserves the pointer-to-world displacement');
    near(after.node.height,before.node.height+27/1.65,'Vertical resize preserves the pointer-to-world displacement');
    near(after.node.x,before.node.x,'Southeast resize keeps its opposite x anchor');
    near(after.node.y,before.node.y,'Southeast resize keeps its opposite y anchor');
    near(after.handle.x,after.projected.x,'Committed resize handle remains aligned with the compiler camera');
    near(after.handle.y,after.projected.y,'Committed resize handle remains aligned vertically');
    check(after.scroll.every(value=>value===0),'Resize and expanded viewBox do not introduce DOM scrolling');
    report.cases.push({name:'real pointer resize',before,after});
    console.log(`Verified ${report.assertions} canvas coordinate assertions, including implicit-scroll negative control and real pointer resize.`);
    return report;
  } finally {
    await page.evaluate(()=>globalThis.__unoumlCanvasCoordinateFixture?.close()).catch(()=>{});
    if(artifactDir){await mkdir(artifactDir,{recursive:true});await writeFile(resolve(artifactDir,'canvas-coordinates.json'),JSON.stringify(report,null,2));}
  }
}
