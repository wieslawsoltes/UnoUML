import assert from 'node:assert/strict';

export async function verifyImportedPageRouting(page, source) {
  const original = '@startuml(id=first)\r\nclass "First page" as Shared\r\n@enduml\r\n@startuml(id=second)\r\nclass "Second page" as Shared\r\n@enduml\r\n';
  const chooser = page.waitForEvent('filechooser');
  const importing = page.evaluate(async () => (await import(new URL('uml/viewport.js',document.baseURI))).importIncludes(false));
  await (await chooser).setFiles({name:'multiple.puml',mimeType:'text/plain',buffer:Buffer.from(original)});
  assert.equal(await importing,1);
  const paper = page.locator('.paper').first();
  const editor = page.getByRole('textbox',{name:'Edit diagram label'});
  // A cancelled editor belongs to the old diagram. Its pending DOT selection
  // must never consume the next imported-source label commit.
  const cancelledDot = '@startdot\ndigraph { pending [label="Cancelled DOT"]; }\n@enddot';
  await source.fill(cancelledDot);
  await paper.locator('[data-dot-id="node:pending"]').dblclick();
  await editor.fill('Do not save');
  await editor.press('Escape');
  await editor.waitFor({state:'detached'});
  assert.equal(await source.inputValue(),cancelledDot);
  const input = '@startuml\n!include multiple.puml!second\n@enduml';
  await source.fill(input);
  const item = paper.locator('[data-qualified-name="Shared"][data-source-file="multiple.puml"]').first();
  await item.locator('text').filter({hasText:'Second page'}).dblclick();
  assert.equal(await item.getAttribute('data-file-line'),'4');
  assert.equal(await editor.inputValue(),'Second page');
  await editor.fill('Changed second');
  await editor.press('Enter');
  await item.locator('text').filter({hasText:'Changed second'}).waitFor();
  assert.equal(await source.inputValue(),input);
  const read = () => page.evaluate(async () => (await import(new URL('uml/viewport.js',document.baseURI))).readInclude('multiple.puml'));
  assert.equal(await read(),original.replace('"Second page"','"Changed second"'));
  await page.locator('.surface').first().press('Control+z');
  await item.locator('text').filter({hasText:'Second page'}).waitFor();
  assert.equal(await read(),original);
  assert.equal(await paper.getAttribute('data-renderer'),'managed');
  const baseline = '@startuml\nclass PreviousMain\n@enduml';
  await source.fill(baseline);
  await paper.locator('[data-qualified-name="PreviousMain"]').waitFor();
  await source.fill(input);
  await item.locator('text').filter({hasText:'Second page'}).dblclick();
  await editor.fill('Local change');
  await editor.press('Enter');
  await item.locator('text').filter({hasText:'Local change'}).waitFor();
  const replacement = original.replace('"Second page"','"Reimported change"');
  const replacing = page.waitForEvent('filechooser');
  const reimporting = page.evaluate(async () => (await import(new URL('uml/viewport.js',document.baseURI))).importIncludes(false));
  await (await replacing).setFiles({name:'multiple.puml',mimeType:'text/plain',buffer:Buffer.from(replacement)});
  assert.equal(await reimporting,1);
  await page.locator('.surface').first().press('Control+z');
  await paper.locator('[data-qualified-name="PreviousMain"]').waitFor();
  assert.equal(await source.inputValue(),baseline);
  assert.equal(await read(),replacement);
  await page.locator('.surface').first().press('Control+y');
  await item.locator('text').filter({hasText:'Reimported change'}).waitFor();
  assert.equal(await source.inputValue(),input);
  assert.equal(await read(),replacement);

  // Hold real conflicting storage transactions so each update finishes only
  // after a selection change or a modal dialog takes over the interaction.
  const target = paper.locator('[data-qualified-name="ConflictTarget"] text').filter({hasText:/^Local target$/});
  let current = replacement, currentLabel = 'Reimported change';
  for (const scenario of ['selection','modal']) {
    const conflictInput = `@startuml\n' ${scenario} conflict recovery\n!include multiple.puml!second\nclass "Local target" as ConflictTarget\n@enduml`;
    const nextLabel = `Concurrent ${scenario} change`, pendingLabel = `Pending ${scenario} edit`;
    const concurrent = current.replace(`"${currentLabel}"`,`"${nextLabel}"`);
    const pending = current.replace(`"${currentLabel}"`,`"${pendingLabel}"`);
    await source.fill(conflictInput);
    await target.waitFor();
    await item.locator('text').filter({hasText:currentLabel}).dblclick();
    assert.equal(await editor.inputValue(),currentLabel);
    await editor.fill(pendingLabel);
    const sourceElement = await source.elementHandle();
    let dialog;
    try {
      try {
        await holdImportedConflict(page,concurrent);
        await editor.press('Enter');
        await editor.waitFor({state:'detached'});
        await page.waitForFunction(async expected =>
          (await import(new URL('uml/viewport.js',document.baseURI))).readInclude('multiple.puml') === expected,pending);
        assert.equal(await sourceElement.evaluate(element => element.readOnly),true,'The imported write remains pending behind the held transaction');
        if (scenario === 'selection') await target.click();
        else {
          await page.getByText('Edit included source…',{exact:true}).click();
          dialog = page.getByRole('dialog',{name:'Edit imported include',exact:true});
          await dialog.waitFor({state:'visible'});
        }
      } finally {
        await page.evaluate(async () => { await globalThis.releaseImportedConflict?.(); });
      }
      await page.waitForFunction(element => !element.readOnly,sourceElement);
    } finally { await sourceElement.dispose(); }
    // Inspect the DOM as well as the accessible tree: a misplaced editor behind
    // the modal could be hidden from role locators while still stealing focus.
    assert.equal(await page.locator('.canvas-label-editor').count(),0,`A conflict must not reopen the previous editor after a ${scenario} change`);
    if (dialog) {
      await dialog.waitFor({state:'visible'});
      await dialog.getByRole('button',{name:'Cancel',exact:true}).click();
      await dialog.waitFor({state:'hidden'});
      assert.equal(await editor.count(),0,'Closing the modal does not reveal a stale recovered editor');
    }
    await page.getByText('The imported source changed. Its current version was kept, and your label was not applied.',{exact:true}).waitFor();
    assert.equal(await source.inputValue(),conflictInput);
    assert.equal(await read(),concurrent);
    assert.equal(await readStoredInclude(page),concurrent,'The delayed label update preserves the conflicting stored include byte for byte');
    current = concurrent; currentLabel = nextLabel;
  }
  await target.dblclick();
  assert.equal(await editor.inputValue(),'Local target','The current local object remains independently editable');
  await editor.press('Escape');
  console.log('Verified cancelled DOT editor isolation, later-block imported aliases, atomic undo, conflict history recovery and stale imported-edit recovery after selection changes and modal dialogs.');
}

