// Copy the npm-distributed renderer and materialize upstream's on-demand libraries.
// Packages are deploy-time assets. A diagram only requests the libraries it uses.
import { createHash } from 'node:crypto';
import { mkdir, readFile, copyFile, rename, writeFile } from 'node:fs/promises';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
const exec = promisify(execFile);
const core = new URL('../node_modules/@plantuml/core/', import.meta.url);
const output = new URL('../src/UnoUML.App/wwwroot/uml/vendor/', import.meta.url);
await mkdir(output, { recursive: true });
for (const name of ['plantuml.js', 'viz-global.js', 'themes.js', 'openiconic.js', 'emoji.js', 'LICENSE']) {
  await copyFile(new URL(name, core), new URL(name, output));
}

const { revision, blobs } = JSON.parse(await readFile(new URL('stdlib-lock.json', import.meta.url)));
const queue = Object.entries(blobs);
function gitBlobHash(bytes) {
  return createHash('sha1').update('blob ' + bytes.length + '\0').update(bytes).digest('hex');
}
async function copyLibraries() {
  for (let item; (item = queue.shift());) {
    const [name, expected] = item;
    const dest = new URL(name, output);
    const cached = await readFile(dest).catch(() => null);
    if (cached && gitBlobHash(cached) === expected) continue;
    const url = 'https://raw.githubusercontent.com/plantuml/plantuml/' + revision + '/js-plantuml/' + name;
    const { stdout } = await exec('curl', ['--fail', '--silent', '--show-error', '--location', '--retry', '3', url], { encoding: 'buffer', maxBuffer: 64 * 1024 * 1024 });
    if (gitBlobHash(stdout) !== expected) throw new Error('Unexpected contents for ' + name);
    const tmp = new URL(name + '.part', output);
    await writeFile(tmp, stdout);
    await rename(tmp, dest);
  }
}
await Promise.all(Array.from({ length: 4 }, copyLibraries));
console.log('Prepared PlantUML, Viz.js, and ' + Object.keys(blobs).length + ' verified support bundles.');
