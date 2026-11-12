import { clickControl, fillControl } from './native-controls.mjs';
import assert from 'node:assert/strict';

export async function verifyCompatibilityEditing(page, source) {
  const paper = page.locator('.paper').first();
  const surface = page.locator('.surface').first();
  const field = page.getByRole('textbox', {name:'Edit diagram label'});
  const richClass = '@startuml\nclass Customer\n@enduml';
  await fillControl(page, source, richClass);
  await paper.locator('[data-qualified-name="Customer"] text').filter({hasText:/^Customer$/}).dblclick();
  await clickControl(page, page.getByRole('button', {name:'Bold (Ctrl+B)',exact:true}));
  await field.press('Enter');
  assert.equal(await source.inputValue(),'@startuml\nclass "**Customer**" as Customer\n@enduml');
  await paper.locator('[data-qualified-name="Customer"] tspan[font-weight="bold"]').filter({hasText:/^Customer$/}).waitFor();
  assert.equal(await paper.getAttribute('data-renderer'),'managed');
  await surface.press('Control+z');
  assert.equal(await source.inputValue(),richClass);
  const reference = '@startuml\nparticipant Client\nparticipant Server\nref over Client, Server\nOriginal reference\nend ref\nClient -> Server : Continue\n@enduml';
  await fillControl(page, source, reference);
  await paper.locator('[data-source-line="3"] text').filter({hasText:/Original reference/}).dblclick();
  await fillControl(page, field, 'First line\nSecond line');
  await field.press('Control+Enter');
  assert.equal(await source.inputValue(), reference.replace('Original reference','First line\nSecond line'));
  await paper.locator('[data-source-line="3"] text').filter({hasText:/Second line/}).waitFor();
  await surface.press('Control+z');
  assert.equal(await source.inputValue(),reference);

  for (const [dialect, language, initial, fraction] of [
    ['math','asciimath','x^2','(x^2)/(b)'],
    ['latex','tex','y^2','\\frac{y^2}{b}']
  ]) {
    const formula = '@start' + dialect + '\n' + initial + '\n@end' + dialect;
    await fillControl(page, source, formula);
    await paper.locator('.math-expression text').filter({hasText:new RegExp('^'+initial[0]+'$')}).first().dblclick();
    assert.equal(await field.inputValue(),initial);
    await clickControl(page, page.getByRole('button',{name:'Insert fraction',exact:true}));
    assert.equal(await field.inputValue(),fraction);
    await field.press('Control+Enter');
    assert.equal(await source.inputValue(),formula.replace(initial,fraction));
    await paper.locator('.math-expression path').first().waitFor({state:'attached'});
    await paper.locator('.math-expression text').filter({hasText:/^b$/}).waitFor();
    assert.equal(await paper.getAttribute('data-renderer'),'managed',language + ' remains native after structural formula edit');
    await surface.press('Control+z');
    assert.equal(await source.inputValue(),formula);
  }

  const ditaa = '@startditaa\n+-------+\n| Box   |\n+-------+\n@endditaa';
  await fillControl(page, source, ditaa);
  await paper.locator('[data-ditaa-shape]').first().dblclick({position:{x:4,y:4}});
  const drawing = '+-------+    +-------+\n| Input +--->| Output|\n+-------+    +-------+';
  await fillControl(page, field, drawing);
  await clickControl(page, page.getByRole('button',{name:'Apply diagram content (Ctrl+Enter)',exact:true}));
  assert.equal(await source.inputValue(),'@startditaa\n'+drawing+'\n@endditaa');
  await paper.locator('.ditaa-label text').filter({hasText:/^Output$/}).waitFor();
  await surface.press('Control+z');
  assert.equal(await source.inputValue(),ditaa);

  const c4 = '@startuml\n!include <C4/C4_Context>\nPerson(\\\n $label="Customer",\\\n $alias=user)\nSystem(app,"Portal")\nRel($label="Visits", $to=app, $from=user)\n@enduml';
  await fillControl(page, source, c4);
  await paper.locator('[data-qualified-name="user"] text').filter({hasText:/^Customer$/}).first().dblclick();
  await fillControl(page, field, 'Account "owner"');
  await field.press('Enter');
  const renamed = c4.replace('"Customer"','"Account <U+0022>owner<U+0022>"');
  assert.equal(await source.inputValue(),renamed);
  await paper.locator('[data-qualified-name="user"] text').filter({hasText:/owner/}).first().waitFor();
  await paper.locator('.link text').filter({hasText:/Visits/}).first().dblclick();
  await fillControl(page, field, 'Opens');
  await field.press('Enter');
  assert.equal(await source.inputValue(),renamed.replace('"Visits"','"Opens"'));
  console.log('Verified rich label tools, multiline references, formula structural tools, Ditaa geometry, continued/named C4 editing, imported-source persistence and transactional cancellation.');
}
