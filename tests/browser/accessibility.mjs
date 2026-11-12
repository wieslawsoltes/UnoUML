import assert from 'node:assert/strict';
import { activateAccessibility, activateControl, fillControl } from './native-controls.mjs';
import { traceNativeInput } from './native-input-trace.mjs';

export async function verifyAccessibility(page) {
  await activateAccessibility(page);
  // Uno also gives nonmodal tooltip PopupPanels role=dialog. Check the actual
  // application ContentDialogs, whose public identity is assigned pre-tree.
  assert.equal(await page.getByRole('dialog', { includeHidden: true })
    .and(page.locator('[xamlautomationid^="UnoUML.Dialog."]')).count(), 0,
    'Keyboard accessibility activation must not invoke a native application command');
  const button = name => page.getByRole('button', { name, exact: true });
  // The visible semantic node can precede the queued ContentDialog.Opened
  // reconciliation. Require the complete public modal contract before input.
  const waitForModal = dialog => dialog.and(page.locator('[aria-modal="true"]')).waitFor({ state: 'visible' });
  await button('Split').press('Enter');
  const source = page.getByRole('textbox', { name: 'PlantUML source', exact: true });
  await source.waitFor({ state: 'visible' });
  assert.equal(await source.evaluate(element => element.tagName), 'TEXTAREA', 'Multiline source uses the native semantic textarea');
  assert.ok(await source.getAttribute('xamlautomationid'), 'Source projection exposes its public XAML AutomationId');
  assert.match(await source.inputValue(), /^@startuml/);
  assert.equal(await button('Undo').isDisabled(), true, 'An empty undo history is exposed as disabled');
  await button('Fit diagram').waitFor({ state: 'visible' });
  await page.getByRole('textbox', { name: 'Search library', exact: true }).waitFor({ state: 'visible' });
  const original = await source.inputValue();
  for (let attempt = 0; attempt < 2; attempt++) {
    await button('Add custom PlantUML…').press('Enter');
    const dialog = page.getByRole('dialog', { name: 'Insert PlantUML', exact: true });
    await waitForModal(dialog);
    assert.equal(await dialog.getAttribute('aria-modal'), 'true');
    const field = dialog.getByRole('textbox', { name: 'PlantUML to insert on this page', exact: true });
    assert.equal(await field.inputValue(), '', 'Reopening a dialog creates a fresh named editor');
    await fillControl(page, field, 'class Cancelled');
    await dialog.getByRole('button', { name: 'Cancel', exact: true }).press('Enter');
    await dialog.waitFor({ state: 'hidden' });
    assert.equal(await source.inputValue(), original, 'Cancelling a dialog preserves the document');
  }
  for (let attempt = 0; attempt < 2; attempt++) {
    await button('New').press('Enter');
    const dialog = page.getByRole('dialog', { name: 'New document', exact: true });
    await waitForModal(dialog);
    assert.equal(await dialog.getAttribute('aria-modal'), 'true');
    await button('New').waitFor({ state: 'hidden' });
    assert.equal(await page.getByRole('button', { name: 'New', exact: true, includeHidden: true }).count(), 1,
      'The modal excludes the existing background command from the accessible tree');
    const picker = dialog.getByRole('combobox', { name: 'Starting diagram', exact: true });
    await picker.press('Enter');
    const choices = page.getByRole('listbox', { name: 'Starting diagram', exact: true });
    // Uno's virtualized listbox is a logical accessibility root without a box.
    // Role resolution still excludes aria-hidden; native option focus + Enter
    // performs the actual selection even if its semantic bounds are pending.
    await choices.waitFor({ state: 'attached' });
    // Uno 7 uses the ARIA combobox relationship (controls + activedescendant),
    // replacing the native-DOM adapter's aria-owns workaround for portalled lists.
    assert.equal(await picker.getAttribute('aria-controls'), await choices.getAttribute('id'));
    assert.equal(await picker.getAttribute('aria-haspopup'), 'listbox');
    const checkout = choices.getByRole('option', { name: 'Checkout flow', exact: true, selected: true });
    await checkout.waitFor({ state: 'attached' });
    assert.equal(await picker.getAttribute('aria-activedescendant'), await checkout.getAttribute('id'),
      'The combobox exposes its selected option through the live listbox');
    assert.equal(await checkout.getAttribute('aria-posinset'), '1');
    assert.ok(Number(await checkout.getAttribute('aria-setsize')) >= 2);
    // The public native-focus snapshot is queued after OnOpen completes, and
    // logical option DOM can arrive on a later frame. Wait for that projection
    // while preserving the exact modal/controlled-popup focus requirement.
    const focusReady = await page.waitForFunction(automationId => {
      const element = [...document.querySelectorAll('[role="combobox"][xamlautomationid]')]
        .find(candidate => candidate.getAttribute('xamlautomationid') === automationId);
      if (!element) return false;
      const focused = document.activeElement;
      const popup = document.getElementById(element.getAttribute('aria-controls'));
      return !!(element.closest('[role="dialog"]')?.contains(focused) || popup?.contains(focused));
    }, await picker.getAttribute('xamlautomationid'));
    assert.equal(await focusReady.jsonValue(), true, 'Keyboard focus remains in the modal or its controlled popup');
    await focusReady.dispose();
    const domain = choices.getByRole('option', { name: 'Domain model', exact: true });
    // Observe the background editor's public native-owner reader while the
    // modal excludes that editor from the accessible interaction tree.
    const diagnosticSource = page.getByRole('textbox', { name: 'PlantUML source', exact: true, includeHidden: true });
    await traceNativeInput(page, diagnosticSource, 'native-option-focus', async () => {
      await domain.focus();
      // Native focused-item projection is queued. Re-resolve the current
      // semantic option while acknowledging focus; do not request focus again.
      await domain.and(page.locator(':focus')).waitFor({ state: 'attached' });
      assert.equal(await domain.evaluate(element => document.activeElement === element), true,
        'Logical option focus enters the native ComboBoxItem before committing');
    }, { failureOnly: true });
    await page.keyboard.press('Enter');
    await choices.waitFor({ state: 'detached' });
    await dialog.getByRole('combobox', { name: 'Starting diagram', expanded: false }).waitFor();
    assert.equal(await picker.getAttribute('aria-controls'), null, 'Closing the popup releases its list relationship');
    await picker.press('Enter');
    const selected = choices.getByRole('option', { name: 'Domain model', exact: true, selected: true });
    await selected.waitFor({ state: 'attached' });
    assert.equal(await picker.getAttribute('aria-activedescendant'), await selected.getAttribute('id'));
    await choices.getByRole('option', { name: 'Checkout flow', exact: true, selected: false }).waitFor({ state: 'attached' });
    await picker.press('Escape');
    await choices.waitFor({ state: 'detached' });
    await dialog.getByRole('button', { name: 'Cancel', exact: true }).press('Enter');
    await dialog.waitFor({ state: 'hidden' });
    await button('New').waitFor({ state: 'visible' });
    assert.equal(await source.inputValue(), original, 'Popup selection and cancellation preserve the open document');
  }
  const renderer = page.getByRole('dialog', { name: 'Rendering engine', exact: true });
  // Preserve Enter coverage while waiting for each current native command to
  // be visible and focused. Keep the passive trace through modal acceptance so
  // a delayed native focus override identifies the actual key recipient.
  await traceNativeInput(page, source, 'renderer-menu-activation', async () => {
    await activateControl(page, button('Library actions'), { key: 'Enter' });
    await activateControl(page, page.getByRole('menuitem', { name: 'Rendering engine…', exact: true }), { key: 'Enter' });
    await waitForModal(renderer);
  }, { failureOnly: true });
  assert.equal(await renderer.getAttribute('aria-modal'), 'true');
  const serverName = 'Use a PlantUML server for this document';
  const nativeName = 'Use the C# renderer for supported syntax';
  const encodedName = 'Encode the diagram in the request URL (GET)';
  await renderer.getByRole('checkbox', { name: nativeName, checked: true, disabled: false }).waitFor();
  await renderer.getByRole('checkbox', { name: encodedName, checked: false, disabled: true }).waitFor();
  const server = renderer.getByRole('switch', { name: serverName, exact: true });
  const endpoint = renderer.getByRole('textbox', { name: 'Server SVG endpoint', exact: true });
  assert.equal(await server.isChecked(), false);
  assert.equal(await endpoint.isDisabled(), true);
  await server.press('Space');
  await renderer.getByRole('switch', { name: serverName, checked: true }).waitFor();
  await renderer.getByRole('checkbox', { name: nativeName, disabled: true }).waitFor();
  await renderer.getByRole('checkbox', { name: encodedName, disabled: false }).waitFor();
  assert.equal(await endpoint.isDisabled(), false);
  await renderer.getByRole('checkbox', { name: encodedName }).press('Space');
  await renderer.getByRole('checkbox', { name: encodedName, checked: true }).waitFor();
  await server.press('Space');
  await renderer.getByRole('switch', { name: serverName, checked: false }).waitFor();
  await renderer.getByRole('checkbox', { name: nativeName, disabled: false }).waitFor();
  await renderer.getByRole('checkbox', { name: encodedName, checked: true, disabled: true }).waitFor();
  assert.equal(await endpoint.isDisabled(), true);
  await renderer.getByRole('button', { name: 'Cancel', exact: true }).press('Enter');
  await renderer.waitFor({ state: 'hidden' });
  console.log('Verified named XAML controls, reopened modal/popup lifecycles, accessible list selection and synchronized renderer toggle states.');
  return source;
}
