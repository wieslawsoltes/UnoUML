import { readFile, writeFile, mkdir, rm } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import { gzipSync } from 'node:zlib';

// Read the data-only literals in the verified upstream bundles. No upstream
// JavaScript is evaluated by the build, including metadata and string escapes.
function literal(source, start) {
  let at = start;
  const space = () => { while (/\s/.test(source[at] ?? '') && at < source.length) at++; };
  function read() {
    space();
    const first = source[at++];
    if (first === '!' && (source[at] === '0' || source[at] === '1')) return source[at++] === '0';
    if (first === '"' || first === "'") {
      let result = '';
      while (at < source.length) {
        let c = source[at++];
        if (c === first) {
          if (!source.startsWith('.split(', at)) return result;
          at += 7;
          const separator = read();
          space();
          if (typeof separator !== 'string' || source[at++] !== ')') throw new Error('Invalid compact resource array');
          return result.split(separator);
        }
        if (c !== '\\') { result += c; continue; }
        c = source[at++];
        if (c === '\n') continue;
        if (c === '\r') { if (source[at] === '\n') at++; continue; }
        if (c === 'x' || c === 'u') {
          const length = c === 'x' ? 2 : 4;
          const digits = source.slice(at, at + length);
          if (!new RegExp('^[0-9a-f]{' + length + '}$', 'i').test(digits)) throw new Error('Invalid resource escape');
          result += String.fromCharCode(parseInt(digits, 16)); at += length;
        } else result += ({ n: '\n', r: '\r', t: '\t', b: '\b', f: '\f', v: '\v', 0: '\0' })[c] ?? c;
      }
      throw new Error('Unterminated resource string');
    }
    if (first === '[' || first === '{') {
      const array = first === '[';
      const result = array ? [] : Object.create(null);
      const close = array ? ']' : '}';
      space();
      while (source[at] !== close) {
        if (array) result.push(read());
        else {
          space();
          let key;
          if (source[at] === '"' || source[at] === "'") key = read();
          else {
            const match = /^[\w$]+/.exec(source.slice(at));
            if (!match) throw new Error('Invalid resource property');
            key = match[0]; at += key.length;
          }
          space();
          if (source[at++] !== ':') throw new Error('Invalid resource property separator');
          result[key] = read();
        }
        space();
        if (source[at] !== ',') break;
        at++; space();
      }
      if (source[at++] !== close) throw new Error('Unterminated resource container at ' + (at - 1));
      return result;
    }
    at--;
    const match = /^(?:true|false|null|-?(?:0|[1-9]\d*)(?:\.\d+)?(?:e[+-]?\d+)?)/i.exec(source.slice(at));
    if (!match) throw new Error('Resource assignment contains executable code at ' + at);
    at += match[0].length;
    return JSON.parse(match[0]);
  }
  return { value: read(), end: at };
}

const maximumShardBytes = 128 * 1024;

async function writeLibrary(directory, name, resources, alias) {
  if (!/^[a-z0-9][a-z0-9.-]*$/.test(name)) throw new Error('Invalid standard library name');
  const folder = new URL(name + '/', directory);
  await rm(folder, { recursive: true, force: true });
  await mkdir(folder, { recursive: true });
  const chunks = Object.create(null);
  let entries = [], bytes = 2, count = 0;
  const writes = [];
  function flush() {
    if (!entries.length) return;
    const contents = JSON.stringify(Object.fromEntries(entries));
    const hash = createHash('sha256').update(contents).digest('hex').slice(0, 24);
    for (const [key] of entries) chunks[key.slice(name.length + 1)] = hash;
    writes.push(writeFile(new URL(hash + '.json', folder), contents));
    count++;
    entries = []; bytes = 2;
  }
  for (const key of Object.keys(resources).sort()) {
    if (!key.startsWith(name + '/')) throw new Error('Resource assigned to another standard library');
    const value = resources[key];
    const size = Buffer.byteLength(JSON.stringify(key) + ':' + JSON.stringify(value));
    if (entries.length && bytes + size + 1 > maximumShardBytes) flush();
    entries.push([key, value]);
    bytes += size + 1;
  }
  flush();
  await Promise.all(writes);
  const index = { $format: 1, $chunks: chunks };
  if (alias) index.$link = alias;
  await writeFile(new URL(name + '.json', directory), JSON.stringify(index));
  return count;
}

