import assert from 'node:assert/strict';
import { compareRenderers } from './managed.mjs';
import { verifyStudioEditing } from './direct-editing.mjs';
import { createReadStream } from 'node:fs';
import { mkdir, stat } from 'node:fs/promises';
import { createServer } from 'node:http';
import { extname, resolve, sep } from 'node:path';
import { chromium } from 'playwright';

const prefix = '/UnoUML/';
const mime = { '.css': 'text/css', '.html': 'text/html', '.js': 'text/javascript', '.json': 'application/json', '.mjs': 'text/javascript',
  '.svg': 'image/svg+xml', '.ttf': 'font/ttf', '.wasm': 'application/wasm', '.txt': 'text/plain' };

async function servePublishedSite(wwwroot) {
  await stat(resolve(wwwroot, 'index.html'));
  const server = createServer(async (request, response) => {
    try {
      const path = decodeURIComponent(new URL(request.url, 'http://localhost').pathname);
      if (!path.startsWith(prefix)) throw new Error('Outside the GitHub Pages project path');
      const file = resolve(wwwroot, path.slice(prefix.length) || 'index.html');
      if (!file.startsWith(wwwroot + sep) || !(await stat(file)).isFile()) throw new Error('Missing asset');
      response.writeHead(200, { 'Content-Type': mime[extname(file)] || 'application/octet-stream' });
      createReadStream(file).pipe(response);
    } catch { response.writeHead(404); response.end(); }
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  return server;
}

const external = process.env.WORKSPACE_URL;
if (!external && !process.argv[2]) throw new Error('Pass the published wwwroot directory or set WORKSPACE_URL');
const server = external ? null : await servePublishedSite(resolve(process.argv[2]));
const browser = await chromium.launch({ executablePath: process.env.CHROMIUM_PATH || undefined, headless: true });
try {
  const page = await browser.newPage({ viewport: { width: 1390, height: 860 }, colorScheme: 'light', acceptDownloads: true });
  const errors = [];
  page.setDefaultTimeout(35000);
  page.on('pageerror', error => errors.push(error.message));
  const url = external || `http://127.0.0.1:${server.address().port}${prefix}`;
  const click = name => page.getByText(name, { exact: true }).last().click();
  const paper = page.locator('.paper svg').first();
  const store = page.locator('.paper text').filter({ hasText: /^Storefront$/ }).first();
  const screenshot = async name => {
    if (process.env.SCREENSHOT_DIR) {
      await mkdir(process.env.SCREENSHOT_DIR, { recursive: true });
      await page.screenshot({ path: resolve(process.env.SCREENSHOT_DIR, name + '.png') });
    }
  };

  await page.goto(url, { waitUntil: 'domcontentloaded' });
  await paper.waitFor({ state: 'visible', timeout: 120000 });
  assert.equal(await page.locator('.paper').first().getAttribute('data-renderer'), 'managed', 'The starter diagram must render in C#');
  assert.equal(page.frames().filter(f => f.url().includes('/uml/engine.html')).length, 0, 'The managed path must leave the JS engine unloaded');
  await screenshot('unouml-desktop');
  await click('Split');
  await page.locator('textarea:visible').first().waitFor();
  // Normal pointer clicks exercise the Uno native-host presenter and hit testing in the shadow SVG.
  await store.click();
  await page.getByText('Apply label', { exact: true }).waitFor();
  const editor = await page.locator('textarea').evaluateAll(nodes => nodes.findIndex(node => node.value.startsWith('@startuml')));
  assert.ok(editor >= 0, 'Source editor is available in Split view');
  const textbox = page.locator('textarea').nth(editor);
  await verifyStudioEditing(page, textbox);
  const modified = (await textbox.inputValue()).replace('Place order', 'Confirm basket');
  await textbox.fill(modified);
  await page.locator('.paper text').filter({ hasText: /Confirm basket/ }).first().waitFor({ timeout: 25000 });

  await click('Actor');
  await page.waitForFunction(() => [...document.querySelectorAll('textarea')].some(n => n.value.includes('actor "Actor" as Actor')));
  await click('Design');
  const surface = page.locator('.surface').first();
  const transform = () => page.locator('.paper').first().evaluate(el => el.style.transform);
  const before = await transform();
  await surface.hover();
  await page.keyboard.down('Control');
  await page.mouse.wheel(0, -125);
  await page.keyboard.up('Control');
  await page.waitForFunction(old => [...document.querySelectorAll('*')].some(el => {
    const paper = el.shadowRoot?.querySelector('.paper');
    return paper && paper.style.transform !== old;
  }), before);

  await click('Diagrams');
  await click('Domain model');
  await page.locator('.paper [data-qualified-name="Order"]').first().waitFor({ timeout: 25000 });
  const node = name => page.locator(`.paper [data-qualified-name="${name}"] text`).filter({ hasText: new RegExp('^' + name + '$') }).first();
  const selectPair = async () => {
    await node('Order').click();
    await node('LineItem').click({ modifiers: ['Shift'] });
    await page.getByText('MULTIPLE SELECTION', { exact: true }).waitFor();
  };
  // A new page must become selectable after a manual zoom. This is a normal, unforced click
  // and Playwright waits for the camera update before hit testing.
  await node('Order').click();
  await surface.press('0');
  await selectPair();
  await page.keyboard.press('Control+d');
  await page.locator('.paper [data-qualified-name="OrderCopy"]').first().waitFor();
  await page.locator('.paper [data-qualified-name="LineItemCopy"]').first().waitFor();
  await selectPair();
  await click('Group in package…');
  await click('Group');
  await page.waitForFunction(() => JSON.parse(localStorage.getItem('UnoUML:unouml.document.v1')).Source.includes('package "Group"'));
  await click('Objects');
  await click('Standard library…');
  await page.getByText('Search include paths', { exact: true }).last().waitFor();
  const search = page.locator('textarea:visible').last();
  await search.fill('c4_context');
  await click('c4/c4_context');
  await click('Insert include');
  await page.waitForFunction(() => JSON.parse(localStorage.getItem('UnoUML:unouml.document.v1')).Source.includes('!include <c4/c4_context>'));
  const download = page.waitForEvent('download');
  await click('Save');
  assert.ok((await download).suggestedFilename().endsWith('.puml'));
  await page.setViewportSize({ width: 390, height: 844 });
  await page.waitForFunction(() => document.documentElement.scrollWidth <= innerWidth);
  await screenshot('unouml-mobile');
  await page.reload({ waitUntil: 'domcontentloaded' });
  await page.locator('.paper [data-qualified-name="OrderCopy"]').first().waitFor({ timeout: 120000 });
  await compareRenderers(page);
  assert.deepEqual(errors, []);
  console.log('Passed published workspace startup, native hit testing, source edit, toolbox insertion, zoom, template pages, multi-select, duplicate and group, standard library search and inclusion, source download, mobile layout and draft restoration.');
} finally {
  await browser.close();
  if (server) await new Promise(resolve => server.close(resolve));
}
