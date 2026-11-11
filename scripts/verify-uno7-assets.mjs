import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';

const path = process.argv[2] || 'src/UnoUML.App/obj/project.assets.json';
const assets = JSON.parse(await readFile(path, 'utf8'));
const packages = Object.keys(assets.libraries).filter(name => /^(Uno\.|Silk\.NET\.WebGPU\/)/.test(name)).sort();
const expected = [
  'Uno.WinUI',
  'Uno.WinUI.Runtime.WebAssembly.Browser',
  'Uno.WinUI.Composition.WebGpu',
  'Uno.WinUI.Composition.Skia',
  'Uno.WinUI.Graphics3DWebGpu'
];
for (const id of expected) {
  const name = packages.find(name => name.startsWith(id + '/'));
  assert.ok(name, 'Missing required Uno 7 package: ' + id);
  assert.equal(Number(name.split('/').at(-1).split('.')[0]), 7,
    'The migrated application must resolve Uno 7: ' + id);
}
console.log('Resolved Uno 7 graphics package graph:\n' + packages.join('\n'));
