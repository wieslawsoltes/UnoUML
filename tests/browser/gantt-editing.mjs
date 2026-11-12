import { clickControl, fillControl } from './native-controls.mjs';
import assert from 'node:assert/strict';

export async function verifyGanttEditing(page,source) {
  await page.setViewportSize({width:1480,height:960});
  await clickControl(page, page.getByRole('button',{name:'Split',exact:true}));
  await source.waitFor({state:'visible'});
  const paper=page.locator('.paper').first(),surface=page.locator('.surface').first();
  const fixture='@startgantt\nProject starts 2026-01-05\n[Design] as [D] lasts 3 days \' keep plan\n[Build] starts at [D]\'s end and lasts 5 days\n[Ship] happens at [Build]\'s end\n@endgantt';
  const task=id=>paper.locator(`g.task[data-gantt-id="${id}"]`);
  const undo=async expected=>{
    await clickControl(page, page.getByRole('button',{name:'Undo',exact:true}));
    await page.waitForFunction(value=>[...document.querySelectorAll('textarea')].some(e=>e.getAttribute('aria-label')==='PlantUML source'&&e.value===value),expected);
    assert.equal(await source.inputValue(),expected);
  };
  await fillControl(page, source, fixture);
  await task('Build').waitFor();
  assert.equal(await paper.getAttribute('data-renderer'),'managed');
  await clickControl(page, page.getByRole('button',{name:'Fit diagram',exact:true}));

  // Real pointer movement changes dates, propagates a dependent milestone and
  // remains one undo transaction without introducing generic canvas metadata.
  let bar=task('Build').locator('.gantt-bar'),box=await bar.boundingBox();
  let day=await task('Build').evaluate(e=>Number(e.dataset.ganttUnit)*e.ownerSVGElement.getScreenCTM().a);
  await page.mouse.move(box.x+box.width/2,box.y+box.height/2);
  await page.mouse.down();await page.mouse.move(box.x+box.width/2+2*day,box.y+box.height/2,{steps:8});await page.mouse.up();
  await paper.locator('g.task[data-gantt-id="Build"][data-gantt-start="5"]').waitFor();
  await paper.locator('g.task[data-gantt-id="Ship"][data-gantt-start="9"]').waitFor();
  assert.ok((await source.inputValue()).includes('[Build] lasts 5 days and starts D+5'));
  assert.ok(!(await source.inputValue()).includes('@unouml:canvas'));
  await undo(fixture);
  await paper.locator('g.task[data-gantt-id="Build"][data-gantt-start="3"]').waitFor();

  // The right grip changes duration while keeping the dependency; the left
  // grip keeps the finish date and establishes an explicit new start.
  await clickControl(page, page.getByRole('button',{name:'Fit diagram',exact:true}));
  await clickControl(page, task('Build').locator('.gantt-bar'));
  let handle=page.locator('.gantt-handle[data-gantt-handle="end"]');
  await handle.waitFor({state:'visible'});box=await handle.boundingBox();
  day=await task('Build').evaluate(e=>Number(e.dataset.ganttUnit)*e.ownerSVGElement.getScreenCTM().a);
  await page.mouse.move(box.x+box.width/2,box.y+box.height/2);await page.mouse.down();
  await page.mouse.move(box.x+box.width/2+2*day,box.y+box.height/2,{steps:8});await page.mouse.up();
  await paper.locator('g.task[data-gantt-id="Build"][data-gantt-duration="7"][data-gantt-start="3"]').waitFor();
  assert.ok((await source.inputValue()).includes("starts at [D]'s end"),'Finish resize retains the predecessor constraint');
  await undo(fixture);

  await clickControl(page, page.getByRole('button',{name:'Fit diagram',exact:true}));
  await clickControl(page, task('Build').locator('.gantt-bar'));
  handle=page.locator('.gantt-handle[data-gantt-handle="start"]');box=await handle.boundingBox();
  day=await task('Build').evaluate(e=>Number(e.dataset.ganttUnit)*e.ownerSVGElement.getScreenCTM().a);
  await page.mouse.move(box.x+box.width/2,box.y+box.height/2);await page.mouse.down();
  await page.mouse.move(box.x+box.width/2+day,box.y+box.height/2,{steps:8});await page.mouse.up();
  await paper.locator('g.task[data-gantt-id="Build"][data-gantt-start="4"][data-gantt-duration="4"]').waitFor();
  await undo(fixture);

  // An interrupted gesture restores the current render without touching source.
  await clickControl(page, task('Build').locator('.gantt-bar'));bar=task('Build').locator('.gantt-bar');box=await bar.boundingBox();
  await page.mouse.move(box.x+box.width/2,box.y+box.height/2);await page.mouse.down();
  await page.mouse.move(box.x+box.width/2+day,box.y+box.height/2,{steps:4});await page.keyboard.press('Escape');await page.mouse.up();
  assert.equal(await source.inputValue(),fixture);

  await clickControl(page, task('Build').locator('.gantt-bar'));
  await surface.focus();await page.keyboard.down('ArrowRight');await page.keyboard.down('ArrowRight');await page.keyboard.up('ArrowRight');
  await paper.locator('g.task[data-gantt-id="Build"][data-gantt-start="5"]').waitFor();
  // The SVG is installed before the managed render continuation restores the
  // selected task and enables authoring. Its visible resize handles witness
  // that completion before the next independent keyboard transaction.
  await page.locator('.gantt-handle[data-gantt-handle="end"]').waitFor({state:'visible'});
  const nudged=await source.inputValue();
  await surface.press('ArrowRight');
  await paper.locator('g.task[data-gantt-id="Build"][data-gantt-start="6"]').waitFor();
  await undo(nudged);
  await undo(fixture);

  // F2 edits the actual task token and preserves an explicit alias.
  await clickControl(page, task('D').locator('[data-edit-kind="gantt-label"]'));await surface.press('F2');
  const labelEditor=page.getByRole('textbox',{name:'Edit diagram label',exact:true});
  await labelEditor.waitFor({state:'visible'});await fillControl(page, labelEditor, 'Concept');await labelEditor.press('Enter');
  await task('D').locator('text').filter({hasText:/^Concept$/}).waitFor();
  assert.equal(await source.inputValue(),fixture.replace('[Design]','[Concept]'));
  await undo(fixture);

  await clickControl(page, task('Build').locator('[data-edit-kind="gantt-label"]'));
  await clickControl(page, page.getByRole('button', {name:'Edit task schedule…',exact:true}));
  let dialog=page.getByRole('dialog',{name:'Edit task schedule',exact:true});
  await fillControl(page, dialog.getByRole('textbox',{name:'Duration in days',exact:true}), '8');
  await fillControl(page, dialog.getByRole('textbox',{name:'Completion percent (optional)',exact:true}), '40');
  await fillControl(page, dialog.getByRole('textbox',{name:'Task color (#RRGGBB)',exact:true}), '#224466');
  await clickControl(page, dialog.getByRole('button',{name:'Apply',exact:true}));await dialog.waitFor({state:'hidden'});
  await paper.locator('g.task[data-gantt-id="Build"][data-gantt-duration="8"]').waitFor();
  assert.ok((await source.inputValue()).includes('is 40% completed and is colored in #224466'));
  await undo(fixture);

  // Every bar in a shared `then` statement must select its own task, including
  // the inspector and Delete path; source-line selection alone is ambiguous.
  const chained='@startgantt\n[A] lasts 2 days then [B] lasts 3 days then [C] lasts 1 day\n@endgantt';
  await fillControl(page, source, chained);await task('B').waitFor();await clickControl(page, page.getByRole('button',{name:'Fit diagram',exact:true}));
  await clickControl(page, task('B').locator('.gantt-bar'));
  await clickControl(page, page.getByRole('button', {name:'Rename schedule task…',exact:true}));
  dialog=page.getByRole('dialog',{name:'Rename schedule task',exact:true});
  assert.equal(await dialog.getByRole('textbox',{name:'Task name',exact:true}).inputValue(),'B');
  await fillControl(page, dialog.getByRole('textbox',{name:'Task name',exact:true}), 'Middle');
  await clickControl(page, dialog.getByRole('button',{name:'Rename',exact:true}));await dialog.waitFor({state:'hidden'});
  await task('Middle').waitFor();assert.equal(await source.inputValue(),chained.replace('[B]','[Middle]'));
  await undo(chained);

  await clickControl(page, task('B').locator('.gantt-bar'));await surface.press('Delete');
  dialog=page.getByRole('dialog',{name:'Remove schedule task',exact:true});
  await clickControl(page, dialog.getByRole('button',{name:'Remove',exact:true}));await dialog.waitFor({state:'hidden'});
  await task('B').waitFor({state:'detached'});
  assert.equal(await task('C').getAttribute('data-gantt-start'),'5','Dependent task retains its date after explicit detach');
  await undo(chained);
  await clickControl(page, task('B').locator('.gantt-bar'));
  const beforeCut=await source.inputValue();
  await surface.press('Control+x');
  assert.equal(await source.inputValue(),beforeCut,'Caption clipboard cannot remove a complete schedule task');

  // Use distinct identities as the render-completion witness. Reusing B from
  // the preceding fixture can measure its old SVG before the source debounce
  // renders the new DPI, then send the pointer to stale screen coordinates.
  const scaled='@startgantt\nskinparam dpi 192\n[Scaled A] as [SA] lasts 2 days\n[Scaled B] as [SB] starts at [SA]\'s end and lasts 3 days\n@endgantt';
  await fillControl(page, source, scaled);await task('SB').waitFor();await clickControl(page, page.getByRole('button',{name:'Fit diagram',exact:true}));
  bar=task('SB').locator('.gantt-bar');box=await bar.boundingBox();
  day=await task('SB').evaluate(e=>Number(e.dataset.ganttUnit)*e.getScreenCTM().a);
  await page.mouse.move(box.x+box.width/2,box.y+box.height/2);await page.mouse.down();
  await page.mouse.move(box.x+box.width/2+day,box.y+box.height/2,{steps:6});await page.mouse.up();
  await paper.locator('g.task[data-gantt-id="SB"][data-gantt-start="3"]').waitFor();
  await undo(scaled);

  const generated="@startgantt\n[Plan /'comment-like'/ stage] as [A] lasts 2 days\n@endgantt";
  await fillControl(page, source, generated);await task('A').waitFor();await clickControl(page, page.getByRole('button',{name:'Fit diagram',exact:true}));
  await clickControl(page, task('A').locator('.gantt-bar'));
  assert.equal(await page.locator('.gantt-handle:visible').count(),0,'Generated task geometry has no editable resize affordances');
  bar=task('A').locator('.gantt-bar');box=await bar.boundingBox();
  await page.mouse.move(box.x+box.width/2,box.y+box.height/2);await page.mouse.down();
  await page.mouse.move(box.x+box.width/2+50,box.y+box.height/2,{steps:5});await page.mouse.up();
  assert.equal(await source.inputValue(),generated);
  console.log('Verified native Gantt bar move, start/finish resizing, dependency propagation, cancellation, held-key atomic history, exact field selection, inline rename, inspector edits and safe dependent deletion.');
}
