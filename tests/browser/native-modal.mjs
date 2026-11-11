import assert from 'node:assert/strict';
import { mkdir, writeFile } from 'node:fs/promises';
import { resolve } from 'node:path';

// Reproduce Uno 7.0.32's captured modal semantic structure in a separate realm.
// Public role queries verify the browser accessibility tree, not only attributes.
export async function verifyNativeModal(page, { artifactDir } = {}) {
  const moduleUrl = await page.evaluate(() => new URL('uml/native-accessibility.js', document.baseURI).href);
  const id = 'unouml-native-modal-fixture';
  await page.evaluate(async id => {
    const iframe = document.createElement('iframe');
    iframe.id = id;
    iframe.title = 'Native modal accessibility regression';
    iframe.style.cssText = 'position:fixed;left:0;top:0;width:600px;height:400px;background:white;z-index:2147483647';
    const loaded = new Promise(resolve => iframe.onload = resolve);
    iframe.srcdoc = `<!doctype html><body>
      <div id="uno-semantics-root">
        <div id="uno-semantics-application" aria-hidden="true" tabindex="-1">
          <button aria-label="Background command" aria-hidden="true" tabindex="-1">Background</button>
          <div id="uno-semantics-inactive-popup" aria-hidden="true" tabindex="-1">
            <div role="dialog" aria-label="Inactive dialog" xamlautomationid="inactive"><button>Inactive</button></div>
          </div>
          <div id="uno-semantics-popup" aria-hidden="true" tabindex="-1">
            <div id="uno-semantics-dialog" role="dialog" aria-modal="true" aria-label="Current dialog" xamlautomationid="current">
              <input id="modal-field" aria-label="Dialog field"><button id="modal-close">Close</button>
            </div>
          </div>
        </div>
      </div>
      <div id="uno-semantics-outside" aria-hidden="true"><div role="dialog" aria-modal="true" xamlautomationid="outside">Outside</div></div>
    </body>`;
    document.body.append(iframe);
    await loaded;
  }, id);
  const frame = await (await page.locator('#' + id).elementHandle()).contentFrame();
  const report = { assertions: 0, cases: [] };
  const equal = (actual, expected, message) => { report.assertions++; assert.deepEqual(actual, expected, message); };
  try {
    await frame.evaluate(async url => { globalThis.modalModule = await import(url); }, moduleUrl);
    const repair = automationId => frame.evaluate(id => modalModule.restoreDialogAncestry(id), automationId);
    const current = frame.getByRole('dialog', { name: 'Current dialog', exact: true });
    equal(await current.count(), 0, 'The captured ancestor bug hides the active modal from public role queries');
    equal(await frame.getByRole('dialog', { name: 'Current dialog', exact: true, includeHidden: true }).count(), 1,
      'The hidden modal exists; this is an accessibility ancestry failure');
    equal(await repair('missing'), 0, 'A stale dialog id cannot alter the semantic tree');
    equal(await repair('outside'), 0, 'AutomationId resolution stays inside the Uno semantic root');
    equal(await repair('inactive'), 0, 'A closed/nonmodal dialog cannot expose its ancestry');
    equal(await repair('current'), 2, 'Only the active modal Popup/application ancestors need repair');
    await current.waitFor({ state: 'visible' });
    equal(await current.getAttribute('aria-modal'), 'true', 'The native modal role remains intact');
    equal(await frame.getByRole('button', { name: 'Background command', exact: true }).count(), 0,
      'Background commands remain excluded from the accessibility tree');
    equal(await frame.getByRole('button', { name: 'Background command', exact: true, includeHidden: true }).count(), 1,
      'Repair preserves the background nodes for native restoration');
    equal(await frame.getByRole('dialog', { name: 'Inactive dialog', exact: true }).count(), 0,
      'Unrelated popup branches remain hidden');
    equal(await frame.locator('#uno-semantics-outside').getAttribute('aria-hidden'), 'true', 'Outside nodes are untouched');
    equal(await frame.locator('#uno-semantics-application').getAttribute('tabindex'), '-1', 'Native focusability is untouched');
    await current.getByRole('textbox', { name: 'Dialog field', exact: true }).focus();
    equal(await repair('current'), 0, 'Repeating the repair is idempotent');
    equal(await frame.evaluate(() => document.activeElement.id), 'modal-field', 'Repair never moves native/browser focus');
    report.cases.push('active modal exposure, public roles and background isolation');

    await frame.evaluate(() => {
      document.activeElement?.blur();
      for (const id of ['uno-semantics-application', 'uno-semantics-popup', 'uno-semantics-dialog', 'modal-field', 'modal-close'])
        document.getElementById(id).setAttribute('aria-hidden', 'true');
      const parent = document.getElementById('uno-semantics-dialog');
      parent.insertAdjacentHTML('beforeend', '<div id="uno-semantics-nested-popup" aria-hidden="true"><div role="dialog" aria-modal="true" aria-label="Nested dialog" xamlautomationid="nested"><button>Nested command</button></div></div>');
    });
    equal(await repair('nested'), 4, 'Nested modal ancestry is exposed without unhiding sibling controls');
    await frame.getByRole('dialog', { name: 'Nested dialog', exact: true }).waitFor({ state: 'visible' });
    equal(await frame.getByRole('button', { name: 'Nested command', exact: true }).count(), 1, 'Nested modal controls are accessible');
    equal(await frame.getByRole('textbox', { name: 'Dialog field', exact: true }).count(), 0, 'Parent modal controls remain isolated');
    equal(await frame.getByRole('button', { name: 'Background command', exact: true }).count(), 0, 'Application background remains isolated');
    report.cases.push('nested modal isolation');

    await frame.evaluate(() => {
      document.getElementById('uno-semantics-dialog').removeAttribute('aria-modal');
      document.getElementById('uno-semantics-application').setAttribute('aria-hidden', 'true');
    });
    equal(await repair('current'), 0, 'A callback arriving after native dialog close is a no-op');
    equal(await frame.locator('#uno-semantics-application').getAttribute('aria-hidden'), 'true', 'Closed callbacks cannot undo a later modal scope');
    report.cases.push('closed and stale callbacks');
    report.pass = true;
  } catch (error) {
    report.pass = false;
    report.error = error.message;
    throw error;
  } finally {
    if (artifactDir) {
      await mkdir(artifactDir, { recursive: true });
      await writeFile(resolve(artifactDir, 'native-modal.json'), JSON.stringify(report, null, 2));
    }
    await page.locator('#' + id).evaluate(element => element.remove());
  }
  console.log('Verified native modal semantics:', JSON.stringify(report));
  return report;
}
