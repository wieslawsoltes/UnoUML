import { publishedPrefix, servePublishedSite } from './published-site.mjs';
import { graphicsLaunchOptions } from './graphics-options.mjs';
import { verifyGpuPresentation, verifyGpuFallback } from './gpu-presentation.mjs';
import assert from 'node:assert/strict';
import { verifyAccessibility } from './accessibility.mjs';
import { verifyPorts } from './ports.mjs';
import { compareRenderers } from './managed.mjs';
import { verifyStudioEditing } from './direct-editing.mjs';
import { verifyCompatibilityEditing } from './compat-editing.mjs';
import { verifyArchiveImport, verifyArchiveAliasEditing } from './archive.mjs';
import { verifyNativeCompatibility } from './native-compatibility.mjs';
import { verifyTypedFields } from './typed-fields.mjs';
import { verifySaltEditing } from './salt-editing.mjs';
import { verifyYamlEditing } from './yaml-editing.mjs';
import { verifyDotEditing } from './dot-editing.mjs';
import { verifyDotStructuralAuthoring } from './dot-structure.mjs';
import { verifyStructuredEditing } from './structured-editing.mjs';
import { verifyCanvasEditing } from './canvas-editing.mjs';
import { verifyFilesEditing } from './files-editing.mjs';
import { verifyNetworkEditing } from './network-editing.mjs';
import { verifyGanttEditing } from './gantt-editing.mjs';
import { verifyImportedPageRouting } from './imported-editing.mjs';
import { mkdir, writeFile } from 'node:fs/promises';
import { resolve } from 'node:path';
import { chromium } from 'playwright';

const prefix = publishedPrefix;

async function verifyIndependentAuthoring(browser, url) {
  const scenarios = [
    ['dot-structure', verifyDotStructuralAuthoring],
    ['structured-editing', verifyStructuredEditing],
    ['canvas-editing', verifyCanvasEditing],
    ['files-editing', verifyFilesEditing],
    ['network-editing', verifyNetworkEditing],
    ['gantt-editing', verifyGanttEditing]
  ];
  const results = await Promise.allSettled(scenarios.map(async ([name, verify]) => {
    const check = await browser.newPage({ viewport: { width: 1480, height: 960 }, colorScheme: 'light', acceptDownloads: true });
    const errors = [];
    check.setDefaultTimeout(35000);
    check.on('pageerror', error => errors.push(error.message));
    try {
      await check.goto(url, { waitUntil: 'domcontentloaded' });
      await check.locator('.paper svg').first().waitFor({ state: 'visible', timeout: 120000 });
      await check.getByRole('button', {name:'Split',exact:true}).click();
      const source = check.getByRole('textbox', {name:'PlantUML source',exact:true});
      await source.waitFor({state:'visible'});
      await verify(check, source);
      assert.deepEqual(errors, [], `${name} must not produce browser errors`);
      console.log(`Passed independent ${name} workspace scenario.`);
    } catch (error) {
      if (process.env.SCREENSHOT_DIR) {
        try {
          await mkdir(process.env.SCREENSHOT_DIR, {recursive:true});
          const stem = resolve(process.env.SCREENSHOT_DIR, name + '-failure');
          await check.screenshot({path:stem + '.png'});
          await writeFile(stem + '.html', await check.content());
          const source = check.getByRole('textbox', {name:'PlantUML source',exact:true});
          if (await source.count()) await writeFile(stem + '.puml', await source.inputValue({timeout:1000}));
        } catch (captureError) { console.error(`Could not capture ${name} failure:`, captureError); }
      }
      console.error(`Failed independent ${name} workspace scenario:`, error);
      throw new Error(`${name} workspace scenario failed`, {cause:error});
    } finally {
      await check.close();
    }
  }));
  const failures = results.filter(result => result.status === 'rejected').map(result => result.reason);
  if (failures.length) throw new AggregateError(failures, 'Independent authoring workspace checks failed');
}

const external = process.env.WORKSPACE_URL;
if (!external && !process.argv[2]) throw new Error('Pass the published wwwroot directory or set WORKSPACE_URL');
const server = external ? null : await servePublishedSite(resolve(process.argv[2]));
const browser = await chromium.launch(graphicsLaunchOptions());
let page;
const graphicsLog = [];
try {
  page = await browser.newPage({ viewport: { width: 1390, height: 860 }, colorScheme: 'light', acceptDownloads: true });
  const errors = [];
  page.setDefaultTimeout(35000);
  page.on('pageerror', error => errors.push(error.message));
  page.on('console', message => {
    if (graphicsLog.length < 1000) graphicsLog.push(message.type() + ': ' + message.text());
  });
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
  await verifyGpuPresentation(page);
  await screenshot('unouml-desktop');
  const textbox = await verifyAccessibility(page);
  // Normal pointer clicks exercise the Uno native-host presenter and hit testing in the shadow SVG.
  await store.click();
  await page.getByText('Apply label', { exact: true }).waitFor();
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
  await page.getByRole('combobox', { name: 'Diagram page', exact: true }).waitFor({ state: 'visible' });
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
  await verifyPorts(page);
  await verifyCompatibilityEditing(page, textbox);
  await verifyTypedFields(page, textbox);
  await verifySaltEditing(page, textbox);
  await verifyYamlEditing(page, textbox);
  await verifyDotEditing(page, textbox);
  await verifyGpuFallback(browser, url);
  await verifyIndependentAuthoring(browser, url);
  await verifyArchiveImport(page, textbox);
  await verifyArchiveAliasEditing(page, textbox);
  await verifyImportedPageRouting(page, textbox);
  await verifyNativeCompatibility(page);
  await compareRenderers(page);
  assert.deepEqual(errors, []);
  console.log('Passed published workspace startup, native hit testing, source edit, toolbox insertion, zoom, template pages, multi-select, duplicate and group, standard library search and inclusion, source download, mobile layout and draft restoration.');
} catch (error) {
  if (page && process.env.SCREENSHOT_DIR) {
    try {
      await mkdir(process.env.SCREENSHOT_DIR, { recursive: true });
      await page.screenshot({ path: resolve(process.env.SCREENSHOT_DIR, 'unouml-failure.png') });
      await writeFile(resolve(process.env.SCREENSHOT_DIR, 'unouml-failure.html'), await page.content());
      const source = page.getByRole('textbox', {name:'PlantUML source',exact:true});
      if (await source.count()) await writeFile(resolve(process.env.SCREENSHOT_DIR, 'unouml-failure.puml'), await source.inputValue());
    } catch (captureError) { console.error('Could not capture workspace failure:', captureError); }
  }
  throw error;
} finally {
  if (process.env.SCREENSHOT_DIR) {
    await mkdir(process.env.SCREENSHOT_DIR, {recursive:true});
    await writeFile(resolve(process.env.SCREENSHOT_DIR, 'browser-console.txt'), graphicsLog.join('\n'));
  }
  await browser.close();
  if (server) await new Promise(resolve => server.close(resolve));
}
