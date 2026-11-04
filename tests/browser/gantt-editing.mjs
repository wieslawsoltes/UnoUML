import assert from 'node:assert/strict';

export async function verifyGanttEditing(page,source) {
  await page.setViewportSize({width:1480,height:960});
  await page.getByRole('button',{name:'Split',exact:true}).click();
  await source.waitFor({state:'visible'});
  const paper=page.locator('.paper').first(),surface=page.locator('.surface').first();
  const fixture='@startgantt\nProject starts 2026-01-05\n[Design] as [D] lasts 3 days \' keep plan\n[Build] starts at [D]\'s end and lasts 5 days\n[Ship] happens at [Build]\'s end\n@endgantt';
  const task=id=>paper.locator(`g.task[data-gantt-id="${id}"]`);
  const undo=async expected=>{
    await page.getByRole('button',{name:'Undo',exact:true}).click();
    await page.waitForFunction(value=>[...document.querySelectorAll('textarea')].some(e=>e.getAttribute('aria-label')==='PlantUML source'&&e.value===value),expected);
    assert.equal(await source.inputValue(),expected);
  };
  await source.fill(fixture);
  await task('Build').waitFor();
  assert.equal(await paper.getAttribute('data-renderer'),'managed');
  await page.getByRole('button',{name:'Fit diagram',exact:true}).click();

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
  await page.getByRole('button',{name:'Fit diagram',exact:true}).click();
  await task('Build').locator('.gantt-bar').click();
  let handle=page.locator('.gantt-handle[data-gantt-handle="end"]');
  await handle.waitFor({state:'visible'});box=await handle.boundingBox();
  day=await task('Build').evaluate(e=>Number(e.dataset.ganttUnit)*e.ownerSVGElement.getScreenCTM().a);
  await page.mouse.move(box.x+box.width/2,box.y+box.height/2);await page.mouse.down();
  await page.mouse.move(box.x+box.width/2+2*day,box.y+box.height/2,{steps:8});await page.mouse.up();
  await paper.locator('g.task[data-gantt-id="Build"][data-gantt-duration="7"][data-gantt-start="3"]').waitFor();
  assert.ok((await source.inputValue()).includes("starts at [D]'s end"),'Finish resize retains the predecessor constraint');
  await undo(fixture);

  await page.getByRole('button',{name:'Fit diagram',exact:true}).click();
  await task('Build').locator('.gantt-bar').click();
  handle=page.locator('.gantt-handle[data-gantt-handle="start"]');box=await handle.boundingBox();
  day=await task('Build').evaluate(e=>Number(e.dataset.ganttUnit)*e.ownerSVGElement.getScreenCTM().a);
  await page.mouse.move(box.x+box.width/2,box.y+box.height/2);await page.mouse.down();
  await page.mouse.move(box.x+box.width/2+day,box.y+box.height/2,{steps:8});await page.mouse.up();
  await paper.locator('g.task[data-gantt-id="Build"][data-gantt-start="4"][data-gantt-duration="4"]').waitFor();
  await undo(fixture);

  // An interrupted gesture restores the current render without touching source.
  await task('Build').locator('.gantt-bar').click();bar=task('Build').locator('.gantt-bar');box=await bar.boundingBox();
  await page.mouse.move(box.x+box.width/2,box.y+box.height/2);await page.mouse.down();
  await page.mouse.move(box.x+box.width/2+day,box.y+box.height/2,{steps:4});await page.keyboard.press('Escape');await page.mouse.up();
  assert.equal(await source.inputValue(),fixture);

  await task('Build').locator('.gantt-bar').click();
  await surface.focus();await page.keyboard.down('ArrowRight');await page.keyboard.down('ArrowRight');await page.keyboard.up('ArrowRight');
  await paper.locator('g.task[data-gantt-id="Build"][data-gantt-start="5"]').waitFor();
  const nudged=await source.inputValue();
  await surface.press('ArrowRight');
  await paper.locator('g.task[data-gantt-id="Build"][data-gantt-start="6"]').waitFor();
  await undo(nudged);
  await undo(fixture);

  // F2 edits the actual task token and preserves an explicit alias.
  await task('D').locator('[data-edit-kind="gantt-label"]').click();await surface.press('F2');
  const labelEditor=page.getByRole('textbox',{name:'Edit diagram label',exact:true});
  await labelEditor.waitFor({state:'visible'});await labelEditor.fill('Concept');await labelEditor.press('Enter');
  await task('D').locator('text').filter({hasText:/^Concept$/}).waitFor();
  assert.equal(await source.inputValue(),fixture.replace('[Design]','[Concept]'));
  await undo(fixture);

  await task('Build').locator('[data-edit-kind="gantt-label"]').click();
  await page.getByText('Edit task schedule…',{exact:true}).last().click();
  let dialog=page.getByRole('dialog',{name:'Edit task schedule',exact:true});
  await dialog.getByRole('textbox',{name:'Duration in days',exact:true}).fill('8');
  await dialog.getByRole('textbox',{name:'Completion percent (optional)',exact:true}).fill('40');
  await dialog.getByRole('textbox',{name:'Task color (#RRGGBB)',exact:true}).fill('#224466');
  await dialog.getByRole('button',{name:'Apply',exact:true}).click();await dialog.waitFor({state:'hidden'});
  await paper.locator('g.task[data-gantt-id="Build"][data-gantt-duration="8"]').waitFor();
  assert.ok((await source.inputValue()).includes('is 40% completed and is colored in #224466'));
  await undo(fixture);

  // Every bar in a shared `then` statement must select its own task, including
  // the inspector and Delete path; source-line selection alone is ambiguous.
  const chained='@startgantt\n[A] lasts 2 days then [B] lasts 3 days then [C] lasts 1 day\n@endgantt';
  await source.fill(chained);await task('B').waitFor();await page.getByRole('button',{name:'Fit diagram',exact:true}).click();
  await task('B').locator('.gantt-bar').click();
  await page.getByText('Rename schedule task…',{exact:true}).last().click();
  dialog=page.getByRole('dialog',{name:'Rename schedule task',exact:true});
  assert.equal(await dialog.getByRole('textbox',{name:'Task name',exact:true}).inputValue(),'B');
  await dialog.getByRole('textbox',{name:'Task name',exact:true}).fill('Middle');
  await dialog.getByRole('button',{name:'Rename',exact:true}).click();await dialog.waitFor({state:'hidden'});
  await task('Middle').waitFor();assert.equal(await source.inputValue(),chained.replace('[B]','[Middle]'));
  await undo(chained);

  await task('B').locator('.gantt-bar').click();await surface.press('Delete');
  dialog=page.getByRole('dialog',{name:'Remove schedule task',exact:true});
  await dialog.getByRole('button',{name:'Remove',exact:true}).click();await dialog.waitFor({state:'hidden'});
  await task('B').waitFor({state:'detached'});
  assert.equal(await task('C').getAttribute('data-gantt-start'),'5','Dependent task retains its date after explicit detach');
  await undo(chained);
  await task('B').locator('.gantt-bar').click();
  const beforeCut=await source.inputValue();
  await surface.press('Control+x');
  assert.equal(await source.inputValue(),beforeCut,'Caption clipboard cannot remove a complete schedule task');

  // Use distinct identities as the render-completion witness. Reusing B from
  // the preceding fixture can measure its old SVG before the source debounce
  // renders the new DPI, then send the pointer to stale screen coordinates.
  const scaled='@startgantt\nskinparam dpi 192\n[Scaled A] as [SA] lasts 2 days\n[Scaled B] as [SB] starts at [SA]\'s end and lasts 3 days\n@endgantt';
  await source.fill(scaled);await task('SB').waitFor();await page.getByRole('button',{name:'Fit diagram',exact:true}).click();
  bar=task('SB').locator('.gantt-bar');box=await bar.boundingBox();
  day=await task('SB').evaluate(e=>Number(e.dataset.ganttUnit)*e.getScreenCTM().a);
  await page.mouse.move(box.x+box.width/2,box.y+box.height/2);await page.mouse.down();
  await page.mouse.move(box.x+box.width/2+day,box.y+box.height/2,{steps:6});await page.mouse.up();
  await paper.locator('g.task[data-gantt-id="SB"][data-gantt-start="3"]').waitFor();
  await undo(scaled);

  const generated="@startgantt\n[Plan /'comment-like'/ stage] as [A] lasts 2 days\n@endgantt";
  await source.fill(generated);await task('A').waitFor();await page.getByRole('button',{name:'Fit diagram',exact:true}).click();
  await task('A').locator('.gantt-bar').click();
  assert.equal(await page.locator('.gantt-handle:visible').count(),0,'Generated task geometry has no editable resize affordances');
  bar=task('A').locator('.gantt-bar');box=await bar.boundingBox();
  await page.mouse.move(box.x+box.width/2,box.y+box.height/2);await page.mouse.down();
  await page.mouse.move(box.x+box.width/2+50,box.y+box.height/2,{steps:5});await page.mouse.up();
  assert.equal(await source.inputValue(),generated);
  console.log('Verified native Gantt bar move, start/finish resizing, dependency propagation, cancellation, held-key atomic history, exact field selection, inline rename, inspector edits and safe dependent deletion.');
}
