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
}
