import assert from 'node:assert/strict';

export async function verifyNetworkEditing(page, source) {
  await page.setViewportSize({width:1480,height:960});
  await page.getByText('Split',{exact:true}).last().click();
  await source.waitFor({state:'visible'});
  const paper = page.locator('.paper').first();
  const fixture = '@startnwdiag\nnwdiag {\n  network dmz {\n    address = "192.0.2.0/24"; // subnet retained\n    shared [address=".1", description="Gateway", color="#112233"];\n    client [address=".2"];\n  }\n  network lan {\n    shared [address="10.0.0.1"];\n    db [shape=database];\n  }\n  network spare {\n  }\n}\n@endnwdiag';
  const semantic = text => text.startsWith("' @unouml:canvas ") ? text.slice(text.indexOf('\n') + 1) : text;
  const geometry = text => JSON.parse(text.slice("' @unouml:canvas ".length,text.indexOf('\n')));
  await source.fill(fixture);
  await paper.locator('g.entity[data-qualified-name="shared"]').waitFor();
  assert.equal(await paper.getAttribute('data-renderer'),'managed');
  await page.getByRole('button',{name:'Fit diagram',exact:true}).click();

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

  await paper.locator('[data-edit-kind="quoted"]').filter({hasText:/^Gateway$/}).click();
  await page.getByText('Rename host identifier…',{exact:true}).last().click();
  let dialog = page.getByRole('dialog',{name:'Rename host identifier',exact:true});
  await dialog.getByRole('textbox',{name:'Host identifier',exact:true}).fill('gateway');
  await dialog.getByRole('button',{name:'Rename',exact:true}).click();
  await dialog.waitFor({state:'hidden'});
  await paper.locator('g.entity[data-qualified-name="gateway"]').waitFor();
  const renamed = await source.inputValue();
  assert.equal(semantic(renamed),fixture.replaceAll('shared [','gateway ['));
  const newId = await paper.locator('g.entity[data-qualified-name="gateway"]').getAttribute('data-canvas-id');
  assert.notEqual(newId,oldId);
  const renamedNode = geometry(renamed).nodes.find(n=>n.id===newId);
  assert.deepEqual({...renamedNode,id:oldId},authored,'A shared-host rename retains the authored node geometry');
  assert.equal(geometry(renamed).edges.filter(e=>e.target===newId).length,2,'Both interfaces point at the renamed host');
  await page.getByRole('button',{name:'Undo',exact:true}).click();
  await paper.locator('g.entity[data-qualified-name="shared"]').waitFor();
  assert.equal(await source.inputValue(),dragged,'Undo restores source and all route identities together');
  await page.getByRole('button',{name:'Undo',exact:true}).click();
  await page.waitForFunction(expected => [...document.querySelectorAll('textarea')].some(e=>e.getAttribute('aria-label')==='PlantUML source' && e.value===expected),fixture);
  assert.equal(await source.inputValue(),fixture);

  await paper.locator('[data-edit-kind="text"]').filter({hasText:/^dmz$/}).click();
  await page.getByText('Insert network host…',{exact:true}).last().click();
  dialog = page.getByRole('dialog',{name:'Insert network host',exact:true});
  await dialog.getByRole('textbox',{name:'Host identifier',exact:true}).fill('cache');
  await dialog.getByRole('textbox',{name:'Host description',exact:true}).fill('Cache');
  await dialog.getByRole('textbox',{name:'Interface address',exact:true}).fill('.5');
  await dialog.getByRole('button',{name:'Insert',exact:true}).click();
  await dialog.waitFor({state:'hidden'});
  await paper.locator('g.entity[data-qualified-name="cache"]').waitFor();
  assert.ok((await source.inputValue()).includes('cache [address = ".5", description = "Cache"];'));
  assert.ok((await source.inputValue()).includes('// subnet retained'));
  await page.getByRole('button',{name:'Undo',exact:true}).click();
  await paper.locator('g.entity[data-qualified-name="cache"]').waitFor({state:'detached'});
  assert.equal(await source.inputValue(),fixture);

  await paper.locator('[data-edit-kind="quoted"]').filter({hasText:/^Gateway$/}).click();
  await page.getByText('Attach host to network…',{exact:true}).last().click();
  dialog = page.getByRole('dialog',{name:'Attach host to network',exact:true});
  await dialog.getByRole('textbox',{name:'Interface address',exact:true}).fill('172.16.0.1');
  await dialog.getByRole('button',{name:'Attach',exact:true}).click();
  await dialog.waitFor({state:'hidden'});
  await paper.locator('[data-edit-kind="quoted"]').filter({hasText:/^172\.16\.0\.1$/}).waitFor();
  assert.equal(await paper.locator('g.entity[data-qualified-name="shared"]').count(),1,'Attaching reuses the shared host');
  assert.equal(await paper.locator('g.interface').count(),5);
  const attached = await source.inputValue();
  await paper.locator('[data-edit-kind="quoted"]').filter({hasText:/^172\.16\.0\.1$/}).click();
  await page.getByText('Detach from this network',{exact:true}).last().click();
  await paper.locator('[data-edit-kind="quoted"]').filter({hasText:/^172\.16\.0\.1$/}).waitFor({state:'detached'});
  assert.equal(await paper.locator('g.interface').count(),4);
  assert.equal(await source.inputValue(),fixture,'Detaching removes exactly the added interface');
  await page.getByRole('button',{name:'Undo',exact:true}).click();
  await paper.locator('[data-edit-kind="quoted"]').filter({hasText:/^172\.16\.0\.1$/}).waitFor();
  assert.equal(await source.inputValue(),attached);
  await page.getByRole('button',{name:'Undo',exact:true}).click();
  await paper.locator('[data-edit-kind="quoted"]').filter({hasText:/^172\.16\.0\.1$/}).waitFor({state:'detached'});
  assert.equal(await source.inputValue(),fixture);

  // Wrapperless syntax, named colors and inline statements use the same native
  // parser as structural edits, so no formatting-dependent fallback is needed.
  const compact = '@startnwdiag\nnetwork direct { color = lightblue; gateway [address="203.0.113.1", description="Direct gateway", shape=node]; }\n@endnwdiag';
  await source.fill(compact);
  await paper.locator('[data-edit-kind="quoted"]').filter({hasText:/^Direct gateway$/}).waitFor();
  assert.equal(await paper.getAttribute('data-renderer'),'managed');
  await paper.locator('[data-edit-kind="quoted"]').filter({hasText:/^203\.0\.113\.1$/}).click();
  await page.getByText('Interface address…',{exact:true}).last().click();
  dialog = page.getByRole('dialog',{name:'Interface address',exact:true});
  await dialog.getByRole('textbox',{name:'Interface address',exact:true}).fill('203.0.113.2');
  await dialog.getByRole('button',{name:'Apply',exact:true}).click();
  await dialog.waitFor({state:'hidden'});
  await paper.locator('[data-edit-kind="quoted"]').filter({hasText:/^203\.0\.113\.2$/}).waitFor();
  assert.equal(await source.inputValue(),compact.replace('203.0.113.1','203.0.113.2'));
  await page.getByRole('button',{name:'Undo',exact:true}).click();
  await paper.locator('[data-edit-kind="quoted"]').filter({hasText:/^203\.0\.113\.1$/}).waitFor();
  assert.equal(await source.inputValue(),compact);
  console.log('Verified native network structural authoring, shared-host attachment, identity-preserving canvas rename, interface addresses, wrapperless syntax, comment retention and exact atomic undo.');
}
