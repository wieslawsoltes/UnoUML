import { activateAccessibility, activateControl, clickControl, fillControl, getNativeControlDiagnostics } from './native-controls.mjs';
import { verifyGpuScene } from './gpu-scene.mjs';
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
import { traceNativeInput } from './native-input-trace.mjs';
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
  // Each scenario creates a complete Uno/WebGPU application. Keep resource
  // pressure bounded while investigating the observed concurrent startup
  // stalls; contention is a hypothesis, not an established application defect.
  const concurrency = 2;
  const runScenario = async ([name, verify]) => {
    let check;
    const errors = [];
    const failures = [];
    const started = Date.now();
    const diagnostics = {
      name, concurrency, startedAt: new Date(started).toISOString(),
      phases: [], console: [], requests: [], pageErrors: [], requestFailures: [], httpErrors: [], dropped: {}
    };
    const limits = { phases: 32, console: 256, requests: 512, pageErrors: 64, requestFailures: 128, httpErrors: 128 };
    const record = (field, value) => {
      if (diagnostics[field].length < limits[field]) diagnostics[field].push({ elapsedMs: Date.now() - started, ...value });
      else diagnostics.dropped[field] = (diagnostics.dropped[field] ?? 0) + 1;
    };
    const phase = value => record('phases', { phase: value });
    const describeError = error => ({ message: String(error.message ?? error).slice(0, 4096), stack: error.stack?.slice(0, 16384) });
    const pendingRequests = new Map();
    try {
      phase('creating context');
      check = await browser.newPage({ viewport: { width: 1480, height: 960 }, colorScheme: 'light', acceptDownloads: true });
      check.setDefaultTimeout(35000);
      check.on('pageerror', error => { errors.push(error.message); record('pageErrors', describeError(error)); });
      check.on('console', message => record('console', {
        type: message.type(), text: message.text().slice(0, 4096), location: message.location()
      }));
      check.on('request', request => {
        const value = { url: request.url().slice(0, 4096), method: request.method(), type: request.resourceType() };
        if (diagnostics.requests.length < limits.requests) {
          const entry = { elapsedMs: Date.now() - started, ...value };
          diagnostics.requests.push(entry);
          pendingRequests.set(request, entry);
        } else diagnostics.dropped.requests = (diagnostics.dropped.requests ?? 0) + 1;
      });
      check.on('response', response => {
        const entry = pendingRequests.get(response.request());
        if (entry) entry.status = response.status();
        if (response.status() >= 400) record('httpErrors', { status: response.status(), url: response.url().slice(0, 4096) });
      });
      const finished = (request, failure) => {
        const entry = pendingRequests.get(request);
        if (entry) {
          entry.durationMs = Date.now() - started - entry.elapsedMs;
          entry.finished = true;
          if (failure) entry.failure = failure;
          pendingRequests.delete(request);
        }
      };
      check.on('requestfinished', request => finished(request));
      check.on('requestfailed', request => {
        const failure = request.failure()?.errorText ?? 'Unknown request failure';
        record('requestFailures', { url: request.url().slice(0, 4096), type: request.resourceType(), failure });
        finished(request, failure);
      });
      phase('navigating');
      await check.goto(url, { waitUntil: 'domcontentloaded' });
      phase('waiting for diagram');
      await check.locator('.paper svg').first().waitFor({ state: 'visible', timeout: 120000 });
      phase('waiting for startup and accessibility');
      await activateAccessibility(check);
      phase('opening split editor');
      await activateControl(check, check.getByRole('button', {name:'Split',exact:true}));
      const source = check.getByRole('textbox', {name:'PlantUML source',exact:true});
      await source.waitFor({state:'visible'});
      phase('running authoring scenario');
      await verify(check, source);
      assert.deepEqual(errors, [], `${name} must not produce browser errors`);
      phase('passed');
      console.log(`Passed independent ${name} workspace scenario.`);
    } catch (error) {
      failures.push(error);
      phase('failed');
      if (check && process.env.SCREENSHOT_DIR) {
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
    } finally {
      diagnostics.elapsedMs = Date.now() - started;
      diagnostics.nativeActions = check ? getNativeControlDiagnostics(check) : [];
      if (check && !check.isClosed()) {
        try {
          diagnostics.finalState = await check.evaluate(() => {
            const describe = element => {
              if (!element) return null;
              const box = element.getBoundingClientRect(), style = getComputedStyle(element);
              return { id: element.id, tag: element.localName, className: element.getAttribute('class'),
                display: style.display, visibility: style.visibility, children: element.childElementCount,
                rect: { x: box.x, y: box.y, width: box.width, height: box.height } };
            };
            const root = document.getElementById('uno-semantics-root');
            return { url: location.href, readyState: document.readyState, visibilityState: document.visibilityState,
              loading: describe(document.getElementById('loading')), canvas: describe(document.getElementById('uno-canvas')),
              nativeHost: describe(document.getElementById('uno-native-element-host')), semantics: describe(root),
              semanticElements: root?.querySelectorAll('*').length ?? 0, activeElement: describe(document.activeElement) };
          });
        } catch (error) { diagnostics.stateCaptureError = describeError(error); }
      }
      if (check) {
        try { await check.close(); }
        catch (error) { failures.push(new Error(`Could not close ${name} workspace context`, { cause: error })); }
      }
      pendingRequests.clear();
      diagnostics.failures = failures.map(describeError);
      if (process.env.SCREENSHOT_DIR) {
        try {
          await mkdir(process.env.SCREENSHOT_DIR, { recursive: true });
          await writeFile(resolve(process.env.SCREENSHOT_DIR, name + '-diagnostics.json'), JSON.stringify(diagnostics, null, 2));
        } catch (error) { failures.push(new Error(`Could not save ${name} workspace diagnostics`, { cause: error })); }
      }
    }
    if (failures.length) throw new AggregateError(failures, `${name} workspace scenario failed`);
  };
  const results = new Array(scenarios.length);
  let next = 0;
  await Promise.all(Array.from({ length: concurrency }, async () => {
    while (next < scenarios.length) {
      const index = next++;
      try {
        await runScenario(scenarios[index]);
        results[index] = { status: 'fulfilled' };
      } catch (reason) {
        results[index] = { status: 'rejected', reason };
      }
    }
  }));
  const failures = results.filter(result => result.status === 'rejected').map(result => result.reason);
  if (failures.length) throw new AggregateError(failures, 'Independent authoring workspace checks failed');
}

