import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';

const path = process.argv[2] || 'src/UnoUML.App/obj/project.assets.json';
const assets = JSON.parse(await readFile(path, 'utf8'));
const packages = Object.keys(assets.libraries).filter(name => /^(Uno\.|Silk\.NET\.WebGPU\/)/.test(name)).sort();
const framework = packages.filter(name => /^(Uno\.WinUI|Uno\.UI)(\/|\.Composition\.|\.Runtime\.)/.test(name));
assert.ok(framework.length, 'The resolved package graph must contain the Uno framework');
assert.ok(framework.every(name => Number(name.split('/').at(-1).split('.')[0]) >= 7),
  'The migrated application must not restore Uno 6 framework packages');
assert.ok(packages.some(name => /WebGpu/i.test(name)), 'The resolved graph must contain the WebGPU renderer');
console.log('Resolved Uno 7 graphics package graph:\n' + packages.join('\n'));