async function holdImportedConflict(page, text) {
  await page.evaluate(async text => {
    const db = await new Promise((resolve,reject) => {
      const opening = indexedDB.open('UnoUML-Includes-v1',2);
      opening.onsuccess = () => resolve(opening.result);
      opening.onerror = () => reject(opening.error);
    });
    const transaction = db.transaction(['includes','archives'],'readwrite');
    const store = transaction.objectStore('includes');
    let held = true;
    const completed = new Promise(resolve => {
      transaction.oncomplete = () => { db.close(); resolve(null); };
      transaction.onabort = () => { db.close(); resolve(transaction.error?.message || 'Held include transaction aborted'); };
    });
    globalThis.releaseImportedConflict = async () => {
      held = false;
      const error = await completed;
      delete globalThis.releaseImportedConflict;
      if (error) throw new Error(error);
    };
    const keepAlive = () => {
      if (held) store.get('multiple.puml').onsuccess = keepAlive;
    };
    await new Promise((resolve,reject) => {
      const writing = store.put(text,'multiple.puml');
      writing.onsuccess = () => { keepAlive(); resolve(); };
      writing.onerror = () => reject(writing.error);
    });
  },text);
}

async function readStoredInclude(page) {
  return page.evaluate(async () => {
    const db = await new Promise((resolve,reject) => {
      const opening = indexedDB.open('UnoUML-Includes-v1',2);
      opening.onsuccess = () => resolve(opening.result);
      opening.onerror = () => reject(opening.error);
    });
    try {
      return await new Promise((resolve,reject) => {
        const transaction = db.transaction('includes');
        const reading = transaction.objectStore('includes').get('multiple.puml');
        transaction.oncomplete = () => resolve(reading.result);
        transaction.onabort = () => reject(transaction.error);
      });
    } finally { db.close(); }
  });
}
