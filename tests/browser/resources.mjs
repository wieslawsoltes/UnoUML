import assert from 'node:assert/strict';
import { fileURLToPath } from 'node:url';
import { resolve } from 'node:path';

export async function verifyResourceLoading() {
  const first = 'a'.repeat(24), second = 'b'.repeat(24), override = 'c'.repeat(24), broken = 'd'.repeat(24);
  const responses = new Map([
    ['demo.json', { $format: 1, $chunks: { 'compute/a': first, 'compute/b': first, 'compute/c': second } }],
    ['demo/' + first + '.json', { 'demo/compute/a': 'class A', 'demo/compute/b': 'class B' }],
    ['demo/' + second + '.json', { 'demo/compute/c': 'class C' }],
    ['friendly.json', { $format: 1, $chunks: { 'compute/a': override }, $link: 'demo' }],
    ['friendly/' + override + '.json', { 'friendly/compute/a': 'class Override' }],
    ['legacy.json', { 'legacy/model': 'class Legacy' }],
    ['cycle-a.json', { $format: 1, $chunks: {}, $link: 'cycle-b' }],
    ['cycle-b.json', { $format: 1, $chunks: {}, $link: 'cycle-a' }],
    ['flaky.json', { 'flaky/model': 'class Recovered' }],
    ['broken.json', { $format: 1, $chunks: { model: broken } }],
    ['broken/' + broken + '.json', { 'broken/model': 'class Valid', 'foreign/model': 'class Invalid' }]
  ]);
  const requests = [];
  const originalFetch = globalThis.fetch;
  let transient = true;
  globalThis.fetch = async url => {
    const path = new URL(url).pathname.split('/vendor/resources/')[1];
    requests.push(path);
    await Promise.resolve();
    if (path === 'flaky.json' && transient) { transient = false; return { ok: false }; }
    return { ok: responses.has(path), json: async () => responses.get(path) };
  };
  try {
    const loader = await import('../../src/UnoUML.App/wwwroot/uml/resources.js?resource-tests=' + Date.now());
    loader.beginResourcePass();
    assert.equal(loader.readResource('<demo/compute/a>'), null);
    assert.equal(loader.readResource('<demo/compute/b>'), null);
    assert.equal(await loader.prepareResources(), true);
    assert.equal(loader.readResource('<DEMO/Compute/A.puml>'), 'class A');
    assert.equal(loader.readResource('<demo/compute/b>'), 'class B');
    assert.deepEqual(requests, ['demo.json', 'demo/' + first + '.json'], 'Only the requested shard is fetched, and concurrent reads share it');
    assert.equal(await loader.prepareResources(), false, 'A cached read does not schedule another render pass');

    loader.beginResourcePass();
    assert.equal(loader.readResource('<friendly/compute/a>'), null);
    assert.equal(await loader.prepareResources(), true);
    assert.equal(loader.readResource('<friendly/compute/a>'), 'class Override', 'Alias-local entries precede target entries');
    assert.equal(loader.readResource('<friendly/compute/b>'), 'class B', 'Aliases reuse already loaded target entries');
    assert.equal(loader.readResource('<friendly/compute/c>'), null);
    assert.equal(await loader.prepareResources(), true);
    assert.equal(loader.readResource('<friendly/compute/c>'), 'class C');
    assert.equal(requests.filter(path => path === 'demo.json').length, 1);
    assert.equal(requests.filter(path => path === 'demo/' + second + '.json').length, 1);

    loader.beginResourcePass();
    assert.equal(loader.readResource('<legacy/model>'), null);
    assert.equal(await loader.prepareResources(), true);
    assert.equal(loader.readResource('<legacy/model>'), 'class Legacy', 'Published full bundles remain readable');

    loader.beginResourcePass();
    assert.equal(loader.readResource('<cycle-a/model>'), null);
    assert.equal(await loader.prepareResources(), false, 'Alias cycles terminate without an outstanding promise');
    assert.equal(loader.readResource('<cycle-a/model>'), null);
    assert.equal(await loader.prepareResources(), false);

    loader.beginResourcePass();
    assert.equal(loader.readResource('<flaky/model>'), null);
    assert.equal(await loader.prepareResources(), false);
    loader.beginResourcePass();
    assert.equal(loader.readResource('<flaky/model>'), null);
    assert.equal(await loader.prepareResources(), true, 'A failed request can be retried by a later render');
    assert.equal(loader.readResource('<flaky/model>'), 'class Recovered');

    loader.beginResourcePass();
    assert.equal(loader.readResource('<broken/model>'), null);
    assert.equal(await loader.prepareResources(), false);
    assert.equal(loader.readResource('<broken/model>'), null, 'A malformed shard publishes no partial source');
    responses.set('broken/' + broken + '.json', { 'broken/model': 'class Valid' });
    assert.equal(await loader.prepareResources(), true);
    assert.equal(loader.readResource('<broken/model>'), 'class Valid');

    const before = requests.length;
    loader.beginResourcePass();
    assert.equal(loader.readResource('<demo/missing>'), null);
    assert.equal(loader.readResource('<../escape>'), null);
    assert.equal(loader.readResource('<theme/plain>'), null);
    assert.equal(await loader.prepareResources(), false);
    assert.equal(requests.length, before, 'Unknown entries and embedded themes require no network request');
  } finally {
    globalThis.fetch = originalFetch;
  }
  console.log('Resource loader checks passed. Covered lazy shards, aliases, shared requests, retries, cache publication and legacy bundles.');
}

if (process.argv[1] && fileURLToPath(import.meta.url) === resolve(process.argv[1]))
  await verifyResourceLoading();
