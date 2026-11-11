import { activateAccessibility, clickControl } from './native-controls.mjs';
import assert from 'node:assert/strict';
import {mkdir,readFile} from 'node:fs/promises';
import {resolve} from 'node:path';

export async function verifyCanvasEditing(page,source) {
  await page.setViewportSize({width:1480,height:960});
  const paper=page.locator('.paper').first(),surface=page.locator('.surface').first();
  const surfaceElement=await surface.elementHandle(),sourceElement=await source.elementHandle();
  const shape=id=>paper.locator(`[data-canvas-node="${id}"]`);
  const input='@startuml\nclass A\nclass B\nclass C\nA --> B : request\nB --> C : response\n@enduml';
  const metadata=async()=>JSON.parse((await source.inputValue()).replace(/^\uFEFF/,'').split('\n')[0].slice("' @unouml:canvas ".length));
  const waitSource=async expected=>page.waitForFunction(({input,expected})=>input.value===expected,{input:await source.elementHandle(),expected});
  const waitMetadata=expected=>page.waitForFunction(({input,expected})=>{
    try {
      const state=JSON.parse(input.value.replace(/^\uFEFF/,'').split('\n')[0].slice("' @unouml:canvas ".length));
      return Object.entries(expected.settings||{}).every(([key,value])=>state.settings[key]===value)&&
        (expected.nodes||[]).every(({id,...values})=>{
          const node=state.nodes.find(node=>node.id===id);
          return node&&Object.entries(values).every(([key,value])=>node[key]===value);
        });
    } catch { return false; }
  },{input:sourceElement,expected});
  // Resolve the current SVG through the persistent surface on every poll. Source
  // history and semantic edits can replace the SVG before saved geometry is applied.
  const measure=locator=>locator.evaluate(node=>{
    const {x,y,width,height}=node.getBoundingClientRect();return {x,y,width,height};
  });
  const waitBounds=async(id,expected)=>{
    try {
      await page.waitForFunction(({surface,id,expected})=>{
        const node=[...surface.querySelectorAll('[data-canvas-node]')].find(node=>node.dataset.canvasNode===id);
        if(!node)return false;
        const actual=node.getBoundingClientRect();
        return actual.width>0&&actual.height>0&&['x','y','width','height'].every(key=>Math.abs(actual[key]-expected[key])<0.25);
      },{surface:surfaceElement,id,expected});
    } catch(cause) {
      const observed=await surface.evaluate((surface,id)=>{
        const node=[...surface.querySelectorAll('[data-canvas-node]')].find(node=>node.dataset.canvasNode===id);
        const svg=node?.ownerSVGElement,paper=surface.querySelector('.paper'),rect=node?.getBoundingClientRect();
        return {bounds:rect?{x:rect.x,y:rect.y,width:rect.width,height:rect.height}:null,
          viewBox:svg?.getAttribute('viewBox'),svgWidth:svg?.getAttribute('width'),svgHeight:svg?.getAttribute('height'),
          nodeTransform:node?.getAttribute('transform'),paperTransform:paper?.style.transform};
      },id).catch(error=>({diagnosticError:error.message}));
      throw new Error(`Canvas bounds for ${id}: expected ${JSON.stringify(expected)}; observed ${JSON.stringify(observed)}`,{cause});
    }
  };
  const menu=async title=>{await clickControl(page, page.getByRole('button',{name:'Arrange canvas',exact:true}));await clickControl(page, page.getByRole('menuitem', {name:title,exact:true}));};
  const drag=async(locator,dx,dy,modifiers=[])=>{
    const box=await measure(locator);assert.ok(box);const x=box.x+box.width/2,y=box.y+box.height/2;
    for(const key of modifiers)await page.keyboard.down(key);
    await page.mouse.move(x,y);await page.mouse.down();await page.mouse.move(x+dx,y+dy,{steps:8});await page.mouse.up();
    for(const key of modifiers.reverse())await page.keyboard.up(key);
  };
  await source.fill(input);await shape('A').waitFor();await clickControl(page, page.getByRole('button',{name:'Fit diagram',exact:true}));
  assert.equal(await paper.getAttribute('data-renderer'),'managed');
  // Use the same DOM geometry API for snapshots and polling. Chromium's SVG
  // box-model protocol used by boundingBox() need not share these stroke bounds.
  const originalBounds=await measure(shape('A'));assert.ok(originalBounds);
  await drag(shape('A'),91,55,['Alt']);
  const movedSource=await source.inputValue(),moved=await metadata();
  assert.equal(movedSource.split('\n').slice(1).join('\n'),input,'Free movement preserves semantic source verbatim');
  assert.equal(moved.nodes.length,3);assert.equal(moved.edges.length,2);
  assert.ok(moved.edges.every(e=>e.waypoints.length>=2),'Incident connectors have complete docked paths');
  const at=await measure(shape('A'));
  await surface.press('Control+z');await waitSource(input);await waitBounds('A',originalBounds);
  assert.equal(await source.inputValue(),input,'One undo removes a complete pointer gesture');
  await surface.press('Control+y');await waitSource(movedSource);await waitBounds('A',at);
  assert.equal(await source.inputValue(),movedSource);
  const restored=await measure(shape('A'));
  assert.ok(Math.abs(restored.x-at.x)<1&&Math.abs(restored.y-at.y)<1,'Redo restores visible geometry without drifting the camera');

  await clickControl(page, shape('A'));
  const beforeResize=await metadata(),oldA=beforeResize.nodes.find(n=>n.id==='A');
  await drag(page.locator('[data-canvas-handle="resize:se"]'),48,27,['Alt']);
  const resized=await metadata(),newA=resized.nodes.find(n=>n.id==='A');
  assert.ok(newA.width>oldA.width+10&&newA.height>oldA.height+10,'Resize handles change persisted dimensions');
  const resizedSource=await source.inputValue(),resizedBounds=await measure(shape('A'));
  const unit=await shape('A').evaluate(node=>{const matrix=node.ownerSVGElement.getScreenCTM();return {x:matrix.a,y:matrix.b};});
  await surface.press('ArrowRight');
  await waitMetadata({nodes:[{id:'A',x:newA.x+1}]});
  await waitBounds('A',{...resizedBounds,x:resizedBounds.x+unit.x,y:resizedBounds.y+unit.y});
  const nudged=await metadata();assert.equal(nudged.nodes.find(n=>n.id==='A').x,newA.x+1,'Arrow nudge uses world coordinates');
  await surface.press('Control+z');await waitSource(resizedSource);await waitBounds('A',resizedBounds);
  assert.equal((await metadata()).nodes.find(n=>n.id==='A').x,newA.x,'A keyboard nudge is atomic');

  await clickControl(page, shape('A'));await menu('Pin selection for automatic layout');
  const pinned=await metadata(),fixed=pinned.nodes.find(n=>n.id==='A');assert.equal(fixed.pinned,true);
  await menu('Automatic layout…');
  let dialog=page.getByRole('dialog',{name:'Automatic layout',exact:true});
  await clickControl(page, dialog.getByRole('combobox',{name:'Layout algorithm',exact:true}));
  await clickControl(page, page.getByRole('listbox',{name:'Layout algorithm',exact:true}).getByRole('option',{name:'Grid',exact:true}));
  await clickControl(page, dialog.getByRole('combobox',{name:'Layout scope',exact:true}));
  await clickControl(page, page.getByRole('listbox',{name:'Layout scope',exact:true}).getByRole('option',{name:'Entire diagram',exact:true}));
  if(process.env.SCREENSHOT_DIR) {
    await mkdir(process.env.SCREENSHOT_DIR,{recursive:true});
    await page.screenshot({path:resolve(process.env.SCREENSHOT_DIR,'canvas-authoring.png')});
  }
  await clickControl(page, dialog.getByRole('button',{name:'Apply',exact:true}));await dialog.waitFor({state:'hidden'});
  await waitMetadata({settings:{algorithm:'grid'}});
  const laidOut=await metadata(),afterA=laidOut.nodes.find(n=>n.id==='A');
  assert.deepEqual([afterA.x,afterA.y,afterA.width,afterA.height],[fixed.x,fixed.y,fixed.width,fixed.height],'Pinned geometry survives whole-diagram automatic layout');
  assert.equal(laidOut.settings.algorithm,'grid');

  await clickControl(page, shape('B'));await menu('Lock selection');
  const locked=await source.inputValue();await surface.press('ArrowRight');await surface.press('Delete');
  assert.equal(await source.inputValue(),locked,'Locked shapes resist keyboard move and canvas delete');
  await menu('Unlock selection');
  await menu('Exact position, size and rotation…');
  dialog=page.getByRole('dialog',{name:'Exact canvas geometry',exact:true});
  await dialog.getByRole('textbox',{name:'X position',exact:true}).fill('-100');
  await dialog.getByRole('textbox',{name:'Y position',exact:true}).fill('180');
  await dialog.getByRole('textbox',{name:'Rotation in degrees',exact:true}).fill('30');
  await clickControl(page, dialog.getByRole('button',{name:'Apply',exact:true}));await dialog.waitFor({state:'hidden'});
  await waitMetadata({nodes:[{id:'B',x:-100,y:180,rotation:30}]});
  const exact=await metadata(),exactB=exact.nodes.find(n=>n.id==='B');
  assert.equal(exactB.x,-100);assert.equal(exactB.y,180);assert.equal(exactB.rotation,30);
  assert.ok(Number((await paper.locator('svg').first().getAttribute('viewBox')).split(' ')[0])<0,'Negative placement expands the exported viewBox');

  const beforeSemantic=await measure(shape('B')),oldDrawing=await paper.locator('svg').first().elementHandle();
  const renamedSource=(await source.inputValue()).replace('class C','class "Renamed C" as C');
  await source.fill(renamedSource);
  await page.waitForFunction(el=>!el.isConnected,oldDrawing);
  await shape('C').locator('text').filter({hasText:/^Renamed C$/}).waitFor();
  await waitBounds('B',beforeSemantic);await waitSource(renamedSource);
  const afterSemantic=await measure(shape('B'));
  assert.ok(Math.abs(beforeSemantic.x-afterSemantic.x)<1&&Math.abs(beforeSemantic.y-afterSemantic.y)<1,'A semantic re-render preserves the camera origin after negative placement');

  const beforeEscape=await source.inputValue(),box=await measure(shape('C'));
  await page.mouse.move(box.x+box.width/2,box.y+box.height/2);await page.mouse.down();await page.mouse.move(box.x+box.width/2+25,box.y+box.height/2+20,{steps:4});
  await page.keyboard.press('Escape');await page.mouse.up();assert.equal(await source.inputValue(),beforeEscape,'Escape cancels a preview without history');

  const downloaded=page.waitForEvent('download');await clickControl(page, page.getByRole('button',{name:'Export',exact:true}));
  await clickControl(page, page.getByRole('menuitem', {name:'Vector image (.svg)',exact:true}));
  const file=await downloaded,svg=await readFile(await file.path(),'utf8');
  assert.match(svg,/data-qualified-name="B"/);assert.match(svg,/transform="matrix\(/);assert.doesNotMatch(svg,/data-uml-hit|data-canvas-(?:node|edge)|canvas-handle|multi-mark/,'SVG export contains geometry, not editing controls');

  // The live Order-domain diagram exposed endpoint captions hidden under nodes
  // after Grid turns a vertical association into an orthogonal cross-row route.
  const cardinalities='@startuml\nclass Order {\n+id : UUID\n+total() : Money\n}\nclass LineItem {\n+quantity : int\n}\nOrder "1" *-- "many" LineItem : contains\nstruct Struct\nclass Class\nabstract class Abstractclass\n@enduml';
  await source.fill(cardinalities);await shape('Order').waitFor();
  const sourceCaption=paper.locator('[data-canvas-label-role="source"]'),targetCaption=paper.locator('[data-canvas-label-role="target"]');
  await sourceCaption.waitFor();await targetCaption.waitFor();
  await menu('Automatic layout…');dialog=page.getByRole('dialog',{name:'Automatic layout',exact:true});
  await clickControl(page, dialog.getByRole('combobox',{name:'Layout algorithm',exact:true}));
  await clickControl(page, page.getByRole('listbox',{name:'Layout algorithm',exact:true}).getByRole('option',{name:'Grid',exact:true}));
  await clickControl(page, dialog.getByRole('button',{name:'Apply',exact:true}));await dialog.waitFor({state:'hidden'});
  await waitMetadata({settings:{algorithm:'grid'}});
  const routedCaptions=await source.inputValue();
  assert.equal(routedCaptions.slice(routedCaptions.indexOf('\n')+1),cardinalities);
  // This fixture is wider than the preceding three-node scene. Fit it before
  // pointer actions so Playwright need not scroll the clipped canvas to a label.
  await clickControl(page, page.getByRole('button',{name:'Fit diagram',exact:true}));
  const intersects=(a,b)=>a.x<b.x+b.width-.1&&a.x+a.width>b.x+.1&&a.y<b.y+b.height-.1&&a.y+a.height>b.y+.1;
  const captionBefore=[await measure(sourceCaption),await measure(targetCaption)];
  for(const caption of captionBefore)for(const id of ['Order','LineItem','Struct','Class','Abstractclass'])
    assert.equal(intersects(caption,await measure(shape(id))),false,`Endpoint caption must remain outside ${id}`);
  for(const caption of [sourceCaption,targetCaption]) {
    const matrix=await caption.evaluate(node=>{const m=node.transform.baseVal.consolidate().matrix;return [m.a,m.b,m.c,m.d];});
    assert.ok(matrix.every((value,i)=>Math.abs(value-[1,0,0,1][i])<1e-6),'Endpoint text remains upright when its attachment turns');
  }
  await clickControl(page, paper.locator('[data-canvas-label-role="center"] text').filter({hasText:/^contains$/}));
  const moveLabel=page.getByRole('button',{name:'Move connector label',exact:true});await moveLabel.waitFor();
  const endpointsBeforeDrag=[await measure(sourceCaption),await measure(targetCaption)];
  const labelMatrix=caption=>caption.evaluate(node=>{const m=node.transform.baseVal.consolidate().matrix;return [m.a,m.b,m.c,m.d,m.e,m.f];});
  const endpointTransforms=await Promise.all([sourceCaption,targetCaption].map(labelMatrix));
  const beforeLabelState=await metadata();
  await drag(moveLabel,28,-18,['Alt']);
  const afterLabelState=await metadata();
  assert.deepEqual(afterLabelState.nodes,beforeLabelState.nodes);
  assert.deepEqual(afterLabelState.edges.map(e=>e.waypoints),beforeLabelState.edges.map(e=>e.waypoints));
  for(const [i,caption] of [sourceCaption,targetCaption].entries()) {
    const actual=await measure(caption);
    assert.ok((await labelMatrix(caption)).every((value,j)=>Math.abs(value-endpointTransforms[i][j])<1e-7),'Central label drag preserves endpoint world transforms');
    for(const key of ['x','y','width','height'])assert.ok(Math.abs(actual[key]-endpointsBeforeDrag[i][key])<.25,
      `Central label drag shifted endpoint ${i}: before ${JSON.stringify(endpointsBeforeDrag[i])}; after ${JSON.stringify(actual)}`);
  }
  await surface.press('Control+z');await waitSource(routedCaptions);
  await surface.press('Control+z');await waitSource(cardinalities);
  console.log('Verified endpoint caption ownership, upright route turns, node clearance, independent central-label movement and exact undo.');

  // Nested DOM ordering and collapse projection must not corrupt child geometry or manual paths.
  const grouped='@startuml\npackage ZGroup {\n class AChild\n class BChild\n AChild --> BChild : inner\n}\nclass Outside\nAChild --> Outside : outer\n@enduml';
  await source.fill(grouped);await shape('ZGroup').waitFor();await clickControl(page, page.getByRole('button',{name:'Fit diagram',exact:true}));
  const beforeChild=await measure(shape('ZGroup.AChild')),group=shape('ZGroup').locator('text').filter({hasText:'ZGroup'}).first();
  await drag(group,42,24,['Alt']);
  const afterChild=await measure(shape('ZGroup.AChild'));assert.ok(Math.abs(afterChild.x-beforeChild.x-42)<2&&Math.abs(afterChild.y-beforeChild.y-24)<2,'Group drag translates children exactly once');
  await clickControl(page, shape('ZGroup.AChild'));await menu('Lock selection');
  await clickControl(page, group);const childLocked=await source.inputValue();await surface.press('Delete');
  assert.equal(await source.inputValue(),childLocked,'Deleting a container cannot bypass a child lock');
  await clickControl(page, shape('ZGroup.AChild'));await menu('Unlock selection');await clickControl(page, group);
  const routes=(await metadata()).edges.map(e=>e.waypoints);
  await menu('Collapse selected containers');assert.equal(await shape('ZGroup.AChild').isVisible(),false);
  await menu('Expand selected containers');assert.equal(await shape('ZGroup.AChild').isVisible(),true);
  assert.deepEqual((await metadata()).edges.map(e=>e.waypoints),routes,'Collapse is a transient projection and retains logical routes');

  const activity='@startuml\nstart\nif (Ready?) then (yes)\n:Accept;\nelse (no)\n:Reject;\nendif\n:Continue;\nstop\n@enduml';
  await source.fill(activity);await paper.locator('.activity-Join').waitFor();
  await clickControl(page, page.getByRole('button',{name:'Fit diagram',exact:true}));
  const join=paper.locator('.activity-Join'),decision=paper.locator('.activity-Decision');
  const joinBefore=await measure(join),decisionBefore=await measure(decision);
  await drag(join,31,23,['Alt']);
  const joinAfter=await measure(join),decisionAfter=await measure(decision);
  assert.ok(Math.abs(joinAfter.x-joinBefore.x-31)<2&&Math.abs(joinAfter.y-joinBefore.y-23)<2,'A closing junction moves independently of its source construct');
  assert.ok(Math.abs(decisionAfter.x-decisionBefore.x)<1&&Math.abs(decisionAfter.y-decisionBefore.y)<1,'Moving a join does not move the decision header');
  const accept=paper.locator('.activity-Action').filter({hasText:'Accept'});
  await clickControl(page, accept);await clickControl(page, join, {modifiers:['Shift']});
  assert.equal(await paper.locator('.multi-mark').count(),1,'Activity actions and closing junctions retain distinct visual multi-selection');
  await surface.press('Escape');assert.equal(await page.locator('[data-canvas-handle]').count(),0,'Escape clears a visual-only selection');
  await menu('Select all shapes');
  assert.equal(await paper.locator('.multi-mark').count(),(await metadata()).nodes.length-1,'Select all includes generated junctions and endpoints');
  await surface.press('Escape');

  const caretProjection=await page.evaluate(async()=>{
    const {syncSourceText,removeSourceText}=await import(new URL('uml/source-text.js',document.baseURI));
    const root=document.createElement('div'),input=document.createElement('textarea');
    root.id='unouml-source-text-projection-check';root.hidden=true;input.setAttribute('xamlautomationid',root.id);root.append(input);document.body.append(root);
    try {
      const results=[];
      for(const [start,end,direction,managedStart,managedLength] of [[4,4,'forward',6,0],[1,3,'backward',2,3]]) {
        input.value='a\nbc';input.setSelectionRange(start,end,direction);
        syncSourceText(root.id,JSON.stringify('\uFEFFa\r\nbc'),false,managedStart,managedLength);
        results.push([input.value,input.selectionStart,input.selectionEnd,input.selectionDirection]);
      }
      return results;
    } finally {removeSourceText(root.id);root.remove();}
  });
  assert.deepEqual(caretProjection,[['\uFEFFa\nbc',5,5,'forward'],['\uFEFFa\nbc',2,4,'backward']],
    'Native BOM repair maps end/interior UTF-16 selections and normalizes only the textarea projection');

  // A BOM remains at file start while the geometry comment moves before @start.
  // Rebuild literal offsets when that changes the renderer's page-relative input.
  const bom='\uFEFF@startdot\ndigraph G { A [label="Original"]; B; A -> B; }\n@enddot';
  await source.fill(bom);await shape('node:A').waitFor();await clickControl(page, page.getByRole('button',{name:'Fit diagram',exact:true}));
  await waitSource(bom);
  assert.equal(await source.inputValue(),bom,'The native source editor retains the BOM before canvas edits');
  const bomDrawing=await paper.locator('svg').first().elementHandle(),bomBefore=await measure(shape('node:A'));
  await drag(shape('node:A'),24,16,['Alt']);
  await page.waitForFunction(el=>!el.isConnected,bomDrawing);
  const bomMoved={...bomBefore,x:bomBefore.x+24,y:bomBefore.y+16};
  await waitBounds('node:A',bomMoved);
  const beforeLabel=await source.inputValue();
  assert.ok(beforeLabel.startsWith('\uFEFF' + "' @unouml:canvas "),'The native editor preserves a BOM ahead of inserted geometry metadata');
  assert.equal(beforeLabel.slice(beforeLabel.indexOf('\n')+1),bom.slice(1),'Geometry metadata preserves every semantic source character');
  await surface.press('Control+z');await waitSource(bom);await waitBounds('node:A',bomBefore);
  await surface.press('Control+y');await waitSource(beforeLabel);await waitBounds('node:A',bomMoved);
  await shape('node:A').locator('text').filter({hasText:/^Original$/}).dblclick();
  const label=page.getByRole('textbox',{name:'Edit diagram label',exact:true});await label.waitFor();
  await label.fill('After geometry');await label.press('Control+Enter');await label.waitFor({state:'hidden'});
  const afterLabel=beforeLabel.replace('A [label="Original"]','A [label="After geometry"]');
  await waitSource(afterLabel);
  await shape('node:A').locator('text').filter({hasText:/^After geometry$/}).waitFor();
  assert.ok((await source.inputValue()).startsWith('\uFEFF'),'Canvas authoring preserves the document BOM');
  assert.match(await source.inputValue(),/A \[label="After geometry"\]/,'BOM geometry edits retain valid exact-source label offsets');
  await surface.press('Control+z');await waitSource(beforeLabel);
  await shape('node:A').locator('text').filter({hasText:/^Original$/}).waitFor();
  await surface.press('Control+y');await waitSource(afterLabel);
  await shape('node:A').locator('text').filter({hasText:/^After geometry$/}).waitFor();

  const saveSource=async expected=>{
    const pending=page.waitForEvent('download');
    await clickControl(page, page.getByRole('button',{name:'Save',exact:true}));
    const download=await pending;
    assert.deepEqual(await readFile(await download.path()),Buffer.from(expected,'utf8'),'Save preserves the exact UTF-8 BOM and source bytes');
    await page.waitForFunction(expected=>{
      const draft=JSON.parse(localStorage.getItem('UnoUML:unouml.document.v1'));
      return draft?.Source===expected&&draft.Dirty===false;
    },expected);
  };
  await saveSource(afterLabel);

  // Textarea values normalize CRLF to LF; document, draft and download bytes do
  // not. Exercise a real file picker so the file decoder and native projection
  // both have to preserve the BOM without normalizing the managed document.
  const crlf=afterLabel.replace(/\n/g,'\r\n'),name='BOM-roundtrip.puml';
  const choosing=page.waitForEvent('filechooser');
  await clickControl(page, page.getByRole('button',{name:'Open',exact:true}));
  await (await choosing).setFiles({name,mimeType:'text/plain',buffer:Buffer.from(crlf,'utf8')});
  await page.waitForFunction(({name,source})=>{
    const draft=JSON.parse(localStorage.getItem('UnoUML:unouml.document.v1'));
    return draft?.FileName===name&&draft.Source===source;
  },{name,source:crlf});
  await waitSource(afterLabel);
  await saveSource(crlf);
  await page.reload({waitUntil:'domcontentloaded'});
  await paper.locator('svg').first().waitFor({state:'visible',timeout:120000});
  await activateAccessibility(page);
  await clickControl(page, page.getByRole('button',{name:'Split',exact:true}));await source.waitFor({state:'visible'});
  await waitSource(afterLabel);
  assert.equal(await page.evaluate(()=>JSON.parse(localStorage.getItem('UnoUML:unouml.document.v1')).Source),crlf,'Draft reload preserves BOM and CRLF bytes');
  await shape('node:A').locator('text').filter({hasText:/^After geometry$/}).waitFor();
  await source.press('Control+Home');await source.press('Delete');await waitSource(afterLabel.slice(1));
  console.log('Verified free geometry, atomic undo, auto layout, source-map identity, compound collapse, activity selection, BOM editing, byte-exact Save/Open/draft reload and intentional BOM removal.');
}
