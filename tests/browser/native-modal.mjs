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

    // Model the tagged Uno event ordering: the public enable handler removes
    // its element and focuses a native control before document key forwarding.
    await frame.evaluate(() => {
      document.body.innerHTML = '<div id="uno-semantics-root"><button id="native-command" aria-label="Native command">Native command</button></div>';
      const state = globalThis.activationFixture = { activations: 0, upstreamKeys: 0, forwarded: [] };
      // Program.Main installs the boundary before host construction registers
      // native document input. Keep that real ordering in this regression.
      state.dispose = modalModule.installAccessibilityKeyboardBoundary();
      state.idempotent = state.dispose === modalModule.installAccessibilityKeyboardBoundary();
      for (const type of ['keydown', 'keyup']) document.addEventListener(type, event => {
        state.forwarded.push([event.type, event.key, event.target.id]);
      });
      globalThis.addEnableControl = () => {
        const control = document.createElement('div');
        control.id = 'uno-enable-accessibility';
        control.setAttribute('role', 'button');
        control.setAttribute('aria-label', 'Enable accessibility');
        control.tabIndex = 0;
        control.addEventListener('click', () => {
          state.activations++;
          control.remove();
          document.getElementById('native-command').focus();
        });
        control.addEventListener('keydown', event => {
          if (event.key === 'Enter' || event.key === ' ') {
            state.upstreamKeys++;
            event.preventDefault();
            control.click();
          }
        });
        document.body.prepend(control);
      };
    });
    equal(await frame.evaluate(() => activationFixture.idempotent), true, 'Repeated initialization installs one boundary');
    for (const key of ['Enter', 'Space']) {
      await frame.evaluate(() => addEnableControl());
      await frame.getByRole('button', { name: 'Enable accessibility', exact: true }).press(key);
    }
    equal(await frame.evaluate(() => [activationFixture.activations, activationFixture.upstreamKeys, activationFixture.forwarded]),
      [2, 0, []], 'Enter and Space use the existing public click handler without reaching document key forwarding');
    equal(await frame.evaluate(() => document.activeElement.id), 'native-command', 'Native activation focus is preserved');
    report.cases.push('public Enter/Space activation and recreated enable control');

    await frame.evaluate(() => addEnableControl());
    await frame.getByRole('button', { name: 'Enable accessibility', exact: true }).focus();
    try {
      await page.keyboard.down('Enter');
      await page.keyboard.down('Enter');
    } finally { await page.keyboard.up('Enter'); }
    equal(await frame.evaluate(() => [activationFixture.activations, activationFixture.forwarded]), [3, []],
      'A held activation key and its release never invoke the newly focused native command');
    await frame.getByRole('button', { name: 'Native command', exact: true }).press('Enter');
    equal(await frame.evaluate(() => activationFixture.forwarded.map(event => event.slice(0, 2))),
      [['keydown', 'Enter'], ['keyup', 'Enter']], 'Ordinary native control keys continue to reach the framework');
    report.cases.push('held key suppression and ordinary native input');

    await frame.evaluate(() => {
      const state = activationFixture;
      state.forwarded = []; state.comboActions = 0; state.menuActions = 0; state.menuNavigation = 0;
      const root = document.getElementById('uno-semantics-root');
      root.insertAdjacentHTML('beforeend', '<div id="combo" role="combobox" tabindex="0" aria-label="Starting diagram" aria-expanded="false"></div><div role="listbox"><div id="choice" role="option" tabindex="-1" aria-label="Choice">Choice</div></div><input id="sink" aria-label="New dialog field">');
      const combo = document.getElementById('combo'), choice = document.getElementById('choice'), sink = document.getElementById('sink');
      combo.addEventListener('keydown', event => {
        const expanded = combo.getAttribute('aria-expanded') === 'true';
        if (event.key === 'Enter' || event.key === ' ' || (event.key === 'ArrowDown' && event.altKey) || (event.key === 'Escape' && expanded)) {
          event.preventDefault(); state.comboActions++;
          combo.setAttribute('aria-expanded', String(!expanded));
          (expanded ? combo : choice).focus();
        }
      });
      // These paths intentionally still require native keyboard dispatch.
      sink.addEventListener('keydown', event => event.preventDefault());
      choice.addEventListener('keydown', event => event.preventDefault());
      globalThis.addMenu = () => {
        document.getElementById('menu')?.remove();
        const menu = document.createElement('div'); menu.id = 'menu'; menu.setAttribute('role', 'menu'); menu.tabIndex = -1;
        for (const name of ['First action', 'Second action']) {
          const item = document.createElement('div'); item.setAttribute('role', 'menuitem'); item.setAttribute('aria-label', name); item.tabIndex = -1;
          item.addEventListener('keydown', event => {
            if (event.key === 'Enter' || event.key === ' ') {
              event.preventDefault(); state.menuActions++; item.remove(); sink.focus();
            } else if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
              event.preventDefault(); state.menuNavigation++;
              [...menu.children].find(candidate => candidate !== item)?.focus();
            } else if (event.key === 'Escape') {
              event.preventDefault(); menu.focus(); // Native dispatch must still close.
            }
          });
          menu.append(item);
        }
        root.append(menu);
      };
    });
    const combo = frame.getByRole('combobox', { name: 'Starting diagram', exact: true });
    for (const key of ['Enter', 'Space', 'Alt+ArrowDown', 'Escape']) await combo.press(key);
    equal(await frame.evaluate(() => [activationFixture.comboActions, activationFixture.forwarded.filter(event => event[1] !== 'Alt')]),
      [4, []], 'Handled combo actions execute once across same-event focus transfer to a popup option');
    await frame.evaluate(() => activationFixture.forwarded = []);
    for (const key of ['Escape', 'ArrowDown']) await combo.press(key);
    equal(await frame.evaluate(() => activationFixture.forwarded.map(event => event.slice(0, 2))),
      [['keydown', 'Escape'], ['keyup', 'Escape'], ['keydown', 'ArrowDown'], ['keyup', 'ArrowDown']],
      'Closed Escape and unmodified ArrowDown retain native combo behavior');
    report.cases.push('handled and unhandled combobox keys with popup focus transfer');

    await frame.evaluate(() => activationFixture.forwarded = []);
    for (const key of ['Enter', 'Space']) {
      await frame.evaluate(() => addMenu());
      await frame.getByRole('menuitem', { name: 'First action', exact: true }).press(key);
    }
    equal(await frame.evaluate(() => [activationFixture.menuActions, activationFixture.forwarded]), [2, []],
      'Invoked menu targets may detach without forwarding activation into the new dialog field');
    await frame.evaluate(() => addMenu());
    await frame.getByRole('menuitem', { name: 'First action', exact: true }).focus();
    try {
      await page.keyboard.down('ArrowDown');
      await page.keyboard.down('ArrowDown');
      await frame.getByRole('textbox', { name: 'New dialog field', exact: true }).focus();
      await page.keyboard.down('ArrowDown');
    } finally { await page.keyboard.up('ArrowDown'); }
    equal(await frame.evaluate(() => [activationFixture.menuNavigation, activationFixture.forwarded]), [2, []],
      'Held menu arrows navigate once per repeat, then stay contained if focus leaves the menu');
    report.cases.push('detached menu invocation and repeated menu navigation');

    await frame.evaluate(() => addMenu());
    await frame.getByRole('menuitem', { name: 'First action', exact: true }).press('Escape');
    await frame.getByRole('textbox', { name: 'New dialog field', exact: true }).press('a');
    await frame.getByRole('option', { name: 'Choice', exact: true }).press('Enter');
    equal(await frame.evaluate(() => activationFixture.forwarded.map(event => event.slice(0, 2))),
      [['keydown', 'Escape'], ['keyup', 'Escape'], ['keydown', 'a'], ['keyup', 'a'], ['keydown', 'Enter'], ['keyup', 'Enter']],
      'Menu Escape, managed text input and option keys keep native dispatch even when prevented at the target');
    report.cases.push('preserved menu closing, text and option keyboard paths');

    await frame.evaluate(() => {
      activationFixture.dispose();
      activationFixture.forwarded = [];
      addEnableControl();
    });
    await frame.getByRole('button', { name: 'Enable accessibility', exact: true }).press('Enter');
    equal(await frame.evaluate(() => [activationFixture.upstreamKeys, activationFixture.forwarded.map(event => event.slice(0, 2))]),
      [1, [['keydown', 'Enter'], ['keyup', 'Enter']]],
      'The unguarded tagged event ordering reproduces the forwarding defect');
    report.cases.push('unguarded event-routing regression control');
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
