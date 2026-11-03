import assert from 'node:assert/strict';

// Three deterministic ZIP entries exercise relative source and JSON resolution.
export const archiveBytes = Buffer.from('UEsDBBQAAAAIAAAAIVwV0gDKYwAAAHQAAAAQAAAAbW9kZWxzL21haW4uaXVtbFNUSc7PS8tMt1XNyU9Mic8qzs/TUMpNLUnUAzGVNLmKUpNLEvPSc1IVlKBK9ZITC0oygZIKicUKjkXJGZllqSlcipl5yTmlKakKxZlJOZl56XqZpbk5XDBpBV1dO4VgiAwXAFBLAwQUAAAACAAAACFc7puB7BAAAAAOAAAAEwAAAG1vZGVscy9zaWJsaW5nLml1bWxLzkksLlYIzkzKycxL5wIAUEsDBBQAAAAIAAAAIVztDsioIAAAAB4AAAAQAAAAbW9kZWxzL21ldGEuanNvbqtWSk4sKMnMz1OyUnIsSs7ILEtNUShOLSrLTE5VqgUAUEsBAhQDFAAAAAgAAAAhXBXSAMpjAAAAdAAAABAAAAAAAAAAAAAAAIABAAAAAG1vZGVscy9tYWluLml1bWxQSwECFAMUAAAACAAAACFc7puB7BAAAAAOAAAAEwAAAAAAAAAAAAAAgAGRAAAAbW9kZWxzL3NpYmxpbmcuaXVtbFBLAQIUAxQAAAAIAAAAIVztDsioIAAAAB4AAAAQAAAAAAAAAAAAAACAAdIAAABtb2RlbHMvbWV0YS5qc29uUEsFBgAAAAADAAMAvQAAACABAAAAAA==','base64');

export async function verifyArchiveFiles(page) {
  const chooser = page.waitForEvent('filechooser');
  const importing = page.evaluate(() => ui.importIncludes(false));
  const pixel = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAC0lEQVR4nGP4DwQACfsD/fteaysAAAAASUVORK5CYII=','base64');
  await (await chooser).setFiles([
    {name:'resources.zip',mimeType:'application/zip',buffer:archiveBytes},
    {name:'pixel.png',mimeType:'image/png',buffer:pixel}
  ]);
  assert.equal(await importing,2);
  assert.equal(await page.evaluate(() => ui.readArchive('resources.zip')),archiveBytes.toString('base64'));
  assert.equal(await page.evaluate(() => ui.readInclude('pixel.png')),'data:image/png;base64,'+pixel.toString('base64'));
  const before = await page.evaluate(() => ui.readArchiveVersion('resources.zip'));
  await page.evaluate(data => ui.saveArchive('resources.zip',data),archiveBytes.toString('base64'));
  assert.ok(await page.evaluate(() => ui.readArchiveVersion('resources.zip')) > before);
  assert.deepEqual(await page.evaluate(async () => JSON.parse(await ui.listIncludes())),['common.iuml']);

  const peer = await page.context().newPage();
  try {
    await peer.goto(page.url());
    await peer.evaluate(async () => { window.files = await import('/uml/files.js'); await files.includeFiles(); });
    assert.deepEqual(await page.evaluate(async () => JSON.parse(await ui.updateInclude('common.iuml','class UpdatedFromFile','class OtherTab'))),{updated:true,persisted:true});
    assert.deepEqual(await peer.evaluate(async () => JSON.parse(await files.updateInclude('common.iuml','class UpdatedFromFile','class StaleTab'))),{updated:false,persisted:false});
    assert.equal(await peer.evaluate(() => files.readInclude('common.iuml')),'class OtherTab');
    await page.evaluate(() => ui.updateInclude('common.iuml','class OtherTab','class UpdatedFromFile'));
  } finally { await peer.close(); }

  const context = await page.context().browser().newContext();
  try {
    const migration = await context.newPage();
    await migration.goto(page.url());
    const value = await migration.evaluate(async data => {
      await new Promise((resolve,reject) => {
        const request = indexedDB.open('UnoUML-Includes-v1',1);
        request.onupgradeneeded = () => request.result.createObjectStore('includes');
        request.onsuccess = () => {
          const db = request.result, tx = db.transaction('includes','readwrite');
          tx.objectStore('includes').put('class Legacy','legacy.iuml');
          tx.oncomplete = () => { db.close(); resolve(); };
          tx.onerror = () => reject(tx.error);
        };
        request.onerror = () => reject(request.error);
      });
      const files = await import('/uml/files.js');
      await files.includeFiles();
      if (!JSON.parse(await files.saveArchive('resources.zip',data)).persisted) throw Error('Archive persistence failed');
      return files.readInclude('legacy.iuml');
    },archiveBytes.toString('base64'));
    assert.equal(value,'class Legacy');
    await migration.reload();
    const restored = await migration.evaluate(async () => {
      const files = await import('/uml/files.js');
      await files.includeFiles();
      return [files.readInclude('legacy.iuml'),files.readArchive('resources.zip'),files.readArchiveVersion('resources.zip')];
    });
    assert.deepEqual(restored.slice(0,2),['class Legacy',archiveBytes.toString('base64')]);
    assert.ok(restored[2] > 0);
  } finally { await context.close(); }
}

export async function verifyArchiveImport(page, source) {
  const actions = page.getByText('Library',{exact:true}).locator('..').locator('..');
  await actions.locator('[xamltype="Microsoft.UI.Xaml.Controls.Button"]').click();
  const chooser = page.waitForEvent('filechooser');
  await page.getByText('Load ZIP archive…',{exact:true}).click();
  await (await chooser).setFiles({name:'resources.zip',mimeType:'application/zip',buffer:archiveBytes});
  await page.waitForFunction(async () => (await import(new URL('uml/viewport.js',document.baseURI))).readArchiveVersion('resources.zip') > 0);
  await source.fill('@startuml\n!import resources.zip\n!include models/main.iuml\n@enduml');
  const paper = page.locator('.paper').first();
  await paper.locator('[data-qualified-name="Archived"] text').filter({hasText:/^Archived service$/}).waitFor();
  await paper.locator('[data-qualified-name="Sibling"]').waitFor();
  assert.equal(await paper.getAttribute('data-renderer'),'managed');
  assert.equal(await paper.locator('[data-qualified-name="Archived"]').getAttribute('data-source-file'),'models/main.iuml');
  await page.waitForFunction(() => JSON.parse(localStorage.getItem('UnoUML:unouml.document.v1')).Source.includes('!import resources.zip'));
  await page.reload({waitUntil:'domcontentloaded'});
  await paper.locator('[data-qualified-name="Archived"] text').filter({hasText:/^Archived service$/}).waitFor({timeout:120000});
  assert.equal(await paper.getAttribute('data-renderer'),'managed');
  assert.ok(await page.evaluate(async () => (await import(new URL('uml/viewport.js',document.baseURI))).readArchiveVersion('resources.zip')) > 0);
  console.log('Verified ZIP archive picker, native relative includes/JSON resources, source provenance and IndexedDB restoration.');
}
