import { createHash } from 'node:crypto';
import { createReadStream } from 'node:fs';
import { cp, mkdir, readFile, readdir, realpath, rename, rm, writeFile } from 'node:fs/promises';
import { basename, dirname, join, relative, resolve, sep } from 'node:path';
import { pathToFileURL } from 'node:url';

async function filesUnder(root, folder = '') {
  const files = [];
  for (const entry of await readdir(join(root, folder), { withFileTypes: true })) {
    const path = folder ? `${folder}/${entry.name}` : entry.name;
    if (entry.isDirectory()) files.push(...await filesUnder(root, path));
    else if (entry.isFile()) files.push(path);
    else throw new Error(`Published assets must be regular files: ${path}`);
  }
  return files.sort();
}

async function fingerprint(root, files) {
  const build = createHash('sha256');
  for (const path of files) {
    const file = createHash('sha256');
    for await (const chunk of createReadStream(join(root, path))) file.update(chunk);
    build.update(path).update('\0').update(file.digest()).update('\0');
  }
  return build.digest('hex');
}

// Uno's package name fingerprints static bootstrap inputs, not the compiled
// application or our ES modules. Give the complete published graph one immutable
// URL prefix so a cached config cannot mix assemblies and modules across builds.
export async function stagePages(sourcePath, outputPath) {
  const source = await realpath(resolve(sourcePath));
  const output = resolve(outputPath);
  await mkdir(dirname(output), { recursive: true });
  const physicalOutput = join(await realpath(dirname(output)), basename(output));
  const relativeOutput = relative(source, physicalOutput);
  if (!relativeOutput || relativeOutput !== '..' && !relativeOutput.startsWith(`..${sep}`))
    throw new Error('The staged site must be outside the published source directory');

  const files = await filesUnder(source);
  const index = await readFile(join(source, 'index.html'), 'utf8');
  if (!/<head(?:\s[^>]*)?>/i.test(index) || /<base(?:\s|>)/i.test(index))
    throw new Error('Expected a published index with a head element and no existing base URL');

  // Do not overwrite an existing directory; only remove output created here if
  // staging fails. The original publish tree is never changed.
  await mkdir(output);
  try {
    const staging = join(output, 'assets');
    await cp(source, staging, { recursive: true, force: false, errorOnExist: true });
    const build = await fingerprint(staging, files);
    const base = `./build/${build}/`;
    await mkdir(join(output, 'build'));
    await rename(staging, join(output, 'build', build));
    const entry = index.replace(/<head(?:\s[^>]*)?>/i, head =>
      `${head}\n    <base href="${base}" />\n    <meta name="unouml-build" content="${build}" />`);
    await writeFile(join(output, 'index.html'), entry);
    await writeFile(join(output, '.nojekyll'), '');
    if (files.some(path => path.startsWith('packages/')))
      await cp(join(source, 'packages'), join(output, 'packages'), { recursive: true });
    return { build, base, files: files.length };
  } catch (error) {
    await rm(output, { recursive: true, force: true });
    throw error;
  }
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  if (process.argv.length !== 4) throw new Error('Usage: node scripts/stage-pages.mjs <published-wwwroot> <new-pages-directory>');
  const result = await stagePages(process.argv[2], process.argv[3]);
  console.log(`Staged ${result.files} assets at ${result.base}`);
}
