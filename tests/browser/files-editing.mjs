import { activateControl, fillControl } from './native-controls.mjs';
import {fitCanvas} from './canvas-frame.mjs';
import {traceNativeInput} from './native-input-trace.mjs';
import assert from 'node:assert/strict';
import {mkdir} from 'node:fs/promises';
import {resolve} from 'node:path';

export async function verifyFilesEditing(page, source) {
  await page.setViewportSize({width:1480,height:960});
  const paper=page.locator('.paper').first(),surface=page.locator('.surface').first();
  const row=path=>paper.locator(`[data-files-path=${JSON.stringify(path)}]`);
  const sourceAutomationId=await source.getAttribute('xamlautomationid');
  assert.ok(sourceAutomationId,'Native source editor exposes its public AutomationId');
  // Native semantic nodes can be replaced when the inspector rebuilds. Resolve
  // the current editor for each poll instead of retaining a detached textarea.
  const waitSource=async expected=>page.waitForFunction(({automationId,expected})=>{
    const input=document.querySelector('textarea[xamlautomationid="'+CSS.escape(automationId)+'"]');
    return input?.isConnected&&input.value===expected;
  },{automationId:sourceAutomationId,expected});
  const split=text=>{
    const bom=text.startsWith('\uFEFF')?'\uFEFF':'',body=text.slice(bom.length),newline=body.indexOf('\n');
    if(!body.startsWith("' @unouml:canvas "))return {state:null,body:text};
    return {state:JSON.parse(body.slice("' @unouml:canvas ".length,newline)),body:bom+body.slice(newline+1)};
  };
  // Native text and the BOM-safe semantic projection settle separately from
  // diagram rendering. Observe the live editor and take one complete snapshot.
  const projectedSource=async expectedBody=>(await page.waitForFunction(({automationId,expectedBody})=>{
    const input=document.querySelector('textarea[xamlautomationid="'+CSS.escape(automationId)+'"]');
    const text=input?.isConnected?input.value:'',newline=text.indexOf('\n');
    if(!text.startsWith("\uFEFF' @unouml:canvas ")||newline<0)return false;
    if(expectedBody!==null&&'\uFEFF'+text.slice(newline+1)!==expectedBody)return false;
    return text;
  },{automationId:sourceAutomationId,expectedBody:expectedBody??null})).jsonValue();
  const snapshot=async expectedBody=>split(await projectedSource(expectedBody));
  const id=async path=>row(path).getAttribute('data-canvas-id');
  const node=async path=>{const identity=await id(path);return (await snapshot()).state.nodes.find(item=>item.id===identity);};
  const waitApplied=async path=>page.waitForFunction(({surface,path})=>{
    const row=[...surface.querySelectorAll('[data-files-path]')].find(row=>row.dataset.filesPath===path);
    return row&&row.dataset.canvasNode===row.dataset.canvasId;
  },{surface:await surface.elementHandle(),path});
  const input='\uFEFF@startfiles\n/src/A.cs\n/src/nested/B.cs\n/docs/\n@endfiles';
  // Retain passive input evidence through the renderer's initial acknowledgement.
  await traceNativeInput(page,source,'files-source-replacement',async()=>{
    await fillControl(page, source, input);
    await row('/src/A.cs').waitFor();
    await fitCanvas(page);
    assert.equal(await paper.getAttribute('data-renderer'),'managed');
  },{failureOnly:true,captureManagedInput:true});
  assert.equal(await paper.locator('[data-files-path]').count(),5,'Implicit directories have distinct selectable rows');
  const oldIds=new Map();
  for(const path of ['/src/','/src/A.cs','/src/nested/','/src/nested/B.cs','/docs/'])oldIds.set(path,await id(path));

  // A real free-canvas gesture creates persisted geometry for every Files row.
  const box=await row('/src/A.cs').evaluate(element=>{const r=element.getBoundingClientRect();return {x:r.x,y:r.y,width:r.width,height:r.height};});
  const x=box.x+box.width/2,y=box.y+box.height/2;
  await page.keyboard.down('Alt');await page.mouse.move(x,y);await page.mouse.down();
  await page.mouse.move(x+85,y+36,{steps:8});await page.mouse.up();await page.keyboard.up('Alt');
  const arrangedSource=await projectedSource(input);
  const arrangedSnapshot=split(arrangedSource),arranged=arrangedSnapshot.state;
  assert.equal(arrangedSnapshot.body,input,'Free placement retains file paths and the BOM');
  assert.equal(arranged.nodes.length,5);

  // The parent is inferred from two separate declarations. Editing its name
  // must rename both paths and all four descendant geometry identities at once.
  await activateControl(page, row('/src/'));await surface.press('F2');
  const editor=page.getByRole('textbox',{name:'Edit diagram label',exact:true});
  await editor.waitFor();assert.equal(await editor.inputValue(),'src');
  await fillControl(page, editor, 'library');await editor.press('Enter');
  await row('/library/nested/B.cs').waitFor();await waitApplied('/library/A.cs');
  const renamedBody=input.replaceAll('/src/','/library/');
  assert.equal((await snapshot(renamedBody)).body,renamedBody);
  for(const [path,oldId] of oldIds){
    const current=path.startsWith('/src/')?'/library/'+path.slice(5):path;
    assert.deepEqual(await node(current),{...arranged.nodes.find(item=>item.id===oldId),id:await id(current)},'Renamed paths keep their exact authored geometry');
  }
  await surface.press('Control+z');await waitSource(arrangedSource);await row('/src/A.cs').waitFor();
  assert.equal(await source.inputValue(),arrangedSource,'One undo restores every path and canvas identity');

  // Inspector move changes semantic parentage while retaining the independent
  // geometry of the nested folder and leaf. A conflicting destination is atomic.
  await activateControl(page, row('/src/nested/'));
  await activateControl(page, page.getByRole('button', {name:'Move file or folder…',exact:true}));
  let dialog=page.getByRole('dialog',{name:'Move file or folder',exact:true});
  await fillControl(page, dialog.getByRole('textbox',{name:'Destination path',exact:true}), '/docs/');
  await activateControl(page, dialog.getByRole('button',{name:'Move',exact:true}));await dialog.waitFor({state:'hidden'});
  await waitSource(arrangedSource);
  assert.equal(await source.inputValue(),arrangedSource,'Existing folders are not silently merged');
  await activateControl(page, row('/src/nested/'));await activateControl(page, page.getByRole('button', {name:'Move file or folder…',exact:true}));
  dialog=page.getByRole('dialog',{name:'Move file or folder',exact:true});
  await fillControl(page, dialog.getByRole('textbox',{name:'Destination path',exact:true}), '/docs/models/');
  await activateControl(page, dialog.getByRole('button',{name:'Move',exact:true}));await dialog.waitFor({state:'hidden'});
  await row('/docs/models/B.cs').waitFor();
  const movedBody=input.replace('/src/nested/B.cs','/docs/models/B.cs');
  assert.equal((await snapshot(movedBody)).body,movedBody);
  assert.deepEqual(await node('/docs/models/B.cs'),{...arranged.nodes.find(item=>item.id===oldIds.get('/src/nested/B.cs')),id:await id('/docs/models/B.cs')});
  await surface.press('Control+z');await waitSource(arrangedSource);await row('/src/nested/').waitFor();

  // Exercise OS clipboard events using real keyboard shortcuts. The copy is a
  // self-contained subtree; pasting into /docs/ remaps every canvas ID.
  await activateControl(page, row('/src/nested/'));await surface.press('Control+c');
  await activateControl(page, row('/docs/'));await surface.press('Control+v');
  await row('/docs/nested/B.cs').waitFor();
  const pastedBody=input.replace('@endfiles','/docs/nested/B.cs\n@endfiles');
  const pastedSource=await projectedSource(pastedBody),pastedSnapshot=split(pastedSource),pasted=pastedSnapshot.state;
  assert.equal(pastedSnapshot.body,pastedBody);
  for(const [oldPath,newPath] of [['/src/nested/','/docs/nested/'],['/src/nested/B.cs','/docs/nested/B.cs']]){
    const before=arranged.nodes.find(item=>item.id===oldIds.get(oldPath));
    const offset=arranged.settings.gridSize*2;
    assert.deepEqual(await node(newPath),{...before,id:await id(newPath),x:before.x+offset,y:before.y+offset},'Clipboard geometry follows the new path with a two-grid-cell offset');
  }
  assert.equal(pasted.nodes.length,7);
  await surface.press('Control+z');await waitSource(arrangedSource);await row('/docs/nested/B.cs').waitFor({state:'detached'});
  await surface.press('Control+y');await waitSource(pastedSource);await row('/docs/nested/B.cs').waitFor();
  // Clipboard offsets can place the clone beyond the preserved camera's clip.
  // Fit the committed geometry before selecting it with a real pointer.
  await fitCanvas(page);
  await activateControl(page, row('/docs/nested/'));await surface.press('Delete');
  await row('/docs/nested/B.cs').waitFor({state:'detached'});
  await waitSource(arrangedSource);
  assert.equal(await source.inputValue(),arrangedSource,'Delete removes the subtree and its geometry');
  await surface.press('Control+z');await waitSource(pastedSource);await row('/docs/nested/').waitFor();

  await activateControl(page, row('/docs/nested/'));await activateControl(page, page.getByRole('button', {name:'Insert file…',exact:true}));
  dialog=page.getByRole('dialog',{name:'Insert file',exact:true});
  await fillControl(page, dialog.getByRole('textbox',{name:'Path',exact:true}), '/docs/nested/New.cs');
  await activateControl(page, dialog.getByRole('button',{name:'Insert',exact:true}));await dialog.waitFor({state:'hidden'});
  await row('/docs/nested/New.cs').waitFor();
  assert.ok((await snapshot()).body.includes('/docs/nested/New.cs\n@endfiles'));
  await surface.press('Control+z');await waitSource(pastedSource);await row('/docs/nested/New.cs').waitFor({state:'detached'});

  await fitCanvas(page);
  if(process.env.SCREENSHOT_DIR){await mkdir(process.env.SCREENSHOT_DIR,{recursive:true});await page.screenshot({path:resolve(process.env.SCREENSHOT_DIR,'files-authoring.png')});}
  console.log('Verified Files inferred-folder direct editing, atomic path and geometry remapping, collision guards, inspector move/insert, keyboard subtree copy/paste/delete, BOM preservation and exact undo/redo.');
}
