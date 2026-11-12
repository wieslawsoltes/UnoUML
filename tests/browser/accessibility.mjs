import assert from 'node:assert/strict';
import { activateAccessibility, fillControl } from './native-controls.mjs';

export async function verifyAccessibility(page) {
  await activateAccessibility(page);
  assert.equal(await page.getByRole('dialog', { includeHidden: true }).count(), 0,
    'Keyboard accessibility activation must not invoke a native application command');
  const button = name => page.getByRole('button', { name, exact: true });
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
    await dialog.waitFor({ state: 'visible' });
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
    await dialog.waitFor({ state: 'visible' });
    assert.equal(await dialog.getAttribute('aria-modal'), 'true');
    await button('New').waitFor({ state: 'hidden' });
    assert.equal(await page.getByRole('button', { name: 'New', exact: true, includeHidden: true }).count(), 1,
      'The modal excludes the existing background command from the accessible tree');
    const picker = dialog.getByRole('combobox', { name: 'Starting diagram', exact: true });
    await picker.press('Enter');
    const choices = page.getByRole('listbox', { name: 'Starting diagram', exact: true });
    await choices.waitFor({ state: 'visible' });
    // Uno 7 uses the ARIA combobox relationship (controls + activedescendant),
    // replacing the native-DOM adapter's aria-owns workaround for portalled lists.
    assert.equal(await picker.getAttribute('aria-controls'), await choices.getAttribute('id'));
    assert.equal(await picker.getAttribute('aria-haspopup'), 'listbox');
    const checkout = choices.getByRole('option', { name: 'Checkout flow', exact: true, selected: true });
    await checkout.waitFor({ state: 'visible' });
    assert.equal(await picker.getAttribute('aria-activedescendant'), await checkout.getAttribute('id'),
      'The combobox exposes its selected option through the live listbox');
    assert.equal(await checkout.getAttribute('aria-posinset'), '1');
    assert.ok(Number(await checkout.getAttribute('aria-setsize')) >= 2);
    assert.equal(await picker.evaluate(element => {
      const focused = document.activeElement;
      const popup = document.getElementById(element.getAttribute('aria-controls'));
      return !!(element.closest('[role="dialog"]')?.contains(focused) || popup?.contains(focused));
    }), true, 'Keyboard focus remains in the modal or its controlled popup');
    await choices.getByRole('option', { name: 'Domain model', exact: true }).press('Enter');
    await choices.waitFor({ state: 'hidden' });
    await dialog.getByRole('combobox', { name: 'Starting diagram', expanded: false }).waitFor();
    assert.equal(await picker.getAttribute('aria-controls'), null, 'Closing the popup releases its list relationship');
    await picker.press('Enter');
    const selected = choices.getByRole('option', { name: 'Domain model', exact: true, selected: true });
    await selected.waitFor({ state: 'visible' });
    assert.equal(await picker.getAttribute('aria-activedescendant'), await selected.getAttribute('id'));
    await choices.getByRole('option', { name: 'Checkout flow', exact: true, selected: false }).waitFor({ state: 'visible' });
    await picker.press('Escape');
    await choices.waitFor({ state: 'hidden' });
    await dialog.getByRole('button', { name: 'Cancel', exact: true }).press('Enter');
    await dialog.waitFor({ state: 'hidden' });
    await button('New').waitFor({ state: 'visible' });
    assert.equal(await source.inputValue(), original, 'Popup selection and cancellation preserve the open document');
  }
  await button('Library actions').press('Enter');
  await page.getByRole('menuitem', { name: 'Rendering engine…', exact: true }).press('Enter');
  const renderer = page.getByRole('dialog', { name: 'Rendering engine', exact: true });
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
