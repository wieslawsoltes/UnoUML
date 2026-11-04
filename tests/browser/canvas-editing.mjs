import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';

export async function verifyCanvasEditing(page,source) {
  await page.setViewportSize({width:1480,height:960});
  const paper=page.locator('.paper').first(),surface=page.locator('.surface').first();
  const shape=id=>paper.locator(`[data-canvas-node="${id}"]`);
  const input='@startuml\nclass A\nclass B\nclass C\nA --> B : request\nB --> C : response\n@enduml';
  const metadata=async()=>JSON.parse((await source.inputValue()).replace(/^\uFEFF/,'').split('\n')[0].slice("' @unouml:canvas ".length));
  const menu=async title=>{await page.getByRole('button',{name:'Arrange canvas',exact:true}).click();await page.getByText(title,{exact:true}).last().click();};
  const drag=async(locator,dx,dy,modifiers=[])=>{
    const box=await locator.boundingBox();assert.ok(box);const x=box.x+box.width/2,y=box.y+box.height/2;
    for(const key of modifiers)await page.keyboard.down(key);
    await page.mouse.move(x,y);await page.mouse.down();await page.mouse.move(x+dx,y+dy,{steps:8});await page.mouse.up();
    for(const key of modifiers.reverse())await page.keyboard.up(key);
  };
  await source.fill(input);await shape('A').waitFor();await page.getByRole('button',{name:'Fit diagram',exact:true}).click();
  assert.equal(await paper.getAttribute('data-renderer'),'managed');
  await drag(shape('A'),91,55,['Alt']);
  const movedSource=await source.inputValue(),moved=await metadata();
  assert.equal(movedSource.split('\n').slice(1).join('\n'),input,'Free movement preserves semantic source verbatim');
  assert.equal(moved.nodes.length,3);assert.equal(moved.edges.length,2);
  assert.ok(moved.edges.every(e=>e.waypoints.length>=2),'Incident connectors have complete docked paths');
  const at=await shape('A').boundingBox();
  await surface.press('Control+z');assert.equal(await source.inputValue(),input,'One undo removes a complete pointer gesture');
  await surface.press('Control+y');assert.equal(await source.inputValue(),movedSource);
  const restored=await shape('A').boundingBox();
  assert.ok(Math.abs(restored.x-at.x)<1&&Math.abs(restored.y-at.y)<1,'Redo restores visible geometry without drifting the camera');

  await shape('A').click();
  const beforeResize=await metadata(),oldA=beforeResize.nodes.find(n=>n.id==='A');
  await drag(page.locator('[data-canvas-handle="resize:se"]'),48,27,['Alt']);
  const resized=await metadata(),newA=resized.nodes.find(n=>n.id==='A');
  assert.ok(newA.width>oldA.width+10&&newA.height>oldA.height+10,'Resize handles change persisted dimensions');
  await surface.press('ArrowRight');
  const nudged=await metadata();assert.equal(nudged.nodes.find(n=>n.id==='A').x,newA.x+1,'Arrow nudge uses world coordinates');
  await surface.press('Control+z');assert.equal((await metadata()).nodes.find(n=>n.id==='A').x,newA.x,'A keyboard nudge is atomic');

  await shape('A').click();await menu('Pin selection for automatic layout');
  const pinned=await metadata(),fixed=pinned.nodes.find(n=>n.id==='A');assert.equal(fixed.pinned,true);
  await menu('Automatic layout…');
  let dialog=page.getByRole('dialog',{name:'Automatic layout',exact:true});
  await dialog.getByRole('combobox',{name:'Layout algorithm',exact:true}).click();
  await page.getByRole('listbox',{name:'Layout algorithm',exact:true}).getByRole('option',{name:'Grid',exact:true}).click();
  await dialog.getByRole('combobox',{name:'Layout scope',exact:true}).click();
  await page.getByRole('listbox',{name:'Layout scope',exact:true}).getByRole('option',{name:'Entire diagram',exact:true}).click();
  await dialog.getByRole('button',{name:'Apply',exact:true}).click();await dialog.waitFor({state:'hidden'});
  const laidOut=await metadata(),afterA=laidOut.nodes.find(n=>n.id==='A');
  assert.deepEqual([afterA.x,afterA.y,afterA.width,afterA.height],[fixed.x,fixed.y,fixed.width,fixed.height],'Pinned geometry survives whole-diagram automatic layout');
  assert.equal(laidOut.settings.algorithm,'grid');

  await shape('B').click();await menu('Lock selection');
  const locked=await source.inputValue();await surface.press('ArrowRight');await surface.press('Delete');
  assert.equal(await source.inputValue(),locked,'Locked shapes resist keyboard move and canvas delete');
  await menu('Unlock selection');
  await menu('Exact position, size and rotation…');
  dialog=page.getByRole('dialog',{name:'Exact canvas geometry',exact:true});
  await dialog.getByRole('textbox',{name:'X position',exact:true}).fill('-100');
  await dialog.getByRole('textbox',{name:'Y position',exact:true}).fill('180');
  await dialog.getByRole('textbox',{name:'Rotation in degrees',exact:true}).fill('30');
  await dialog.getByRole('button',{name:'Apply',exact:true}).click();await dialog.waitFor({state:'hidden'});
  const exact=await metadata(),exactB=exact.nodes.find(n=>n.id==='B');
  assert.equal(exactB.x,-100);assert.equal(exactB.y,180);assert.equal(exactB.rotation,30);
  assert.ok(Number((await paper.locator('svg').first().getAttribute('viewBox')).split(' ')[0])<0,'Negative placement expands the exported viewBox');

  const beforeSemantic=await shape('B').boundingBox(),oldDrawing=await paper.locator('svg').first().elementHandle();
  await source.fill((await source.inputValue()).replace('class C','class "Renamed C" as C'));
  await page.waitForFunction(el=>!el.isConnected,oldDrawing);
  await shape('C').getByText('Renamed C',{exact:true}).waitFor();
  const afterSemantic=await shape('B').boundingBox();
  assert.ok(Math.abs(beforeSemantic.x-afterSemantic.x)<1&&Math.abs(beforeSemantic.y-afterSemantic.y)<1,'A semantic re-render preserves the camera origin after negative placement');

  const beforeEscape=await source.inputValue(),box=await shape('C').boundingBox();
  await page.mouse.move(box.x+box.width/2,box.y+box.height/2);await page.mouse.down();await page.mouse.move(box.x+box.width/2+25,box.y+box.height/2+20,{steps:4});
  await page.keyboard.press('Escape');await page.mouse.up();assert.equal(await source.inputValue(),beforeEscape,'Escape cancels a preview without history');

  const downloaded=page.waitForEvent('download');await page.getByRole('button',{name:'Export',exact:true}).click();
  await page.getByText('Vector image (.svg)',{exact:true}).last().click();
  const file=await downloaded,svg=await readFile(await file.path(),'utf8');
  assert.match(svg,/data-qualified-name="B"/);assert.match(svg,/transform="matrix\(/);assert.doesNotMatch(svg,/data-uml-hit|data-canvas-(?:node|edge)|canvas-handle|multi-mark/,'SVG export contains geometry, not editing controls');

  // Nested DOM ordering and collapse projection must not corrupt child geometry or manual paths.
  const grouped='@startuml\npackage ZGroup {\n class AChild\n class BChild\n AChild --> BChild : inner\n}\nclass Outside\nAChild --> Outside : outer\n@enduml';
  await source.fill(grouped);await shape('ZGroup').waitFor();await page.getByRole('button',{name:'Fit diagram',exact:true}).click();
  const beforeChild=await shape('ZGroup.AChild').boundingBox(),group=shape('ZGroup').locator('text').filter({hasText:'ZGroup'}).first();
  await drag(group,42,24,['Alt']);
  const afterChild=await shape('ZGroup.AChild').boundingBox();assert.ok(Math.abs(afterChild.x-beforeChild.x-42)<2&&Math.abs(afterChild.y-beforeChild.y-24)<2,'Group drag translates children exactly once');
  await shape('ZGroup.AChild').click();await menu('Lock selection');
  await group.click();const childLocked=await source.inputValue();await surface.press('Delete');
  assert.equal(await source.inputValue(),childLocked,'Deleting a container cannot bypass a child lock');
  await shape('ZGroup.AChild').click();await menu('Unlock selection');await group.click();
  const routes=(await metadata()).edges.map(e=>e.waypoints);
  await menu('Collapse selected containers');assert.equal(await shape('ZGroup.AChild').isVisible(),false);
  await menu('Expand selected containers');assert.equal(await shape('ZGroup.AChild').isVisible(),true);
  assert.deepEqual((await metadata()).edges.map(e=>e.waypoints),routes,'Collapse is a transient projection and retains logical routes');

  const activity='@startuml\nstart\nif (Ready?) then (yes)\n:Accept;\nelse (no)\n:Reject;\nendif\n:Continue;\nstop\n@enduml';
  await source.fill(activity);await paper.locator('.activity-Join').waitFor();
  await page.getByRole('button',{name:'Fit diagram',exact:true}).click();
  const join=paper.locator('.activity-Join'),decision=paper.locator('.activity-Decision');
  const joinBefore=await join.boundingBox(),decisionBefore=await decision.boundingBox();
  await drag(join,31,23,['Alt']);
  const joinAfter=await join.boundingBox(),decisionAfter=await decision.boundingBox();
  assert.ok(Math.abs(joinAfter.x-joinBefore.x-31)<2&&Math.abs(joinAfter.y-joinBefore.y-23)<2,'A closing junction moves independently of its source construct');
  assert.ok(Math.abs(decisionAfter.x-decisionBefore.x)<1&&Math.abs(decisionAfter.y-decisionBefore.y)<1,'Moving a join does not move the decision header');
  const accept=paper.locator('.activity-Action').filter({hasText:'Accept'});
  await accept.click();await join.click({modifiers:['Shift']});
  assert.equal(await paper.locator('.multi-mark').count(),1,'Activity actions and closing junctions retain distinct visual multi-selection');
  await surface.press('Escape');assert.equal(await page.locator('[data-canvas-handle]').count(),0,'Escape clears a visual-only selection');
  await menu('Select all shapes');
  assert.equal(await paper.locator('.multi-mark').count(),(await metadata()).nodes.length-1,'Select all includes generated junctions and endpoints');
  await surface.press('Escape');

  // A BOM remains at file start while the geometry comment moves before @start.
  // Rebuild literal offsets when that changes the renderer's page-relative input.
  const bom='\uFEFF@startdot\ndigraph G { A [label="Original"]; B; A -> B; }\n@enddot';
  await source.fill(bom);await shape('node:A').waitFor();await page.getByRole('button',{name:'Fit diagram',exact:true}).click();
  const bomDrawing=await paper.locator('svg').first().elementHandle();
  await drag(shape('node:A'),24,16,['Alt']);
  await page.waitForFunction(el=>!el.isConnected,bomDrawing);
  await shape('node:A').getByText('Original',{exact:true}).dblclick();
  const label=page.getByRole('textbox',{name:'Edit diagram label',exact:true});await label.waitFor();
  await label.fill('After geometry');await label.press('Control+Enter');await label.waitFor({state:'hidden'});
  await shape('node:A').getByText('After geometry',{exact:true}).waitFor();
  assert.ok((await source.inputValue()).startsWith('\uFEFF'),'Canvas authoring preserves the document BOM');
  assert.match(await source.inputValue(),/A \[label="After geometry"\]/,'BOM geometry edits retain valid exact-source label offsets');
  console.log('Verified free geometry, atomic undo, auto layout, source-map identity, compound collapse, activity selection and BOM editing.');
}
