import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { inflateRawSync } from 'node:zlib';

// Three deterministic ZIP entries exercise relative source and JSON resolution.
export const archiveBytes = Buffer.from('UEsDBBQAAAAIAAAAIVwV0gDKYwAAAHQAAAAQAAAAbW9kZWxzL21haW4uaXVtbFNUSc7PS8tMt1XNyU9Mic8qzs/TUMpNLUnUAzGVNLmKUpNLEvPSc1IVlKBK9ZITC0oygZIKicUKjkXJGZllqSlcipl5yTmlKakKxZlJOZl56XqZpbk5XDBpBV1dO4VgiAwXAFBLAwQUAAAACAAAACFc7puB7BAAAAAOAAAAEwAAAG1vZGVscy9zaWJsaW5nLml1bWxLzkksLlYIzkzKycxL5wIAUEsDBBQAAAAIAAAAIVztDsioIAAAAB4AAAAQAAAAbW9kZWxzL21ldGEuanNvbqtWSk4sKMnMz1OyUnIsSs7ILEtNUShOLSrLTE5VqgUAUEsBAhQDFAAAAAgAAAAhXBXSAMpjAAAAdAAAABAAAAAAAAAAAAAAAIABAAAAAG1vZGVscy9tYWluLml1bWxQSwECFAMUAAAACAAAACFc7puB7BAAAAAOAAAAEwAAAAAAAAAAAAAAgAGRAAAAbW9kZWxzL3NpYmxpbmcuaXVtbFBLAQIUAxQAAAAIAAAAIVztDsioIAAAAB4AAAAQAAAAAAAAAAAAAACAAdIAAABtb2RlbHMvbWV0YS5qc29uUEsFBgAAAAADAAMAvQAAACABAAAAAA==','base64');

