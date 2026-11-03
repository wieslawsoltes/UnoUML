import assert from 'node:assert/strict';
import { mkdtemp, mkdir, readFile, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import test from 'node:test';
import { stagePages } from '../scripts/stage-pages.mjs';

test('Pages keeps the document URL stable and versions the complete application graph', async () => {
  const temporary = await mkdtemp(join(tmpdir(), 'unouml-publish-'));
  try {
    const source = join(temporary, 'source');
    await mkdir(source);
    const assets = {
      'index.html': '<!doctype html><html><head><meta charset="utf-8"><script type="module" src="./package_fixed/uno-bootstrap.js"></script></head><body></body></html>',
      'package_fixed/uno-bootstrap.js': 'import "./uno-config.js";',
      'package_fixed/uno-config.js': 'export const runtime = "dotnet.a.js";',
      '_framework/UnoUML.App.a.wasm': 'assembly-v1',
      'uml/viewport.js': 'import "./accessibility.js";',
      'uml/accessibility.js': 'export const version = 1;',
      'uml/preview.css': 'body { color: black; }',
      'packages/UnoUML.PlantUml.0.5.0.nupkg': 'download'
    };
    for (const [path, contents] of Object.entries(assets)) {
      await mkdir(dirname(join(source, path)), { recursive: true });
      await writeFile(join(source, path), contents);
    }
    const first = await stagePages(source, join(temporary, 'first'));
    const duplicate = await stagePages(source, join(temporary, 'duplicate'));
    assert.equal(duplicate.build, first.build, 'Identical content has identical asset URLs');
    const entry = await readFile(join(temporary, 'first/index.html'), 'utf8');
    assert.ok(entry.indexOf('<base ') < entry.indexOf('<script '));
    assert.match(entry, new RegExp(`name="unouml-build" content="${first.build}"`));
    for (const [path, contents] of Object.entries(assets)) {
      const address = new URL(path, new URL(first.base, 'https://example.org/UnoUML/'));
      assert.equal(address.pathname, `/UnoUML/build/${first.build}/${path}`);
      assert.equal(await readFile(join(temporary, 'first', first.base, path), 'utf8'), contents);
    }
    assert.equal(await readFile(join(temporary, 'first/packages/UnoUML.PlantUml.0.5.0.nupkg'), 'utf8'), 'download');
    assert.equal(await readFile(join(source, 'index.html'), 'utf8'), assets['index.html']);

    // The Uno bootstrap directory does not change in either regression.
    await writeFile(join(source, '_framework/UnoUML.App.a.wasm'), 'assembly-v2');
    const assembly = await stagePages(source, join(temporary, 'assembly'));
    assert.notEqual(assembly.build, first.build, 'A compiled-only change invalidates config and modules together');
    await writeFile(join(source, 'uml/accessibility.js'), 'export const version = 2;');
    const module = await stagePages(source, join(temporary, 'module'));
    assert.notEqual(module.build, assembly.build, 'A transitive ES-module change invalidates the full graph');

    await assert.rejects(stagePages(source, join(source, 'nested')), /outside/);
    await assert.rejects(stagePages(source, join(temporary, 'first')), { code: 'EEXIST' });
  } finally { await rm(temporary, { recursive: true, force: true }); }
});
