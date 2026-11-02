import { readFile, writeFile } from 'node:fs/promises';

// Bundle assignments encode the canonical lowercase lookup keys used by the TeaVM loader.
// Inspect verified source text without evaluating third-party code in the build process.
export async function indexStandardLibrary(output, files) {
  const paths = new Set();
  const property = '(?:\\.([\\w$]+)|\\["([^"\\\\]+)"\\])';
  const entries = new RegExp('window\\.PLANTUML_STDLIB' + property + property + '=', 'g');
  for (const filename of files.filter(name => name.endsWith('.min.js'))) {
    const contents = await readFile(new URL(filename, output), 'utf8');
    for (const match of contents.matchAll(entries)) {
      const path = (match[1] ?? match[2]) + '/' + (match[3] ?? match[4]);
      if (!/[<>\r\n\0]/.test(path)) paths.add(path);
    }
  }
  if (!paths.has('c4/c4_context') || paths.size < 1000)
    throw new Error('PlantUML standard library bundle format changed. Inspect the path indexer.');
  await writeFile(new URL('include-paths.txt', output), [...paths].sort().join('\n'));
  return paths.size;
}
