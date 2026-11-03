// User-imported files are the only files readable by PlantUML's local include loader.
const memory = new Map();
// Managed and reference renderers read the same imported virtual file system.
export function readInclude(path) { return memory.get(path) ?? null; }
let database, hydrated;
export let includeRevision = 0;

function openDatabase() {
  return database ??= new Promise((resolve, reject) => {
    const request = indexedDB.open('UnoUML-Includes-v1', 1);
    request.onupgradeneeded = () => request.result.createObjectStore('includes');
    request.onsuccess = () => resolve(request.result);
    request.onerror = () => reject(request.error);
  });
}
export async function includeFiles() {
  hydrated ??= (async () => {
    try {
      const db = await openDatabase();
      await new Promise((resolve, reject) => {
        const tx = db.transaction('includes');
        const cursor = tx.objectStore('includes').openCursor();
        cursor.onsuccess = () => {
          const row = cursor.result;
          if (row) { memory.set(row.key, row.value); row.continue(); }
        };
        tx.oncomplete = resolve;
        tx.onabort = tx.onerror = () => reject(tx.error);
      });
    } catch { /* Session-only includes still work when storage is disabled. */ }
  })();
  await hydrated;
  return memory;
}

function pick(accept, multiple, directory) {
  return new Promise(resolve => {
    const input = document.createElement('input');
    input.type = 'file';
    input.accept = accept;
    input.multiple = multiple;
    if (directory) input.webkitdirectory = true;
    input.hidden = true;
    document.body.append(input);
    const finish = files => { input.remove(); resolve(files); };
    input.onchange = () => finish([...input.files]);
    input.oncancel = () => finish([]);
    input.click();
  });
}
export async function openFile(accept) {
  const [file] = await pick(accept, false, false);
  return file ? JSON.stringify({ name: file.name, text: await file.text() }) : null;
}
export async function importIncludes(folder) {
  const chosen = await pick('', true, folder);
  // A directory picker can return binaries and .git data. Only read source
  // formats when importing a directory. Individual file picks are explicit.
  const files = folder ? chosen.filter(f => !f.webkitRelativePath.split('/').some(p => p.startsWith('.')) &&
    /\.(?:puml|plantuml|iuml|uml|pu|inc|txt|iss|json|ya?ml|csv|sprite)$/i.test(f.name)) : chosen;
  if (!files.length) return 0;
  await includeFiles();
  const entries = [];
  for (const file of files) {
    const path = folder ? file.webkitRelativePath.split('/').slice(1).join('/') : file.name;
    entries.push([path, await file.text()]);
  }
  for (const [path, text] of entries) memory.set(path, text);
  includeRevision++;
  try {
    const db = await openDatabase();
    await new Promise((resolve, reject) => {
      const tx = db.transaction('includes', 'readwrite');
      const store = tx.objectStore('includes');
      for (const [path, text] of entries) store.put(text, path);
      tx.oncomplete = resolve;
      tx.onabort = tx.onerror = () => reject(tx.error);
    });
  } catch { /* Keep this session's files available. */ }
  return entries.length;
}

export function download(fileName, blob) {
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = fileName;
  a.hidden = true;
  document.body.append(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 30000);
}
export function saveFile(fileName, text, mime) { download(fileName, new Blob([text], { type: mime })); }
