import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { readFile } from 'node:fs/promises';
import { extname, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright';

const wwwroot = fileURLToPath(new URL('../../src/UnoUML.App/wwwroot/', import.meta.url));
const mime = { '.js': 'text/javascript', '.html': 'text/html', '.css': 'text/css' };
const server = createServer(async (req, res) => {
  try {
    const path = new URL(req.url, 'http://localhost').pathname;
    if (path === '/') {
      res.writeHead(200, { 'Content-Type': 'text/html' });
      res.end('<!doctype html><div id="diagram" style="position:relative;width:800px;height:560px"></div>');
      return;
    }
    const file = resolve(wwwroot, '.' + path);
    if (!file.startsWith(resolve(wwwroot) + sep)) throw new Error('Invalid path');
    const body = await readFile(file);
    res.writeHead(200, { 'Content-Type': mime[extname(file)] ?? 'application/octet-stream' });
    res.end(body);
  } catch { res.writeHead(404); res.end(); }
});
await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
let browser;
try {
  browser = await chromium.launch({ executablePath: process.env.CHROMIUM_PATH || undefined, headless: true });
  const page = await browser.newPage();
  const errors = [];
  page.on('pageerror', error => errors.push(String(error)));
  await page.goto('http://127.0.0.1:' + server.address().port + '/');
  await page.evaluate(async () => {
    window.ui = await import('/uml/viewport.js');
    window.log = [];
    ui.mount('diagram', json => log.push(JSON.parse(json)));
  });
  const render = source => page.evaluate(text => ui.render('diagram', text), source);
  const signature = '@startuml\nclass "Studio" as A\nclass B\nA --> B : sync\n@enduml';
  await render(signature);
  const entity = page.locator('.paper [data-qualified-name="A"]');
  assert.equal(await entity.count(), 1);
  await entity.click();
  assert.deepEqual(await page.evaluate(() => { const {action,id,line} = log.at(-1); return {action,id,line}; }), { action:'select',id:'A',line:1 });
  await entity.dblclick();
  assert.equal(await page.evaluate(() => log.at(-1).action), 'edit');
  await page.evaluate(() => ui.setTool('diagram','connect'));
  await entity.click();
  await page.locator('.paper [data-qualified-name="B"]').click();
  assert.deepEqual(await page.evaluate(() => { const g=log.at(-1); return [g.action,g.from.id,g.to.id]; }), ['connect','A','B']);
  const before=await page.evaluate(()=>log.filter(x=>x.kind==='zoom').at(-1).zoom);
  await page.evaluate(()=>ui.zoom('diagram',1.5));
  assert.ok(await page.evaluate(()=>log.at(-1).zoom)>before);
  for (const format of ['svg','png']) {
    const download=page.waitForEvent('download');
    await page.evaluate(f=>ui.exportDiagram('diagram',f,'graph.'+f), format);
    const file=await download;
    assert.equal(file.suggestedFilename(),'graph.'+format);
    const bytes=await readFile(await file.path());
    if (format==='png') assert.equal(bytes.subarray(1,4).toString(),'PNG');
    else { assert.match(bytes.toString(),/Studio/); assert.doesNotMatch(bytes.toString(),/data-uml-hit/); }
  }
  await render('@startuml\n!include <C4/C4_Context>\nPerson(viewer,"Reviewer")\n@enduml');
  await page.locator('.paper [data-qualified-name="viewer"]').click();
  assert.equal(await page.evaluate(()=>log.at(-1).line),2);
  await render('@startuml\nclass Box {\n +draw()\n}\n@enduml');
  await page.evaluate(()=>ui.setTool('diagram','select'));
  await page.locator('.paper text').filter({hasText:'draw()'}).click();
  assert.equal(await page.evaluate(()=>log.at(-1).line),2);
  await render('@startdot\ndigraph G { A -> B; B -> C }\n@enddot');
  assert.equal(await page.locator('.paper g.node').count(),3);
  const chooser=page.waitForEvent('filechooser');
  const imported=page.evaluate(()=>ui.importIncludes(false));
  await (await chooser).setFiles({name:'common.iuml',mimeType:'text/plain',buffer:Buffer.from('class LoadedFromFile')});
  assert.equal(await imported,1);
  await render('@startuml\n!include common.iuml\n@enduml');
  assert.equal(await page.locator('.paper [data-qualified-name="LoadedFromFile"]').count(),1);
  await assert.rejects(render('@startuml\nbroken ???\n@enduml'),/Syntax Error/i);
  await page.evaluate(async () => {
    const {parseSvg}=await import('/uml/svg.js');
    const s=parseSvg('<svg xmlns="http://www.w3.org/2000/svg"><a href="javascript:alert(1)" onload="x()"/><foreignObject/><script>alert(1)</script><image href="https://example.invalid/tracker.png"/></svg>');
    if (s.querySelector('script,foreignObject,[onload],[href]')) throw Error('Unsafe SVG');
  });
  assert.deepEqual(errors,[]);
  console.log('Renderer checks passed. Covered selection, editing, connect, zoom, SVG/PNG downloads, C4, DOT, local includes, syntax diagnostics, and SVG sanitization.');
} finally {
  await browser?.close();
  await new Promise(resolve => server.close(resolve));
}
