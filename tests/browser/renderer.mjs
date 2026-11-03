import assert from 'node:assert/strict';
import { verifyCanvasEvents } from './direct-editing.mjs';
import { verifyServerRendering } from './server-cases.mjs';
import { verifyResourceLoading } from './resources.mjs';
import { verifyArchiveFiles } from './archive.mjs';
import { createServer } from 'node:http';
import { readFile } from 'node:fs/promises';
import { extname, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright';

const wwwroot = fileURLToPath(new URL('../../src/UnoUML.App/wwwroot/', import.meta.url));
await verifyResourceLoading();
const mime = { '.js': 'text/javascript', '.html': 'text/html', '.css': 'text/css' };
const server = createServer(async (req, res) => {
  try {
    const path = new URL(req.url, 'http://localhost').pathname;
    if (path === '/') {
      res.writeHead(200, { 'Content-Type': 'text/html' });
      res.end('<!doctype html><div id="diagram" style="position:relative;width:800px;height:560px;pointer-events:none"></div>');
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
  const context = await browser.newContext({ hasTouch: true });
  const page = await context.newPage();
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
  await page.locator('.surface').press('Home');
  await page.locator('.surface').press('ArrowRight');
  assert.equal(await page.evaluate(() => log.at(-1).id), 'B');
  await page.locator('.surface').press('Enter');
  assert.equal(await page.evaluate(() => log.at(-1).action), 'edit');
  await entity.tap(); await entity.tap();
  assert.equal(await page.evaluate(() => log.at(-1).action), 'edit');
  await page.evaluate(() => ui.setTool('diagram','place'));
  const a = await entity.boundingBox(), b = await page.locator('.paper [data-qualified-name="B"]').boundingBox();
  await page.mouse.move(a.x + a.width/2, a.y + a.height/2);
  await page.mouse.down();
  await page.mouse.move(b.x + 3, b.y + b.height/2, {steps:10});
  await page.mouse.up();
  assert.deepEqual(await page.evaluate(() => [log.at(-1).action,log.at(-1).direction]),['place','left']);
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
  // Structural drag writes a managed hidden edge. Verify physical geometry under both rank orders.
  for (const [rank, direction] of [['', 'left'], ['left to right direction\n', 'up']]) {
    await render("@startuml\n" + rank + "class A\nclass B\n' @unouml:layout\nA -[hidden]" + direction + "-> B\n@enduml");
    const anchor = await page.locator('.paper [data-qualified-name="A"]').boundingBox();
    const placed = await page.locator('.paper [data-qualified-name="B"]').boundingBox();
    assert.ok(placed.x + placed.width < anchor.x, 'B is left of A in ' + (rank || 'top-to-bottom'));
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
  assert.deepEqual(await page.evaluate(async () => JSON.parse(await ui.listIncludes())), ['common.iuml']);
  assert.deepEqual(await page.evaluate(async () => JSON.parse(await ui.updateInclude('common.iuml', 'class LoadedFromFile', 'class UpdatedFromFile'))), {updated:true,persisted:true});
  assert.deepEqual(await page.evaluate(async () => JSON.parse(await ui.updateInclude('common.iuml', 'class LoadedFromFile', 'class StaleFile'))), {updated:false,persisted:false});
  await render('@startuml\n!include common.iuml\n@enduml');
  assert.equal(await page.locator('.paper [data-qualified-name="UpdatedFromFile"]').count(),1);
  await verifyArchiveFiles(page);
  await assert.rejects(render('@startuml\nbroken ???\n@enduml'),/Syntax Error/i);
  await page.evaluate(async () => {
    const {parseSvg,indexHits}=await import('/uml/svg.js');
    const s=parseSvg('<svg xmlns="http://www.w3.org/2000/svg"><a href="javascript:alert(1)" onload="x()"/><foreignObject/><script>alert(1)</script><image href="https://example.invalid/tracker.png"/></svg>');
    if (s.querySelector('script,foreignObject,[onload],[href]')) throw Error('Unsafe SVG');
    const c4Source='@startuml\n!include <C4/C4_Component>\nComponent (ctrl, "Request handler", "C#", $tags="external")\nRel(ctrl, other, "Calls")\n@enduml';
    const c4Svg=()=>parseSvg('<svg xmlns="http://www.w3.org/2000/svg"><g data-qualified-name="site.api.ctrl" data-source-line="1533"><text>Request</text><text>handler</text></g></svg>');
    const mapped=indexHits(c4Svg(),c4Source);
    const node=mapped.byId.get('ctrl');
    if (!node || mapped.hits.get(node).line!==2) throw Error('Nested C4 source alias lost to qualified name, wrapped text or relationship use');
    const continued=c4Source.replace('Component (ctrl, "Request handler", "C#", $tags="external")', 'Component(\\\n $label="Request handler",\\\n $alias=ctrl, $techn="C#")');
    const named=indexHits(c4Svg(),continued);
    if (!named.byId.get('ctrl') || named.hits.get(named.byId.get('ctrl')).line!==2) throw Error('Continued named C4 alias was not mapped to its invocation');
    const included=parseSvg('<svg xmlns="http://www.w3.org/2000/svg"><g data-qualified-name="Invoice" data-source-line="1" data-source-generated="true" data-source-file="model/invoice.iuml" data-file-line="3"><text>Invoice</text></g></svg>');
    const provenance=[...indexHits(included,'@startuml\n!include model/invoice.iuml\n@enduml',true).hits.values()][0];
    if (!provenance.generated || provenance.sourceFile!=='model/invoice.iuml' || provenance.fileLine!==3) throw Error('Include provenance was lost in canvas selection');
    const duplicate=c4Source.replace('Rel(ctrl, other, "Calls")','Component(ctrl, "Duplicate", "C#")');
    if (indexHits(c4Svg(),duplicate).byId.has('ctrl')) throw Error('Ambiguous C4 alias became editable');
    const dynamic=indexHits(c4Svg(),c4Source.replace('Component (','!if %true()\nComponent (').replace('\n@enduml','\n!endif\n@enduml'));
    if (![...dynamic.hits.values()][0].generated) throw Error('Reference preprocessing output must remain source-editable');
    for (const id of ['other.ctrl','ctrl']) {
      const mixed='@startuml\n!include <C4/C4_Context>\nPerson(ctrl, "Controller")\nrectangle '+id+'\n@enduml';
      const svg=parseSvg('<svg xmlns="http://www.w3.org/2000/svg"><g data-qualified-name="'+id+'" data-source-line="3"><text>Rectangle</text></g></svg>');
      const hit=[...indexHits(svg,mixed).hits.values()][0];
      if (hit.id!==id || hit.line!==3 || hit.generated) throw Error('Ordinary node was rebound to a C4 macro');
    }
    const dotted='@startuml\n!include <C4/C4_Context>\nPerson(ctrl, "Controller")\nSystem(other.ctrl, "Other")\n@enduml';
    for (const id of ['other.ctrl','boundary.other.ctrl']) {
      const svg=parseSvg('<svg xmlns="http://www.w3.org/2000/svg"><g data-qualified-name="'+id+'" data-source-line="999"><text>Other</text></g></svg>');
      const hit=[...indexHits(svg,dotted).hits.values()][0];
      if (hit.id!==id || hit.line!==3 || hit.generated) throw Error('Dotted C4 alias was rebound to another macro');
    }
  });
  if (process.env.UNOUML_CATALOG) {
    const fixtures = JSON.parse(await readFile(process.env.UNOUML_CATALOG, 'utf8'));
    const local = fixtures.filter(f => !f.RequiresReferenceServer);
    for (const fixture of local) {
      try {
        await page.evaluate(async source => {
          await ui.render('diagram', source);
          const svg = document.querySelector('#diagram').shadowRoot.querySelector('.paper > svg');
          if (!svg || !svg.viewBox.baseVal.width || !svg.childElementCount) throw new Error('Empty diagram');
        }, fixture.Source);
      } catch (error) { throw new Error('Catalog fixture failed: ' + fixture.Name, {cause: error}); }
    }
    console.log('Verified ' + local.length + ' local catalog fixtures.');
  }
  await verifyCanvasEvents(page, render);
  await verifyServerRendering(page);
  assert.deepEqual(errors,[]);
  console.log('Renderer checks passed. Covered selection, editing, connect, zoom, SVG/PNG downloads, C4, DOT, local includes, syntax diagnostics, and SVG sanitization.');
} finally {
  await browser?.close();
  await new Promise(resolve => server.close(resolve));
}
