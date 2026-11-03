import { readInclude } from './files.js';

const resources = new Map(), indexes = new Map(), pending = new Set(), loading = new Map();
const loadedChunks = new Set(), missing = new Set();
const packages = /^[a-z0-9][a-z0-9.-]*$/;
const shardNames = /^[a-f0-9]{24}$/;
const withoutExtension = key => key.replace(/\.(?:puml|iuml)$/, '');
const cached = key => resources.get(key) ?? resources.get(withoutExtension(key));
const shardFor = (index, key, library) => index.chunks.get(key.slice(library.length + 1)) ??
  index.chunks.get(withoutExtension(key).slice(library.length + 1));

export function beginResourcePass() { pending.clear(); }

// Managed TIM reads synchronously. The viewport fetches the requested source
// chunks between attempts, while completed resources remain in the shared cache.
export function readResource(path) {
  const imported = readInclude(path);
  if (imported !== null) return imported;
  if (!path.startsWith('<') || !path.endsWith('>') || path.startsWith('<theme/')) return null;
  const requested = path.slice(1, -1).toLowerCase();
  if (missing.has(requested)) return null;
  let key = requested;
  const visited = new Set();
  for (let hop = 0; hop < 32; hop++) {
    const library = key.split('/')[0];
    if (!packages.test(library) || visited.has(library)) break;
    visited.add(library);
    const value = cached(key);
    if (value !== undefined) return value;
    const index = indexes.get(library);
    if (!index || shardFor(index, key, library)) { pending.add(requested); return null; }
    if (!index.alias) break;
    key = index.alias + key.slice(library.length);
  }
  missing.add(requested);
  return null;
}

async function readJson(path) {
  const response = await fetch(new URL('vendor/resources/' + path, import.meta.url), { signal: AbortSignal.timeout(30000) });
  if (!response.ok) throw new Error('Library resource is unavailable');
  const entries = await response.json();
  if (!entries || typeof entries !== 'object' || Array.isArray(entries)) throw new Error('Invalid library resource');
  return entries;
}

function loadOnce(key, load) {
  if (loading.has(key)) return loading.get(key);
  const job = load().catch(() => false).finally(() => loading.delete(key));
  loading.set(key, job);
  return job;
}

async function loadIndex(name) {
  if (indexes.has(name)) return true;
  return loadOnce(name + '.json', async () => {
    const entries = await readJson(name + '.json');
    const alias = entries.$link;
    if (alias !== undefined && (typeof alias !== 'string' || !packages.test(alias) || alias === name))
      throw new Error('Invalid library alias');
    const chunks = new Map(), sources = [];
    if (entries.$format === 1) {
      if (!entries.$chunks || typeof entries.$chunks !== 'object' || Array.isArray(entries.$chunks))
        throw new Error('Invalid library index');
      for (const [key, hash] of Object.entries(entries.$chunks)) {
        if (!key || key.includes('\\') || key.split('/').some(part => !part || part === '.' || part === '..') ||
            typeof hash !== 'string' || !shardNames.test(hash)) throw new Error('Invalid library shard');
        chunks.set(key, hash);
      }
    } else {
      // Existing published bundles remain readable during rolling deployments.
      for (const [key, value] of Object.entries(entries)) {
        if (key === '$link') continue;
        if (!key.startsWith(name + '/') || typeof value !== 'string') throw new Error('Invalid library source');
        sources.push([key, value]);
      }
    }
    // Publish the complete validated response atomically. A failed request can
    // be retried by the next render without leaving a partial resource cache.
    for (const [key, value] of sources) resources.set(key, value);
    indexes.set(name, { chunks, alias });
    return true;
  });
}

async function loadShard(name, hash, index) {
  const path = name + '/' + hash + '.json';
  if (loadedChunks.has(path)) return true;
  return loadOnce(path, async () => {
    const entries = Object.entries(await readJson(path));
    for (const [key, value] of entries)
      if (!key.startsWith(name + '/') || typeof value !== 'string' || index.chunks.get(key.slice(name.length + 1)) !== hash)
        throw new Error('Invalid library shard source');
    for (const [key, value] of entries) resources.set(key, value);
    loadedChunks.add(path);
    return true;
  });
}

async function ensureResource(requested) {
  let key = requested;
  const visited = new Set();
  for (let hop = 0; hop < 32; hop++) {
    const library = key.split('/')[0];
    if (!packages.test(library) || visited.has(library)) break;
    visited.add(library);
    if (cached(key) !== undefined) return true;
    if (!await loadIndex(library)) return false;
    const index = indexes.get(library);
    const hash = shardFor(index, key, library);
    if (hash && !await loadShard(library, hash, index)) return false;
    if (cached(key) !== undefined) return true;
    if (!index.alias) break;
    key = index.alias + key.slice(library.length);
  }
  missing.add(requested);
  return false;
}

export async function prepareResources() {
  const requested = [...pending];
  pending.clear();
  const results = await Promise.all(requested.map(ensureResource));
  return results.some(Boolean);
}
