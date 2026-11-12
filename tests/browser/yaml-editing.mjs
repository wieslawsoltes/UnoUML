import { activateControl, fillControl } from './native-controls.mjs';
import assert from 'node:assert/strict';

export async function verifyYamlEditing(page, source) {
  await page.setViewportSize({width:1480,height:960});
  await activateControl(page, page.getByRole('button', {name:'Split',exact:true}));
  await source.waitFor({state:'visible'});
  const paper = page.locator('.paper').first(), surface = page.locator('.surface').first();
  const editor = page.getByRole('textbox',{name:'Edit diagram label'});
  const render = async value => {
    const previous = await paper.locator('svg').elementHandle();
    await fillControl(page, source, value);
    if (previous) await page.waitForFunction(node => !node.isConnected,previous);
    await paper.locator('svg').waitFor();
    assert.equal(await paper.getAttribute('data-renderer'),'managed');
  };

  const folded = '@startyaml\nvalue:\n\tFirst 🪴 # first comment\n\t# middle comment\n\n\t  \'second\'  # next comment\n\t"third" # last comment\nnext: retained\n@endyaml';
  await render(folded);
  await paper.locator('[data-edit-kind="yaml-folded-text"]').dblclick();
  assert.equal(await editor.inputValue(),'First 🪴 second third');
  await fillControl(page, editor, 'Updated\nline');
  await editor.press('Control+Enter');
  const changed = folded.replace('First 🪴','"Updated\\nline"').replace("'second'",'').replace('"third"','');
  assert.equal(await source.inputValue(),changed);
  await paper.locator('[data-edit-kind="yaml-folded-text"]').filter({hasText:/Updated/}).waitFor();
  await surface.press('Control+z');
  assert.equal(await source.inputValue(),folded);
  await paper.locator('[data-edit-kind="yaml-folded-text"]').filter({hasText:/First/}).waitFor();
  await surface.press('Control+y');
  assert.equal(await source.inputValue(),changed);
  await paper.locator('[data-edit-kind="yaml-folded-text"]').filter({hasText:/Updated/}).waitFor();

  const literal = '@startyaml\n!custom: literal key\nbase: &base editor\ncopy: *base\ntyped: !!str 012\n@endyaml';
  await render(literal);
  const alias = literal.indexOf('*base');
  await paper.locator(`[data-edit-start="${alias}"][data-edit-role="value"]`).dblclick();
  assert.equal(await editor.inputValue(),'*base');
  await fillControl(page, editor, '*base next');
  await editor.press('Enter');
  assert.equal(await source.inputValue(),literal.replace('copy: *base','copy: "*base next"'));
  await paper.locator('[data-edit-role="value"]').filter({hasText:/^\*base next$/}).waitFor();
  await surface.press('Control+z');
  assert.equal(await source.inputValue(),literal);

  const tags = '@startyaml\nbody:\n  !!str 42\n  !local value\nnext: retained\n@endyaml';
  await render(tags);
  await paper.locator('[data-edit-kind="yaml-folded-text"]').dblclick();
  assert.equal(await editor.inputValue(),'!!str 42 !local value');
  await fillControl(page, editor, 'Tags remain text');
  await editor.press('Control+Enter');
  assert.equal(await source.inputValue(),tags.replace('!!str 42','"Tags remain text"').replace('!local value',''));
  await paper.locator('[data-edit-kind="yaml-folded-text"]').filter({hasText:'Tags remain text'}).waitFor();

  const duplicate = '@startyaml\nfirst: previous\nsecond: retained\nfirst: final\n@endyaml';
  await render(duplicate);
  assert.equal(await paper.locator('[data-edit-role="value"]').filter({hasText:'previous'}).count(),0);
  const last = duplicate.indexOf('final');
  await paper.locator(`[data-edit-start="${last}"][data-edit-role="value"]`).dblclick();
  await fillControl(page, editor, 'Latest');
  await editor.press('Enter');
  assert.equal(await source.inputValue(),duplicate.replace('first: final','first: Latest'));
  await paper.locator('[data-edit-role="value"]').filter({hasText:'Latest'}).waitFor();
  await surface.press('Control+z');
  assert.equal(await source.inputValue(),duplicate);

  const block = '@startyaml\nroot:\n\tbody: >-\n\t\tfirst\n\t\tsecond\nlast: retained\n@endyaml';
  await render(block);
  await paper.locator('[data-edit-kind="yaml-block"]').dblclick();
  assert.equal(await editor.inputValue(),'first second');
  await fillControl(page, editor, 'Changed\nnext');
  await editor.press('Control+Enter');
  assert.equal(await source.inputValue(),block.replace('>-\n\t\tfirst\n\t\tsecond','"Changed\\nnext"'));
  await paper.locator('[data-edit-kind="yaml-double"]').filter({hasText:/Changed/}).waitFor();
  await surface.press('Control+z');
  assert.equal(await source.inputValue(),block);
  await paper.locator('[data-edit-kind="yaml-block"]').waitFor();
  const sequenceBlock = '@startyaml\nitems:\n  - body: >2- # retained\n      first\n      second\nnext: retained\n@endyaml';
  await render(sequenceBlock);
  await paper.locator('[data-edit-kind="yaml-block"]').dblclick();
  assert.equal(await editor.inputValue(),'first second');
  await fillControl(page, editor, 'Edited\nsequence body');
  await editor.press('Control+Enter');
  assert.equal(await source.inputValue(),sequenceBlock.replace('>2- # retained\n      first\n      second','"Edited\\nsequence body" # retained'));
  await paper.locator('[data-edit-kind="yaml-double"]').filter({hasText:/Edited/}).waitFor();
  await surface.press('Control+z');
  assert.equal(await source.inputValue(),sequenceBlock);
  await paper.locator('[data-edit-kind="yaml-block"]').waitFor();
  console.log('Verified YAML folded text, comment and tab preservation, literal indicators, duplicate-key targeting and canvas undo/redo.');
}