const external = process.env.WORKSPACE_URL;
if (!external && !process.argv[2]) throw new Error('Pass the published wwwroot directory or set WORKSPACE_URL');
const server = external ? null : await servePublishedSite(resolve(process.argv[2]));
const url = external || `http://127.0.0.1:${server.address().port}${prefix}`;
let browser, page;
const graphicsLog = [];
const failures = [];
const collectFailure = async (message, action) => {
  try { await action(); }
  catch (error) {
    console.error(message, error);
    failures.push(new Error(message, {cause:error}));
  }
};
try {
  browser = await chromium.launch(graphicsLaunchOptions());
  page = await browser.newPage({ viewport: { width: 1390, height: 860 }, colorScheme: 'light', acceptDownloads: true });
  const errors = [];
  page.setDefaultTimeout(35000);
  page.on('pageerror', error => errors.push(error.message));
  page.on('console', message => {
    if (graphicsLog.length < 1000) graphicsLog.push(message.type() + ': ' + message.text());
  });
  const click = name => activateControl(page, page.getByRole('button', {name,exact:true}));
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
  await activateAccessibility(page);
  assert.equal(await page.locator('.paper').first().getAttribute('data-renderer'), 'managed', 'The starter diagram must render in C#');
  assert.equal(page.frames().filter(f => f.url().includes('/uml/engine.html')).length, 0, 'The managed path must leave the JS engine unloaded');
  await verifyGpuPresentation(page);
  await verifyGpuScene(page);
  // Keep explicit native pointer coverage on the fixed toolbar. Dynamic native
  // commands use their public keyboard path because Uno's semantic bounds can
  // stay stale after ancestor layout; canvas gestures remain real pointer input.
  await clickControl(page, page.getByRole('button', {name:'Split',exact:true}));
  const pointerSource = page.getByRole('textbox', {name:'PlantUML source',exact:true});
  await pointerSource.waitFor({state:'visible'});
  await clickControl(page, page.getByRole('button', {name:'Design',exact:true}));
  await pointerSource.waitFor({state:'hidden'});
  await screenshot('unouml-desktop');
  const textbox = await verifyAccessibility(page);
  // Normal pointer clicks exercise the Uno native-host presenter and hit testing in the shadow SVG.
  await activateControl(page, store);
  await page.getByRole('button', { name: 'Apply label', exact: true }).waitFor();
  await verifyStudioEditing(page, textbox);
  const modified = (await textbox.inputValue()).replace('Place order', 'Confirm basket');
  await fillControl(page, textbox, modified);
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
    await activateControl(page, node('Order'));
    await activateControl(page, node('LineItem'), { modifiers: ['Shift'] });
    await page.getByText('MULTIPLE SELECTION', { exact: true }).waitFor();
  };
  // A new page must become selectable after a manual zoom. This is a normal, unforced click
  // and Playwright waits for the camera update before hit testing.
  await activateControl(page, node('Order'));
  await surface.press('0');
  await selectPair();
  await page.keyboard.press('Control+d');
  await page.locator('.paper [data-qualified-name="OrderCopy"]').first().waitFor();
  await page.locator('.paper [data-qualified-name="LineItemCopy"]').first().waitFor();
  await selectPair();
  await click('Group in package…');
  const groupDialog = page.getByRole('dialog', {name:'Group selected objects',exact:true});
  // A semantic command can be visible before native Opened/focus setup and the
  // modal contract are complete. Activate once inside the ready named dialog.
  await groupDialog.and(page.locator('[aria-modal="true"]')).waitFor({state:'visible'});
  await traceNativeInput(page, groupDialog.getByRole('textbox', {name:'Package name',exact:true}),
    'group-dialog-activation', async () => {
      await activateControl(page, groupDialog.getByRole('button', {name:'Group',exact:true}));
      await groupDialog.waitFor({state:'hidden'});
      await page.waitForFunction(() => JSON.parse(localStorage.getItem('UnoUML:unouml.document.v1')).Source.includes('package "Group"'));
    }, {failureOnly:true,captureManagedInput:true});
  await click('Objects');
  await click('Standard library…');
  const search = page.getByRole('textbox', { name: 'Search include paths', exact: true });
  await search.waitFor();
  await fillControl(page, search, 'c4_context');
  await activateControl(page, page.getByRole('option', { name: 'c4/c4_context', exact: true }));
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
  await activateAccessibility(page);
  await verifyPorts(page);
  await verifyCompatibilityEditing(page, textbox);
  await verifyTypedFields(page, textbox);
  await verifySaltEditing(page, textbox);
  await verifyYamlEditing(page, textbox);
  await verifyDotEditing(page, textbox);
  await verifyGpuFallback(browser, url);
  await verifyArchiveImport(page, textbox);
  await verifyArchiveAliasEditing(page, textbox);
  await verifyImportedPageRouting(page, textbox);
  await verifyNativeCompatibility(page);
  await compareRenderers(page);
  assert.deepEqual(errors, []);
  console.log('Passed published workspace startup, native hit testing, source edit, toolbox insertion, zoom, template pages, multi-select, duplicate and group, standard library search and inclusion, source download, mobile layout and draft restoration.');
} catch (error) {
  if (page) {
    try {
      const state = await page.evaluate(() => {
        const root = document.getElementById('uno-semantics-root');
        const describe = element => {
          if (!element) return null;
          const rect = element.getBoundingClientRect();
          return {
            id: element.id, tag: element.localName, role: element.getAttribute('role'),
            name: element.getAttribute('aria-label')?.slice(0, 256),
            automationId: element.getAttribute('xamlautomationid'),
            attached: element.isConnected, focused: document.activeElement === element,
            hidden: !!element.closest('[aria-hidden="true"], [hidden], [inert]'),
            disabled: element.disabled ?? null, readOnly: element.readOnly ?? null,
            ariaDisabled: element.getAttribute('aria-disabled'), ariaModal: element.getAttribute('aria-modal'),
            ...(typeof element.value === 'string' ? {
              value: element.value.slice(0, 512), valueLength: element.value.length,
              selection: [element.selectionStart, element.selectionEnd]
            } : {}),
            rect: {x:rect.x,y:rect.y,width:rect.width,height:rect.height}
          };
        };
        let draft = null, draftError = null;
        try {
          const source = JSON.parse(localStorage.getItem('UnoUML:unouml.document.v1'))?.Source;
          if (typeof source === 'string') draft = {
            source:source.slice(0,8192), length:source.length, truncated:source.length > 8192
          };
        } catch (failure) { draftError = String(failure.message).slice(0,1024); }
        const inputs = [...(root?.querySelectorAll('input, textarea') ?? [])]
          .filter(element => element.closest('[xamlautomationid^="UnoUML.Dialog."]')
            || element.getAttribute('aria-label') === 'PlantUML source' || element === document.activeElement);
        return {
          documentFocused:document.hasFocus(), active:describe(document.activeElement),
          dialogs:[...(root?.querySelectorAll('[role="dialog"]') ?? [])].slice(-8).map(describe),
          inputs:inputs.slice(0,12).map(describe), inputCount:inputs.length, draft, draftError
        };
      });
      const nativeActions = getNativeControlDiagnostics(page).slice(-16).map(entry => ({
        at:entry.at, action:entry.action,
        before:{id:entry.before.id,name:entry.before.name?.slice(0,256),role:entry.before.role,
          key:entry.before.key,attached:entry.before.attached,exposed:entry.before.exposed,
          visible:entry.before.visible,enabled:entry.before.enabled,focused:entry.before.focused,
          documentFocused:entry.before.documentFocused,active:entry.before.active,rect:entry.before.rect},
        after:entry.after ? {attached:entry.after.attached,rect:entry.after.rect,
          dialogs:entry.after.dialogs?.slice(0,8)} : null,
        diagnosticsError:entry.diagnosticsError?.slice(0,1024)
      }));
      const diagnostics = {error:{message:String(error.message).slice(0,2048),stack:error.stack?.slice(0,4096)},state,nativeActions};
      console.error('Primary native failure state: ' + JSON.stringify(diagnostics));
      if (process.env.SCREENSHOT_DIR) {
        await mkdir(process.env.SCREENSHOT_DIR, {recursive:true});
        await writeFile(resolve(process.env.SCREENSHOT_DIR, 'unouml-failure-diagnostics.json'), JSON.stringify(diagnostics,null,2));
      }
    } catch (captureError) { console.error('Could not capture primary native diagnostics:', captureError); }
  }
  if (page && process.env.SCREENSHOT_DIR) {
    try {
      await mkdir(process.env.SCREENSHOT_DIR, { recursive: true });
      await page.screenshot({ path: resolve(process.env.SCREENSHOT_DIR, 'unouml-failure.png') });
      await writeFile(resolve(process.env.SCREENSHOT_DIR, 'unouml-failure.html'), await page.content());
      const source = page.getByRole('textbox', {name:'PlantUML source',exact:true});
      if (await source.count()) await writeFile(resolve(process.env.SCREENSHOT_DIR, 'unouml-failure.puml'), await source.inputValue());
    } catch (captureError) { console.error('Could not capture workspace failure:', captureError); }
  }
  console.error('Failed primary desktop/mobile workspace checks:', error);
  failures.push(new Error('Primary desktop/mobile workspace checks failed', {cause:error}));
} finally {
  // Capture the primary failure before releasing its GPU context. Run every
  // isolated authoring scenario once after the primary flow has finished.
  if (page) await collectFailure('Could not close primary workspace page', () => page.close());
  if (browser) {
    await collectFailure('Independent authoring workspace checks failed', () => verifyIndependentAuthoring(browser, url));
  }
  await collectFailure('Could not save browser console diagnostics', async () => {
    if (process.env.SCREENSHOT_DIR) {
      await mkdir(process.env.SCREENSHOT_DIR, {recursive:true});
      await writeFile(resolve(process.env.SCREENSHOT_DIR, 'browser-console.txt'), graphicsLog.join('\n'));
    }
  });
  if (browser) await collectFailure('Could not close workspace browser', () => browser.close());
  if (server) await collectFailure('Could not close workspace server', () => new Promise((resolve, reject) => {
    server.close(error => error ? reject(error) : resolve());
  }));
}
if (failures.length) throw new AggregateError(failures, 'Published workspace checks failed');
