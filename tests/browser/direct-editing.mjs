import { clickControl } from './native-controls.mjs';
import assert from 'node:assert/strict';

export async function verifyCanvasEvents(page, render) {
  const source = '@startuml\nclass "Customer" as A\nclass Orders\nA --> Orders\n@enduml';
  await render(source);
  await page.evaluate(() => { ui.setTool('diagram', 'select'); ui.setSelection('diagram', 'A', 1); });
  const field = page.getByRole('textbox', {name:'Edit diagram label'});
  const begin = () => page.evaluate(() => ui.beginLabelEdit('diagram', 'Customer', 42));
  assert.equal(await begin(), true);
  await field.fill('Discarded');
  await field.press('Escape');
  assert.equal(await field.count(), 0);
  assert.equal(await page.evaluate(() => log.filter(x => x.kind === 'label').length), 0);

  await begin();
  await field.fill('Client');
  await field.press('Enter');
  assert.deepEqual(await page.evaluate(() => log.at(-1)), {kind:'label', revision:42, text:'Client'});
  await begin();
  await field.fill('Stale');
  await render(source.replace('Customer','Buyer'));
  assert.equal(await field.count(), 0);
  assert.equal(await page.evaluate(() => log.filter(x => x.kind === 'label').length), 1);

  await page.evaluate(() => { ui.setSelection('diagram', 'A', 1); ui.beginLabelEdit('diagram', 'first\nsecond', 43, true); });
  await field.fill('edited\nreference');
  await field.press('End');
  await field.press('Enter');
  assert.equal(await field.inputValue(), 'edited\nreference\n');
  await field.press('Control+Enter');
  assert.deepEqual(await page.evaluate(() => log.at(-1)), {kind:'label', revision:43, text:'edited\nreference\n'});
  await page.evaluate(() => ui.beginLabelEdit('diagram', 'x^2', 44, false, 'asciimath'));
  await clickControl(page, page.getByRole('button', {name:'Insert fraction', exact:true}));
  assert.equal(await field.inputValue(), '(x^2)/(b)');
  await clickControl(page, page.getByRole('button', {name:'Apply diagram label (Enter)', exact:true}));
  assert.deepEqual(await page.evaluate(() => log.at(-1)), {kind:'label', revision:44, text:'(x^2)/(b)'});
  assert.equal(await page.getByRole('toolbar', {name:'Diagram editing tools'}).count(), 0);
  await page.evaluate(() => ui.beginLabelEdit('diagram', 'Customer', 45, false, 'creole'));
  await field.press('Control+b');
  assert.equal(await field.inputValue(), '**Customer**');
  await clickControl(page, page.getByRole('button', {name:'Italic (Ctrl+I)', exact:true}));
  assert.equal(await field.inputValue(), '//**Customer**//');
  await field.press('Control+i');
  assert.equal(await field.inputValue(), '**Customer**');
  await field.press('Enter');
  assert.deepEqual(await page.evaluate(() => log.at(-1)), {kind:'label', revision:45, text:'**Customer**'});

  await page.evaluate(() => {
    ui.setSelection('diagram','A',1);
    ui.setConnectable('diagram',true);
  });
  const port = page.getByRole('button', {name:'Connect selected object'});
  const destination = page.locator('.paper [data-qualified-name="Orders"] text').first();
  const from = await port.boundingBox(), to = await destination.boundingBox();
  await page.mouse.move(from.x+from.width/2,from.y+from.height/2);
  await page.mouse.down();
  await page.mouse.move(to.x+to.width/2,to.y+to.height/2,{steps:6});
  await page.mouse.up();
  assert.deepEqual(await page.evaluate(() => {const e=log.at(-1); return [e.action,e.from.id,e.to.id];}), ['connect','A','Orders']);

  // Retarget the end of a reversed relationship. The port follows the lexical
  // endpoint even when the renderer orders its SVG path the other way around.
  await render('@startuml\nclass A\nclass B\nclass C\nB <-- A : read\n@enduml');
  await page.evaluate(() => {ui.setSelection('diagram','',4); ui.setEndpoints('diagram','B','A');});
  const edgePort = page.getByRole('button', {name:'Reattach target endpoint'});
  const targetC = page.locator('.paper [data-qualified-name="C"] text').first();
  const eb = await edgePort.boundingBox(), cb = await targetC.boundingBox();
  assert.ok(eb && cb);
  await page.mouse.move(eb.x+eb.width/2,eb.y+eb.height/2);
  await page.mouse.down();
  await page.mouse.move(cb.x+cb.width/2,cb.y+cb.height/2,{steps:6});
  await page.mouse.up();
  assert.deepEqual(await page.evaluate(() => {const e=log.at(-1); return [e.action,e.from.line,e.to.id];}), ['retargetTo',4,'C']);
  await render(source);
  await page.evaluate(() => ui.setTool('diagram','marquee'));
  const r=await page.locator('.surface').boundingBox();
  await page.mouse.move(r.x+2,r.y+2);
  await page.mouse.down();
  await page.mouse.move(r.x+r.width-3,r.y+r.height-3,{steps:6});
  await page.mouse.up();
  const selection = await page.evaluate(() => log.at(-1));
  assert.equal(selection.kind,'region');
  assert.equal(selection.add,false);
  assert.deepEqual(selection.hits.map(h=>h.id).filter(id=>id==='A'||id==='Orders').sort(), ['A','Orders']);
  await page.evaluate(() => ui.setTool('diagram','select'));
}

