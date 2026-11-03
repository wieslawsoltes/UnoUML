import assert from 'node:assert/strict';

export async function verifyAccessibility(page) {
  const button = name => page.getByRole('button', { name, exact: true });
  await button('Split').click();
  const source = page.getByRole('textbox', { name: 'PlantUML source', exact: true });
  await source.waitFor({ state: 'visible' });
  assert.match(await source.inputValue(), /^@startuml/);
  assert.equal(await button('Undo').isDisabled(), true, 'An empty undo history is exposed as disabled');
  await button('Fit diagram').waitFor({ state: 'visible' });
  await page.getByRole('textbox', { name: 'Search library', exact: true }).waitFor({ state: 'visible' });
  const original = await source.inputValue();
  for (let attempt = 0; attempt < 2; attempt++) {
    await button('Add custom PlantUML…').click();
    const dialog = page.getByRole('dialog', { name: 'Insert PlantUML', exact: true });
    await dialog.waitFor({ state: 'visible' });
    assert.equal(await dialog.getAttribute('aria-modal'), 'true');
    const field = dialog.getByRole('textbox', { name: 'PlantUML to insert on this page', exact: true });
    assert.equal(await field.inputValue(), '', 'Reopening a dialog creates a fresh named editor');
    await field.fill('class Cancelled');
    await dialog.getByRole('button', { name: 'Cancel', exact: true }).click();
    await dialog.waitFor({ state: 'hidden' });
    assert.equal(await source.inputValue(), original, 'Cancelling a dialog preserves the document');
  }
  console.log('Verified named native XAML buttons, source and search inputs, disabled state and reopened modal dialogs.');
  return source;
}