function zipEntries(bytes) {
  let end = bytes.length - 22;
  while (end >= Math.max(0,bytes.length - 65557) && bytes.readUInt32LE(end) !== 0x06054b50) end--;
  assert.ok(end >= 0,'ZIP central directory exists');
  const entries = new Map();
  let at = bytes.readUInt32LE(end + 16);
  for (let count = bytes.readUInt16LE(end + 10); count > 0; count--) {
    assert.equal(bytes.readUInt32LE(at),0x02014b50);
    const nameLength = bytes.readUInt16LE(at+28), extra = bytes.readUInt16LE(at+30), comment = bytes.readUInt16LE(at+32);
    const name = bytes.subarray(at+46,at+46+nameLength).toString('utf8');
    const local = bytes.readUInt32LE(at+42), size = bytes.readUInt32LE(at+20);
    const body = local + 30 + bytes.readUInt16LE(local+26) + bytes.readUInt16LE(local+28);
    const compressed = bytes.subarray(body,body+size), method = bytes.readUInt16LE(at+10);
    assert.ok(method === 0 || method === 8);
    entries.set(name,{data:method === 0 ? compressed : inflateRawSync(compressed),compressed});
    at += 46 + nameLength + extra + comment;
  }
  return entries;
}

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
    const replacement = Buffer.concat([archiveBytes,Buffer.from('edited')]);
    replacement.writeUInt16LE(6,archiveBytes.length-2);
    const packed = replacement.toString('base64'), original = archiveBytes.toString('base64');
    const updated = await page.evaluate(async ([expected,data]) => JSON.parse(await ui.updateArchive('resources.zip',expected,data)),[original,packed]);
    assert.ok(updated.updated && updated.persisted && updated.version > before);
    assert.deepEqual(await peer.evaluate(async expected => JSON.parse(await files.updateArchive('resources.zip',expected,expected)),original),{updated:false,persisted:false});
    assert.equal(await peer.evaluate(() => files.readArchive('resources.zip')),packed);
    assert.ok((await page.evaluate(async ([expected,data]) => JSON.parse(await ui.updateArchive('resources.zip',expected,data)),[packed,original])).updated);
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
  const sibling = paper.locator('[data-qualified-name="Sibling"]');
  assert.equal(await sibling.getAttribute('data-source-archive'),'resources.zip');
  await sibling.locator('text').filter({hasText:/^Sibling$/}).dblclick();
  const editor = page.getByRole('textbox',{name:'Edit diagram label'});
  await editor.fill('Archived child');
  await editor.press('Enter');
  await sibling.locator('text').filter({hasText:/^Archived child$/}).waitFor();
  const main = await page.evaluate(() => JSON.parse(localStorage.getItem('UnoUML:unouml.document.v1')).Source);
  assert.equal(main,'@startuml\n!import resources.zip\n!include models/main.iuml\n@enduml');
  await sibling.locator('text').filter({hasText:/^Archived child$/}).click();
  await page.getByText('Edit included source…',{exact:true}).click();
  await page.getByText('Edit imported include',{exact:true}).waitFor();
  const member = page.locator('textarea').last();
  assert.equal(await member.inputValue(),'class "Archived child" as Sibling\n');
  const [download] = await Promise.all([page.waitForEvent('download'),page.getByText('Download ZIP',{exact:true}).click()]);
  assert.equal(download.suggestedFilename(),'resources.zip');
  const exported = zipEntries(await readFile(await download.path())), original = zipEntries(archiveBytes);
  assert.deepEqual([...exported.keys()].sort(),[...original.keys()].sort());
  assert.equal(exported.get('models/sibling.iuml').data.toString(),'class "Archived child" as Sibling\n');
  for (const name of ['models/main.iuml','models/meta.json']) {
    assert.deepEqual(exported.get(name).data,original.get(name).data);
    assert.deepEqual(exported.get(name).compressed,original.get(name).compressed);
  }
  await member.press('Control+z');
  assert.equal(await member.inputValue(),'class Sibling\n');
  await page.getByText('Cancel',{exact:true}).last().click();
  const surface = page.locator('.surface').first();
  await surface.press('Control+z');
  await sibling.locator('text').filter({hasText:/^Sibling$/}).waitFor();
  await surface.press('Control+y');
  await sibling.locator('text').filter({hasText:/^Archived child$/}).waitFor();
  await page.reload({waitUntil:'domcontentloaded'});
  await sibling.locator('text').filter({hasText:/^Archived child$/}).waitFor({timeout:120000});
  assert.equal(await paper.getAttribute('data-renderer'),'managed');
  console.log('Verified ZIP member canvas editing, transactional undo/redo/cancel, original-resource-preserving ZIP export, exact provenance and IndexedDB restoration.');
}

export async function verifyArchiveAliasEditing(page, source) {
  await page.getByText('Split',{exact:true}).last().click();
  const input = '@startuml\n!import resources.zip\n!include <models/sibling>\n@enduml';
  const previous = await page.locator('.paper svg').first().elementHandle();
  await source.fill(input);
  await page.waitForFunction(element => !element.isConnected, previous);
  await previous.dispose();
  const paper = page.locator('.paper').first();
  const item = paper.locator('[data-qualified-name="Sibling"]').first();
  await item.locator('text').filter({hasText:'Archived child'}).dblclick();
  assert.equal(await item.getAttribute('data-source-file'),'models/sibling.iuml');
  assert.equal(await item.getAttribute('data-source-archive'),'resources.zip');
  const editor = page.getByRole('textbox',{name:'Edit diagram label'});
  assert.equal(await editor.inputValue(),'Archived child');
  await editor.fill('Alias child');
  await editor.press('Enter');
  await item.locator('text').filter({hasText:'Alias child'}).waitFor();
  assert.equal(await source.inputValue(),input);
  const packed = await page.evaluate(async () => (await import(new URL('uml/viewport.js',document.baseURI))).readArchive('resources.zip'));
  assert.equal(zipEntries(Buffer.from(packed,'base64')).get('models/sibling.iuml').data.toString(),'class "Alias child" as Sibling\n');
  await page.locator('.surface').first().press('Control+z');
  await item.locator('text').filter({hasText:'Archived child'}).waitFor();
  assert.equal(await paper.getAttribute('data-renderer'),'managed');
  console.log('Verified angle-bracket archive include aliases edit the original physical member with atomic undo.');
}