export async function verifyStudioEditing(page, source) {
  const previous = await source.inputValue();
  const label = page.locator('.paper text').filter({hasText:/^Storefront$/}).first();
  await label.dblclick();
  const field = page.getByRole('textbox', {name:'Edit diagram label'});
  await field.fill('Draft name');
  await field.press('Escape');
  assert.equal(await source.inputValue(), previous);
  const surface = page.locator('.surface').first();
  await surface.press('F2');
  await field.fill('Checkout UI');
  await field.press('Enter');
  assert.equal(await source.inputValue(), previous.replace('"Storefront" as Store','"Checkout UI" as Store'));
  await page.locator('.paper text').filter({hasText:/^Checkout UI$/}).first().waitFor();
  // The entire visual edit is one ordinary source transaction.
  await surface.press('Control+z');
  await label.waitFor();
  assert.equal(await source.inputValue(), previous);

  const message = page.locator('.paper .message text').filter({hasText:/Authorize payment/}).first();
  await clickControl(page, message);
  const targetPort = page.getByRole('button', {name:'Reattach target endpoint'});
  const orders = page.locator('.paper [data-qualified-name="Orders"] text').filter({hasText:/^Orders$/}).first();
  const start = await targetPort.boundingBox(), end = await orders.boundingBox();
  assert.ok(start && end, 'Selected message exposes an editable target');
  await page.mouse.move(start.x+start.width/2,start.y+start.height/2);
  await page.mouse.down();
  await page.mouse.move(end.x+end.width/2,end.y+end.height/2,{steps:8});
  await page.mouse.up();
  assert.equal(await source.inputValue(), previous.replace('Store -> Pay : Authorize payment','Store -> Orders : Authorize payment'));
  await surface.press('Control+z');
  assert.equal(await source.inputValue(), previous);
  const json = '@startjson\n{"é🪴":"雪","left":"same","right":"same","n":12}\n@endjson';
  await source.fill(json);
  const duplicate = page.locator('.paper text[data-json-kind="string"]').filter({hasText:/^same$/});
  await duplicate.nth(1).dblclick();
  await field.fill('second');
  await field.press('Enter');
  assert.equal(await source.inputValue(), json.replace('"right":"same"', '"right":"second"'));
  const number = page.locator('.paper text[data-json-kind="scalar"]').filter({hasText:/^12$/});
  await clickControl(page, number);
  await surface.press('F2');
  await field.fill('1e-');
  await field.press('Enter');
  assert.ok((await source.inputValue()).includes('"n":12'));
  await number.dblclick();
  await field.fill('false');
  await field.press('Enter');
  assert.ok((await source.inputValue()).includes('"n":false'));
  await surface.press('Control+z');
  await number.waitFor();
  await source.fill(previous);
  await label.waitFor();
}