async function readArtwork(output, property) {
  const artwork = { openiconic: Object.create(null), emoji: Object.create(null), shortcuts: Object.create(null) };
  const names = { OPENICONIC: 'openiconic', EMOJI: 'emoji', EMOJI_SHORTCUT: 'shortcuts' };
  const assignments = new RegExp('window\\.PLANTUML_(OPENICONIC|EMOJI_SHORTCUT|EMOJI)' + property + '\\s*=', 'g');
  for (const filename of ['openiconic.js', 'emoji.js']) {
    const contents = await readFile(new URL(filename, output), 'utf8');
    for (const match of contents.matchAll(assignments)) {
      const { value } = literal(contents, match.index + match[0].length);
      if (typeof value !== 'string') throw new Error('Invalid upstream artwork entry');
      artwork[names[match[1]]][match[2] ?? match[3]] = value;
    }
  }
  if (Object.keys(artwork.openiconic).length < 200 || Object.keys(artwork.emoji).length < 1000 || Object.keys(artwork.shortcuts).length < 1000)
    throw new Error('The upstream artwork bundle format changed.');
  return artwork;
}

export async function materializeResources(output, files, embeddedOutput) {
  const directory = new URL('resources/', output);
  await mkdir(directory, { recursive: true });
  const metadata = Object.create(null);
  const property = '(?:\\.([\\w$]+)|\\["([^"\\\\]+)"\\])';
  const assignments = new RegExp('window\\.PLANTUML_(STDLIB|STDLIB_JSON)' + property + property + '=', 'g');
  const information = new RegExp('window\\.PLANTUML_STDLIB_INFO' + property + '=\\{', 'g');
  let count = 0, shards = 0;
  for (const filename of files.filter(name => name.endsWith('.min.js'))) {
    const contents = await readFile(new URL(filename, output), 'utf8');
    const resources = Object.create(null);
    for (const match of contents.matchAll(assignments)) {
      const name = (match[2] ?? match[3]) + '/' + (match[4] ?? match[5]);
      const { value } = literal(contents, match.index + match[0].length);
      resources[name] = match[1] === 'STDLIB_JSON' ? (typeof value === 'string' ? value : JSON.stringify(value)) :
        Array.isArray(value) ? value.join('\n') : value.replaceAll(';', '\n');
    }
    for (const match of contents.matchAll(information))
      metadata[match[1] ?? match[2]] = literal(contents, match.index + match[0].length - 1).value;
    const name = filename.replace('.min.js', '');
    count += Object.keys(resources).length;
    shards += await writeLibrary(directory, name, resources, metadata[name]?.link);
  }
  const themes = Object.create(null);
  const contents = await readFile(new URL('themes.js', output), 'utf8');
  for (const match of contents.matchAll(/g\.PLANTUML_THEMES\["([^"\\]+)"\]=/g))
    themes[match[1]] = literal(contents, match.index + match[0].length).value;
  if (!themes.plain || !themes.spacelab || !metadata.c4 || count < 1000)
    throw new Error('The upstream resource bundle format changed.');
  for (const name of Object.keys(metadata)) {
    const visited = new Set();
    for (let link = name; link; link = metadata[link].link) {
      if (!metadata[link] || visited.has(link)) throw new Error('Invalid standard library alias ' + name);
      visited.add(link);
    }
  }
  await writeFile(new URL('manifest.json', directory), JSON.stringify({ themes: Object.keys(themes).sort(), libraries: metadata }));
  if (embeddedOutput) {
    await mkdir(embeddedOutput, { recursive: true });
    await writeFile(new URL('catalog.json.gz', embeddedOutput), gzipSync(JSON.stringify({ themes, libraries: metadata }), { level: 9 }));
    await writeFile(new URL('artwork.json.gz', embeddedOutput), gzipSync(JSON.stringify(await readArtwork(output, property)), { level: 9 }));
    await writeFile(new URL('PlantUML.LICENSE', embeddedOutput), await readFile(new URL('LICENSE', output)));
  }
  return { resources: count, themes: Object.keys(themes).length, libraries: Object.keys(metadata).length, shards };
}
