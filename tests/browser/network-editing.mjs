import { clickControl, fillControl } from './native-controls.mjs';
import assert from 'node:assert/strict';

export async function verifyNetworkEditing(page, source) {
  await page.setViewportSize({width:1480,height:960});
  await clickControl(page, page.getByRole('button', {name:'Split',exact:true}));
  await source.waitFor({state:'visible'});
  const paper = page.locator('.paper').first();
  const fixture = '@startnwdiag\nnwdiag {\n  network dmz {\n    address = "192.0.2.0/24"; // subnet retained\n    shared [address=".1", description="Gateway", color="#112233"];\n    client [address=".2"];\n  }\n  network lan {\n    shared [address="10.0.0.1"];\n    db [shape=database];\n  }\n  network spare {\n  }\n}\n@endnwdiag';
  const semantic = text => text.startsWith("' @unouml:canvas ") ? text.slice(text.indexOf('\n') + 1) : text;
  const geometry = text => JSON.parse(text.slice("' @unouml:canvas ".length,text.indexOf('\n')));
  await fillControl(page, source, fixture);
  await paper.locator('g.entity[data-qualified-name="shared"]').waitFor();
  assert.equal(await paper.getAttribute('data-renderer'),'managed');
  await clickControl(page, page.getByRole('button',{name:'Fit diagram',exact:true}));

  // Author actual geometry before renaming. This catches identity remapping in
  // the production .NET/browser bridge, not only in an isolated source API.
  let shared = paper.locator('g.entity[data-qualified-name="shared"]');
  const oldId = await shared.getAttribute('data-canvas-id');
  const bounds = await shared.boundingBox();
  await page.mouse.move(bounds.x + bounds.width / 2,bounds.y + 8);
  await page.mouse.down();
  await page.mouse.move(bounds.x + bounds.width / 2 + 32,bounds.y + 40,{steps:8});
  await page.mouse.up();
  await page.waitForFunction(() => [...document.querySelectorAll('textarea')].some(e=>e.getAttribute('aria-label')==='PlantUML source' && e.value.startsWith("' @unouml:canvas ")));
  const dragged = await source.inputValue();
  assert.equal(semantic(dragged),fixture);
  const authored = geometry(dragged).nodes.find(n=>n.id===oldId);
  assert.ok(authored);

  await clickControl(page, paper.locator('[data-edit-kind="quoted"]').filter({hasText:/^Gateway$/}));
  await clickControl(page, page.getByRole('button', {name:'Rename host identifier…',exact:true}));
  let dialog = page.getByRole('dialog',{name:'Rename host identifier',exact:true});
  await fillControl(page, dialog.getByRole('textbox',{name:'Host identifier',exact:true}), 'gateway');
  await clickControl(page, dialog.getByRole('button',{name:'Rename',exact:true}));
  await dialog.waitFor({state:'hidden'});
  await paper.locator('g.entity[data-qualified-name="gateway"]').waitFor();
  const renamed = await source.inputValue();
  assert.equal(semantic(renamed),fixture.replaceAll('shared [','gateway ['));
  const newId = await paper.locator('g.entity[data-qualified-name="gateway"]').getAttribute('data-canvas-id');
  assert.notEqual(newId,oldId);
  const renamedNode = geometry(renamed).nodes.find(n=>n.id===newId);
  assert.deepEqual({...renamedNode,id:oldId},authored,'A shared-host rename retains the authored node geometry');
  assert.equal(geometry(renamed).edges.filter(e=>e.target===newId).length,2,'Both interfaces point at the renamed host');
  await clickControl(page, page.getByRole('button',{name:'Undo',exact:true}));
  await paper.locator('g.entity[data-qualified-name="shared"]').waitFor();
  assert.equal(await source.inputValue(),dragged,'Undo restores source and all route identities together');
  await clickControl(page, page.getByRole('button',{name:'Undo',exact:true}));
  await page.waitForFunction(expected => [...document.querySelectorAll('textarea')].some(e=>e.getAttribute('aria-label')==='PlantUML source' && e.value===expected),fixture);
  assert.equal(await source.inputValue(),fixture);

  await clickControl(page, paper.locator('[data-edit-kind="text"]').filter({hasText:/^dmz$/}));
  await clickControl(page, page.getByRole('button', {name:'Insert network host…',exact:true}));
  dialog = page.getByRole('dialog',{name:'Insert network host',exact:true});
  await fillControl(page, dialog.getByRole('textbox',{name:'Host identifier',exact:true}), 'cache');
  await fillControl(page, dialog.getByRole('textbox',{name:'Host description',exact:true}), 'Cache');
  await fillControl(page, dialog.getByRole('textbox',{name:'Interface address',exact:true}), '.5');
  await clickControl(page, dialog.getByRole('button',{name:'Insert',exact:true}));
  await dialog.waitFor({state:'hidden'});
  await paper.locator('g.entity[data-qualified-name="cache"]').waitFor();
  assert.ok((await source.inputValue()).includes('cache [address = ".5", description = "Cache"];'));
  assert.ok((await source.inputValue()).includes('// subnet retained'));
  await clickControl(page, page.getByRole('button',{name:'Undo',exact:true}));
  await paper.locator('g.entity[data-qualified-name="cache"]').waitFor({state:'detached'});
  assert.equal(await source.inputValue(),fixture);

  await clickControl(page, paper.locator('[data-edit-kind="quoted"]').filter({hasText:/^Gateway$/}));
  await clickControl(page, page.getByRole('button', {name:'Attach host to network…',exact:true}));
  dialog = page.getByRole('dialog',{name:'Attach host to network',exact:true});
  await fillControl(page, dialog.getByRole('textbox',{name:'Interface address',exact:true}), '172.16.0.1');
  await clickControl(page, dialog.getByRole('button',{name:'Attach',exact:true}));
  await dialog.waitFor({state:'hidden'});
  await paper.locator('[data-edit-kind="quoted"]').filter({hasText:/^172\.16\.0\.1$/}).waitFor();
  assert.equal(await paper.locator('g.entity[data-qualified-name="shared"]').count(),1,'Attaching reuses the shared host');
  assert.equal(await paper.locator('g.interface').count(),5);
  const attached = await source.inputValue();
  await clickControl(page, paper.locator('[data-edit-kind="quoted"]').filter({hasText:/^172\.16\.0\.1$/}));
  await clickControl(page, page.getByRole('button', {name:'Detach from this network',exact:true}));
  await paper.locator('[data-edit-kind="quoted"]').filter({hasText:/^172\.16\.0\.1$/}).waitFor({state:'detached'});
  assert.equal(await paper.locator('g.interface').count(),4);
  assert.equal(await source.inputValue(),fixture,'Detaching removes exactly the added interface');
  await clickControl(page, page.getByRole('button',{name:'Undo',exact:true}));
  await paper.locator('[data-edit-kind="quoted"]').filter({hasText:/^172\.16\.0\.1$/}).waitFor();
  assert.equal(await source.inputValue(),attached);
  await clickControl(page, page.getByRole('button',{name:'Undo',exact:true}));
  await paper.locator('[data-edit-kind="quoted"]').filter({hasText:/^172\.16\.0\.1$/}).waitFor({state:'detached'});
  assert.equal(await source.inputValue(),fixture);

  // Wrapperless syntax, named colors and inline statements use the same native
  // parser as structural edits, so no formatting-dependent fallback is needed.
  const compact = '@startnwdiag\nnetwork direct { color = lightblue; gateway [address="203.0.113.1", description="Direct gateway", shape=node]; }\n@endnwdiag';
  await fillControl(page, source, compact);
  await paper.locator('[data-edit-kind="quoted"]').filter({hasText:/^Direct gateway$/}).waitFor();
  assert.equal(await paper.getAttribute('data-renderer'),'managed');
  await clickControl(page, paper.locator('[data-edit-kind="quoted"]').filter({hasText:/^203\.0\.113\.1$/}));
  await clickControl(page, page.getByRole('button', {name:'Interface address…',exact:true}));
  dialog = page.getByRole('dialog',{name:'Interface address',exact:true});
  await fillControl(page, dialog.getByRole('textbox',{name:'Interface address',exact:true}), '203.0.113.2');
  await clickControl(page, dialog.getByRole('button',{name:'Apply',exact:true}));
  await dialog.waitFor({state:'hidden'});
  await paper.locator('[data-edit-kind="quoted"]').filter({hasText:/^203\.0\.113\.2$/}).waitFor();
  assert.equal(await source.inputValue(),compact.replace('203.0.113.1','203.0.113.2'));
  await clickControl(page, page.getByRole('button',{name:'Undo',exact:true}));
  await paper.locator('[data-edit-kind="quoted"]').filter({hasText:/^203\.0\.113\.1$/}).waitFor();
  assert.equal(await source.inputValue(),compact);
  const sameLine = '@startnwdiag\nnetwork direct { a [description="First"]; b [description="Second"]; }\n@endnwdiag';
  await fillControl(page, source, sameLine);
  await paper.locator('g.entity[data-qualified-name="b"]').waitFor();
  await clickControl(page, page.getByRole('button',{name:'Fit diagram',exact:true}));
  // Both host declarations and the bus header have the same source line. Hit
  // the actual body, outside its exact label token, to exercise object picking.
  await clickControl(page, paper.locator('g.entity[data-qualified-name="b"] > rect'), {position:{x:8,y:8}});
  await clickControl(page, page.getByRole('button', {name:'Rename host identifier…',exact:true}));
  dialog = page.getByRole('dialog',{name:'Rename host identifier',exact:true});
  assert.equal(await dialog.getByRole('textbox',{name:'Host identifier',exact:true}).inputValue(),'b','Body picking resolves the second host, not the first statement on its line');
  await fillControl(page, dialog.getByRole('textbox',{name:'Host identifier',exact:true}), 'secondHost');
  await clickControl(page, dialog.getByRole('button',{name:'Rename',exact:true}));
  await dialog.waitFor({state:'hidden'});
  await paper.locator('g.entity[data-qualified-name="secondHost"]').waitFor();
  assert.equal(await source.inputValue(),sameLine.replace(' b [',' secondHost ['));
  await clickControl(page, page.getByRole('button',{name:'Undo',exact:true}));
  await paper.locator('g.entity[data-qualified-name="b"]').waitFor();
  assert.equal(await source.inputValue(),sameLine);

  const sourceRow = page.getByRole('button',{name:sameLine.split('\n')[1],exact:true});
  await clickControl(page, sourceRow);
  const hosts = page.getByRole('combobox',{name:'Shared host',exact:true});
  await clickControl(page, hosts);
  const hostChoices = page.getByRole('listbox',{name:'Shared host',exact:true});
  await clickControl(page, hostChoices.getByRole('option',{name:'Second · b',exact:true}));
  await hostChoices.waitFor({state:'detached'});
  await hosts.press('Enter');
  const selectedHost = hostChoices.getByRole('option',{name:'Second · b',exact:true,selected:true});
  await selectedHost.waitFor({state:'attached'});
  assert.equal(await hosts.getAttribute('aria-activedescendant'),await selectedHost.getAttribute('id'));
  await hosts.press('Escape');
  await hostChoices.waitFor({state:'detached'});
  // Restore focus to the ambiguous outline button before the shortcut, so a
  // native picker cannot consume Delete as an input gesture.
  await sourceRow.press('Delete');
  await page.getByText('This source line contains several network objects. Select the exact shape or interface on the canvas.',{exact:true}).last().waitFor();
  assert.equal(await source.inputValue(),sameLine,'An ambiguous outline row never deletes the previous canvas selection');

  // The outline highlights the rail. Hit inside its body, clear of the resize
  // grip at the left edge, to test semantic network picking.
  await clickControl(page, paper.locator('g.network[data-qualified-name="direct"] > rect'), {position:{x:30,y:3}});
  await clickControl(page, page.getByRole('button', {name:'Rename network identifier…',exact:true}));
  dialog = page.getByRole('dialog',{name:'Rename network identifier',exact:true});
  assert.equal(await dialog.getByRole('textbox',{name:'Network identifier',exact:true}).inputValue(),'direct','Rail picking resolves the network that shares a line with its hosts');
  await fillControl(page, dialog.getByRole('textbox',{name:'Network identifier',exact:true}), 'outside');
  await clickControl(page, dialog.getByRole('button',{name:'Rename',exact:true}));
  await dialog.waitFor({state:'hidden'});
  await paper.locator('g.network[data-qualified-name="outside"]').waitFor();
  assert.equal(await source.inputValue(),sameLine.replace('network direct','network outside'));
  await clickControl(page, page.getByRole('button',{name:'Undo',exact:true}));
  await paper.locator('g.network[data-qualified-name="direct"]').waitFor();
  assert.equal(await source.inputValue(),sameLine);
  console.log('Verified native network structural authoring, shared-host attachment, identity-preserving canvas rename, interface addresses, wrapperless syntax, comment retention and exact atomic undo.');
}
