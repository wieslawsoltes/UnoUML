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
        <div id="uno-semantics-application" role="application" tabindex="-1">
          <button id="uno-semantics-background" aria-label="Background command" tabindex="0">Background</button>
          <div id="uno-semantics-inactive-popup" aria-hidden="true" tabindex="-1">
            <div role="dialog" aria-label="Inactive dialog" xamlautomationid="inactive"><button>Inactive</button></div>
          </div>
          <div id="uno-semantics-popup" tabindex="-1">
            <div id="uno-semantics-dialog" role="group" aria-label="Current dialog" xamlautomationid="current">
              <input id="uno-semantics-modal-field" aria-label="Dialog field"><button id="uno-semantics-modal-close">Close</button>
              <div id="uno-semantics-picker" role="combobox" aria-label="Dialog choices" aria-expanded="false" tabindex="0"></div>
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
    const scope = (operation, automationId, generation) => frame.evaluate(({ operation, automationId, generation }) =>
      modalModule[operation](automationId, generation), { operation, automationId, generation });
    const begin = (id, generation) => scope('beginDialogScope', id, generation);
    const activate = (id, generation) => scope('activateDialogScope', id, generation);
    const end = (id, generation) => scope('endDialogScope', id, generation);
    const current = frame.getByRole('dialog', { name: 'Current dialog', exact: true });
    const background = frame.getByRole('button', { name: 'Background command', exact: true });
    const waitModal = async (automationId, name) => {
      await frame.waitForFunction(id => {
        const element = document.querySelector('[xamlautomationid="' + CSS.escape(id) + '"]');
        return element?.getAttribute('role') === 'dialog' && element.getAttribute('aria-modal') === 'true';
      }, automationId);
      await frame.getByRole('dialog', { name, exact: true }).waitFor({ state: 'visible' });
    };
    await frame.evaluate(() => {
      const root = document.getElementById('uno-semantics-root');
      globalThis.modalFixture = { root, base: root.innerHTML };
    });
    await begin('current', 1);
    await frame.evaluate(() => {
      const root = modalFixture.root, dialog = document.getElementById('uno-semantics-dialog');
      // Native FocusTrap records Element objects, then incorrectly hides the
      // active dialog's ancestors along with unrelated background nodes.
      modalFixture.nativeSnapshots = [...root.querySelectorAll('[id^="uno-semantics-"]')]
        .filter(element => element !== dialog && !dialog.contains(element))
        .map(element => ({ element, hidden: element.getAttribute('aria-hidden'), tab: element.getAttribute('tabindex') }));
      for (const { element } of modalFixture.nativeSnapshots) {
        element.setAttribute('aria-hidden', 'true'); element.setAttribute('tabindex', '-1');
      }
      dialog.setAttribute('role', 'dialog'); dialog.setAttribute('aria-modal', 'true');
    });
    equal(await current.count(), 0, 'The captured native ancestor bug excludes the active dialog');
    await activate('current', 1);
    await waitModal('current', 'Current dialog');
    equal(await background.count(), 0, 'Background commands remain excluded');
    equal(await frame.getByRole('button', { name: 'Background command', exact: true, includeHidden: true }).count(), 1,
      'Native background controls are preserved for restoration');
    equal(await frame.getByRole('dialog', { name: 'Inactive dialog', exact: true }).count(), 0, 'Unrelated hidden popups stay excluded');
    equal(await frame.locator('#uno-semantics-outside').getAttribute('aria-hidden'), 'true', 'Nodes outside the semantic root are untouched');
    equal(await frame.locator('#uno-semantics-application').getAttribute('tabindex'), '-1', 'Container focusability is preserved');
    await current.getByRole('textbox', { name: 'Dialog field', exact: true }).focus();
    await activate('current', 1);
    equal(await frame.evaluate(() => document.activeElement.id), 'uno-semantics-modal-field', 'Reconciliation does not move browser/native focus');
    report.cases.push('pre-native baseline, active modal exposure and background isolation');

    await frame.evaluate(() => {
      modalFixture.root.insertAdjacentHTML('beforeend', '<div id="uno-semantics-choices" role="listbox" aria-label="Popup choices"><div id="uno-semantics-choice" role="option" aria-label="Choice" tabindex="-1">Choice</div></div>');
    });
    await frame.waitForFunction(() => document.getElementById('uno-semantics-choices').getAttribute('aria-hidden') === 'true');
    equal(await frame.getByRole('listbox', { name: 'Popup choices', exact: true }).count(), 0, 'An unrelated new popup is isolated');
    await frame.evaluate(() => {
      const picker = document.getElementById('uno-semantics-picker');
      picker.setAttribute('aria-controls', 'uno-semantics-choices'); picker.setAttribute('aria-expanded', 'true');
    });
    await frame.getByRole('listbox', { name: 'Popup choices', exact: true }).waitFor({ state: 'visible' });
    equal(await frame.getByRole('option', { name: 'Choice', exact: true }).count(), 1, 'Late aria-controls exposes the modal-owned sibling listbox');
    equal(await background.count(), 0, 'Controlled popup exposure preserves background isolation');
    await frame.evaluate(() => {
      document.getElementById('uno-semantics-picker').removeAttribute('aria-controls');
      document.getElementById('uno-semantics-choices').remove();
    });
    report.cases.push('late controlled sibling popup relationship');

    await frame.evaluate(() => {
      document.activeElement?.blur();
      modalFixture.oldApplication = document.getElementById('uno-semantics-application');
      // RebuildSemanticTree keeps the stable root and active managed scope,
      // recreating role=dialog without the old DOM's modal/background state.
      modalFixture.root.innerHTML = modalFixture.base.replace('role="group" aria-label="Current dialog"', 'role="dialog" aria-label="Current dialog"');
    });
    await waitModal('current', 'Current dialog');
    equal(await frame.evaluate(() => modalFixture.root === document.getElementById('uno-semantics-root') && !modalFixture.oldApplication.isConnected), true,
      'The native root survives while all controlled Element objects are replaced');
    equal(await background.count(), 0, 'Replacement background nodes are isolated');
    equal(await frame.locator('#uno-semantics-background').getAttribute('tabindex'), '-1', 'Replacement background focusability is isolated');
    await frame.evaluate(() => {
      document.getElementById('uno-semantics-background').setAttribute('tabindex', '7');
      // Native Closed restores its obsolete Element references. The bridge
      // must restore replacement nodes, without overwriting this external edit.
      for (const entry of modalFixture.nativeSnapshots) {
        for (const [name, value] of [['aria-hidden', entry.hidden], ['tabindex', entry.tab]])
          value === null ? entry.element.removeAttribute(name) : entry.element.setAttribute(name, value);
      }
      document.getElementById('uno-semantics-dialog').removeAttribute('role');
      document.getElementById('uno-semantics-dialog').removeAttribute('aria-modal');
    });
    await end('current', 1);
    await background.waitFor({ state: 'visible' });
    equal(await frame.locator('#uno-semantics-background').getAttribute('aria-hidden'), null, 'Closing restores replacement background visibility');
    equal(await frame.locator('#uno-semantics-background').getAttribute('tabindex'), '7', 'Closing preserves an external attribute value differing from the bridge write');
    equal(await frame.locator('#uno-semantics-application').getAttribute('aria-hidden'), null, 'Transient native ancestor hiding never poisons close restoration');
    equal(await frame.locator('#uno-semantics-inactive-popup').getAttribute('aria-hidden'), 'true', 'Original unrelated hidden state is retained');
    report.cases.push('semantic-root replacement and conditional close restoration');

    await begin('late', 2); await activate('late', 2);
    equal(await background.count(), 0, 'A missing active semantic target keeps the background excluded');
    equal(await frame.locator('#uno-semantics-background').getAttribute('tabindex'), '-1', 'Missing-target isolation also removes background keyboard stops');
    await frame.evaluate(() => modalFixture.root.insertAdjacentHTML('beforeend', '<div id="uno-semantics-late" role="group" aria-label="Late dialog" xamlautomationid="late"><button id="uno-semantics-late-command">Late command</button></div>'));
    await waitModal('late', 'Late dialog');
    equal(await background.count(), 0, 'An active modal emitted after Opened acquires background isolation');
    await end('late', 2);
    await background.waitFor({ state: 'visible' });
    equal(await frame.locator('#uno-semantics-late').getAttribute('role'), 'group', 'Closing restores the late-created node baseline');
    report.cases.push('active scope before semantic target creation');

    await frame.evaluate(() => modalFixture.root.innerHTML = modalFixture.base);
    await begin('current', 3);
    await frame.evaluate(() => {
      // Model native A trapping before activation: the bridge must adopt these
      // equal desired values while retaining the earlier pre-native baseline.
      const background = document.getElementById('uno-semantics-background');
      background.setAttribute('aria-hidden', 'true'); background.setAttribute('tabindex', '-1');
    });
    await activate('current', 3);
    await begin('nested', 4); await activate('nested', 4);
    equal(await current.count(), 0, 'A missing nested target cannot expose its lower active dialog');
    equal(await background.count(), 0, 'A missing nested target preserves application isolation');
    await frame.evaluate(() => document.getElementById('uno-semantics-dialog').insertAdjacentHTML('beforeend', '<div id="uno-semantics-nested" role="group" aria-label="Nested dialog" xamlautomationid="nested"><button id="uno-semantics-nested-command">Nested command</button></div>'));
    await waitModal('nested', 'Nested dialog');
    equal(await frame.getByRole('textbox', { name: 'Dialog field', exact: true }).count(), 0, 'Nested scope isolates parent controls');
    equal(await frame.getByRole('button', { name: 'Nested command', exact: true }).count(), 1, 'Nested controls remain accessible');
    await end('nested', 4);
    await waitModal('current', 'Current dialog');
    equal(await frame.getByRole('textbox', { name: 'Dialog field', exact: true }).count(), 1, 'Closing the child restores the active parent scope');
    equal(await background.count(), 0, 'Parent restoration does not expose the application background');
    await begin('nested', 5);
    await frame.evaluate(() => {
      const element = document.getElementById('uno-semantics-background');
      modalFixture.nestedNativeSnapshot = { element, hidden: element.getAttribute('aria-hidden'), tab: element.getAttribute('tabindex') };
    });
    await activate('nested', 5);
    await end('current', 3);
    await waitModal('nested', 'Nested dialog');
    equal(await background.count(), 0, 'An out-of-order parent close preserves the active child scope');
    await frame.evaluate(() => {
      // Native A close was ignored while B was active. B now restores its
      // already-hidden A snapshot; final bridge cleanup must release it.
      const entry = modalFixture.nestedNativeSnapshot;
      for (const [name, value] of [['aria-hidden', entry.hidden], ['tabindex', entry.tab]])
        value === null ? entry.element.removeAttribute(name) : entry.element.setAttribute(name, value);
    });
    await end('nested', 5);
    await background.waitFor({ state: 'visible' });
    equal(await frame.locator('#uno-semantics-background').getAttribute('tabindex'), '0', 'Final close restores the original background focusability');
    report.cases.push('nested and out-of-order scope closure');

    await begin('current', 6); await activate('current', 6);
    await begin('current', 7); await activate('current', 7);
    await end('current', 6);
    await waitModal('current', 'Current dialog');
    equal(await background.count(), 0, 'A stale close cannot release a newer opening of the same AutomationId');
    await end('current', 7);
    await background.waitFor({ state: 'visible' });
    equal(await frame.locator('#uno-semantics-application').getAttribute('aria-hidden'), null, 'The final scope restores accessible ancestry');
    report.cases.push('generation-safe reopened dialog');

    // Native virtualized options have logical roles even when their visual
    // bounds have not been projected. Their selected state comes exclusively
    // from the public ComboBox.SelectedIndex snapshot, never focused-item IDs.
    const comboScope = (operation, generation, selectedIndex) => frame.evaluate(({ operation, generation, selectedIndex }) =>
      modalModule[operation]('selection-picker', generation, selectedIndex), { operation, generation, selectedIndex });
    await frame.evaluate(() => {
      const root = document.getElementById('uno-semantics-root');
      root.innerHTML = '<div id="uno-semantics-selection-head" role="combobox" aria-label="Selection picker" aria-expanded="true" aria-activedescendant="uno-semantics-selection-a" xamlautomationid="selection-picker" tabindex="0"></div>';
      globalThis.selectionFixture = { root, options: '<div id="uno-semantics-selection-list" role="listbox" aria-label="Selection choices" style="position:absolute"><div id="uno-semantics-selection-a" role="option" aria-label="Alpha" aria-posinset="1" aria-setsize="3" tabindex="-1" style="position:absolute;width:0;height:0"></div><div id="uno-semantics-selection-b" role="option" aria-label="Beta" aria-posinset="2" aria-setsize="3" tabindex="-1" style="position:absolute;width:0;height:0"></div></div>' };
    });
    await comboScope('beginComboScope', 1, 1);
    await frame.evaluate(() => selectionFixture.root.insertAdjacentHTML('beforeend', selectionFixture.options));
    equal(await frame.locator('#uno-semantics-selection-b').getAttribute('aria-selected'), null,
      'Unrelated logical options are not projected before the live controls relationship');
    await frame.evaluate(() => document.getElementById('uno-semantics-selection-head').setAttribute('aria-controls', 'uno-semantics-selection-list'));
    const alpha = selected => frame.getByRole('option', { name: 'Alpha', exact: true, selected });
    const beta = selected => frame.getByRole('option', { name: 'Beta', exact: true, selected });
    await beta(true).waitFor({ state: 'attached' });
    equal(await alpha(false).count(), 1, 'Initial public selected index projects false onto other options');
    equal(await frame.locator('#uno-semantics-selection-head').getAttribute('aria-activedescendant'), 'uno-semantics-selection-a',
      'Selection projection leaves a different native focused option unchanged');
    report.cases.push('initial ComboBox selection with late logical popup relationship');

    await comboScope('updateComboScope', 1, 0);
    await alpha(true).waitFor({ state: 'attached' });
    equal(await beta(false).count(), 1, 'A public selection change clears the previously selected option');
    await comboScope('updateComboScope', 1, -1);
    equal(await alpha(false).count(), 1, 'SelectedIndex minus one clears the first option');
    equal(await beta(false).count(), 1, 'SelectedIndex minus one clears every option');
    await comboScope('updateComboScope', 1, 2);
    await frame.evaluate(() => document.getElementById('uno-semantics-selection-list').insertAdjacentHTML('beforeend', '<div id="uno-semantics-selection-c" role="option" aria-label="Gamma" aria-posinset="3" aria-setsize="3" tabindex="-1" style="position:absolute;width:0;height:0"></div>'));
    await frame.getByRole('option', { name: 'Gamma', exact: true, selected: true }).waitFor({ state: 'attached' });
    equal(await alpha(false).count(), 1, 'Late realization applies the current snapshot without changing other options');
    await frame.evaluate(() => {
      document.getElementById('uno-semantics-selection-a').setAttribute('aria-posinset', '3');
      document.getElementById('uno-semantics-selection-c').setAttribute('aria-posinset', '1');
    });
    await alpha(true).waitFor({ state: 'attached' });
    equal(await frame.getByRole('option', { name: 'Gamma', exact: true, selected: false }).count(), 1,
      'Virtualized positions, not DOM order or focus, define the selected index');
    report.cases.push('ComboBox selection changes, empty selection and late/reordered options');

    await comboScope('updateComboScope', 1, 1);
    await frame.evaluate(() => {
      selectionFixture.oldOption = document.getElementById('uno-semantics-selection-b');
      document.getElementById('uno-semantics-selection-list').remove();
      selectionFixture.root.insertAdjacentHTML('beforeend', selectionFixture.options);
    });
    await beta(true).waitFor({ state: 'attached' });
    equal(await frame.evaluate(() => !selectionFixture.oldOption.isConnected), true,
      'The selection scope projects onto replacement option objects');
    await comboScope('beginComboScope', 2, 0);
    equal(await comboScope('updateComboScope', 1, 1), false, 'A stale selection update cannot change a reopened ComboBox');
    equal(await comboScope('endComboScope', 1), false, 'A stale popup close cannot release the current ComboBox scope');
    equal(await alpha(true).count(), 1, 'The newer opening retains its authoritative selected index');
    await frame.evaluate(() => document.getElementById('uno-semantics-selection-b').setAttribute('aria-selected', 'true'));
    await comboScope('endComboScope', 2);
    equal(await frame.locator('#uno-semantics-selection-a').getAttribute('aria-selected'), null,
      'Closing releases unchanged bridge metadata to its original baseline');
    equal(await frame.locator('#uno-semantics-selection-b').getAttribute('aria-selected'), 'true',
      'Closing preserves a later native or external selected-state value');
    await frame.evaluate(async () => {
      document.getElementById('uno-semantics-selection-list').remove();
      selectionFixture.root.insertAdjacentHTML('beforeend', selectionFixture.options);
      await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
    });
    equal(await frame.locator('#uno-semantics-selection-a').getAttribute('aria-selected'), null,
      'No ComboBox observer continues projecting after the final close');
    report.cases.push('ComboBox replacement, generation safety and scoped metadata cleanup');

    const nativeFocus = (generation, focusRevision, focusedIndex) => frame.evaluate(({ generation, focusRevision, focusedIndex }) =>
      modalModule.focusComboScope('selection-picker', generation, focusRevision, focusedIndex), { generation, focusRevision, focusedIndex });
    const settleFocus = () => frame.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
    await frame.evaluate(() => {
      const root = selectionFixture.root;
      root.innerHTML = '<div id="uno-semantics-focus-parent" role="group" tabindex="-1"></div><div id="uno-semantics-selection-head" role="combobox" aria-label="Selection picker" aria-expanded="true" aria-controls="uno-semantics-selection-list" aria-activedescendant="uno-semantics-selection-a" xamlautomationid="selection-picker" tabindex="0"></div>' + selectionFixture.options;
      root.insertAdjacentHTML('afterend', '<input id="external-focus-input" aria-label="External DOM editor">');
      selectionFixture.lateOption = '<div id="uno-semantics-selection-c" role="option" aria-label="Gamma" aria-posinset="3" aria-setsize="3" tabindex="-1" style="position:absolute;width:0;height:0"></div>';
    });
    await comboScope('beginComboScope', 3, 0);
    await frame.locator('#uno-semantics-focus-parent').focus();
    await nativeFocus(3, 1, 1);
    await frame.waitForFunction(() => document.activeElement.id === 'uno-semantics-selection-b');
    equal(await alpha(true).count(), 1, 'Projecting the actual focused item does not change the selected item');
    equal(await beta(false).count(), 1, 'A focused option can remain unselected until native Enter commits it');
    equal(await frame.locator('#uno-semantics-selection-head').getAttribute('aria-activedescendant'), 'uno-semantics-selection-a',
      'The focused-item bridge never infers or rewrites the head selection relationship');
    report.cases.push('actual ComboBox item focus independent from selection');

    await frame.locator('#uno-semantics-focus-parent').focus();
    await nativeFocus(3, 2, 2);
    await frame.evaluate(() => document.getElementById('uno-semantics-selection-list').insertAdjacentHTML('beforeend', selectionFixture.lateOption));
    await frame.waitForFunction(() => document.activeElement.id === 'uno-semantics-selection-c');
    equal(await alpha(true).count(), 1, 'Late option focus preserves the authoritative selected index');
    await frame.locator('#uno-semantics-focus-parent').focus();
    await frame.locator('#uno-semantics-selection-c').evaluate(element => element.remove());
    await nativeFocus(3, 3, 2);
    await nativeFocus(3, 4, -1);
    await frame.evaluate(() => document.getElementById('uno-semantics-selection-list').insertAdjacentHTML('beforeend', selectionFixture.lateOption));
    await settleFocus();
    equal(await frame.evaluate(() => document.activeElement.id), 'uno-semantics-focus-parent',
      'A native focus departure cancels a pending late-option projection');
    equal(await nativeFocus(3, 3, 0), false, 'An older native focus revision cannot revive a cancelled intent');
    await frame.locator('#uno-semantics-selection-c').evaluate(element => element.remove());
    await nativeFocus(3, 5, 2);
    await frame.getByRole('textbox', { name: 'External DOM editor', exact: true }).focus();
    await frame.evaluate(() => document.getElementById('uno-semantics-selection-list').insertAdjacentHTML('beforeend', selectionFixture.lateOption));
    await settleFocus();
    equal(await frame.evaluate(() => document.activeElement.id), 'external-focus-input',
      'Intervening DOM editor focus is never stolen when an option arrives');
    await nativeFocus(3, 6, 0);
    equal(await frame.evaluate(() => document.activeElement.id), 'external-focus-input',
      'A queued native snapshot cannot steal a later external editor focus');
    report.cases.push('late focused options and native/DOM focus cancellation');

    await frame.locator('#uno-semantics-focus-parent').focus();
    await frame.locator('#uno-semantics-selection-c').evaluate(element => element.remove());
    await nativeFocus(3, 7, 2);
    await comboScope('endComboScope', 3);
    await comboScope('beginComboScope', 4, 0);
    await frame.evaluate(() => document.getElementById('uno-semantics-selection-list').insertAdjacentHTML('beforeend', selectionFixture.lateOption));
    await settleFocus();
    equal(await frame.evaluate(() => document.activeElement.id), 'uno-semantics-focus-parent',
      'Closing and reopening cannot inherit a pending focus request');
    equal(await nativeFocus(3, 8, 0), false, 'A stale open generation cannot move current popup focus');
    await nativeFocus(4, 1, 1);
    await frame.waitForFunction(() => document.activeElement.id === 'uno-semantics-selection-b');
    equal(await alpha(true).count(), 1, 'A fresh focus generation still leaves selection unchanged');
    await comboScope('endComboScope', 4);
    report.cases.push('generation-scoped native ComboBox focus lifetime');

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
