// User-imported files are the only files readable by PlantUML's local include loader.
const memory = new Map();
const archives = new Map(), archiveVersions = new Map();
let archiveVersion = 0;
// Managed and reference renderers read the same imported virtual file system.
export function readInclude(path) { return memory.get(path) ?? null; }
export function readArchive(path) { return archives.get(path) ?? null; }
export function readArchiveVersion(path) { return archiveVersions.get(path) ?? 0; }
let database, hydrated;
export let includeRevision = 0;

function openDatabase() {
  if (database) return database;
  const attempt = new Promise((resolve, reject) => {
    let blocked = false;
    const request = indexedDB.open('UnoUML-Includes-v1', 2);
    request.onupgradeneeded = () => {
      for (const name of ['includes','archives'])
        if (!request.result.objectStoreNames.contains(name)) request.result.createObjectStore(name);
    };
    request.onsuccess = () => {
      if (blocked) { request.result.close(); return; }
      request.result.onversionchange = () => request.result.close();
      resolve(request.result);
    };
    request.onblocked = () => { blocked = true; reject(new Error('Another UnoUML tab is using an earlier include database.')); };
    request.onerror = () => reject(request.error);
  });
  database = attempt;
  attempt.catch(() => { if (database === attempt) database = undefined; });
  return attempt;
}
export async function includeFiles() {
  hydrated ??= (async () => {
    try {
      const db = await openDatabase();
      await new Promise((resolve, reject) => {
        const tx = db.transaction(['includes','archives']);
        for (const [name, target] of [['includes',memory],['archives',archives]]) {
          const cursor = tx.objectStore(name).openCursor();
          cursor.onsuccess = () => {
            const row = cursor.result;
            if (row) {
              target.set(row.key, row.value);
              if (name === 'archives') archiveVersions.set(row.key, ++archiveVersion);
              row.continue();
            }
          };
        }
        tx.oncomplete = resolve;
        tx.onabort = tx.onerror = () => reject(tx.error);
      });
    } catch { /* Session-only includes still work when storage is disabled. */ }
  })();
  await hydrated;
  return memory;
}

export async function listIncludes() {
  await includeFiles();
  return JSON.stringify([...memory].filter(([,value]) => !value.startsWith('data:image/')).map(([path]) => path).sort());
}

async function persist(entries, packed = []) {
  try {
    const db = await openDatabase();
    await new Promise((resolve, reject) => {
      const tx = db.transaction(['includes','archives'], 'readwrite');
      const sources = tx.objectStore('includes'), bundles = tx.objectStore('archives');
      for (const [path, text] of entries) { sources.put(text,path); bundles.delete(path); }
      for (const [path, bytes] of packed) { bundles.put(bytes,path); sources.delete(path); }
      tx.oncomplete = resolve;
      tx.onabort = tx.onerror = () => reject(tx.error);
    });
    return true;
  } catch { return false; }
}

function remember(entries, packed) {
  for (const [path, text] of entries) {
    memory.set(path,text); archives.delete(path); archiveVersions.delete(path);
  }
  for (const [path, bytes] of packed) {
    archives.set(path,bytes); memory.delete(path); archiveVersions.set(path,++archiveVersion);
  }
  includeRevision++;
}

export async function updateInclude(path, expectedText, text) {
  await includeFiles();
  if (!memory.has(path) || memory.get(path) !== expectedText) return JSON.stringify({updated:false, persisted:false});
  memory.set(path, text);
  includeRevision++;
  try {
    const db = await openDatabase();
    const result = await new Promise((resolve,reject) => {
      const tx = db.transaction('includes','readwrite'), store = tx.objectStore('includes');
      let conflict;
      const current = store.get(path);
      current.onsuccess = () => {
        if (current.result !== undefined && current.result !== expectedText) conflict = current.result;
        else store.put(text,path);
      };
      tx.oncomplete = () => resolve({conflict});
      tx.onabort = tx.onerror = () => reject(tx.error);
    });
    if (result.conflict !== undefined) {
      if (memory.get(path) === text) { memory.set(path,result.conflict); includeRevision++; }
      return JSON.stringify({updated:false,persisted:false});
    }
    return JSON.stringify({updated:true,persisted:true});
  } catch {
    // Session editing remains available when persistent storage is unavailable.
    return JSON.stringify({updated:true,persisted:false});
  }
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

async function encoded(file) {
  if (file.size > 67108864) throw new Error('The imported resource exceeds 64 MiB.');
  const bytes = new Uint8Array(await file.arrayBuffer()), chunks = [];
  for (let at = 0; at < bytes.length; at += 32768)
    chunks.push(String.fromCharCode(...bytes.subarray(at, at + 32768)));
  return btoa(chunks.join(''));
}

export async function openArchive() {
  const [file] = await pick('.zip,.jar,application/zip,application/java-archive', false, false);
  return file ? JSON.stringify({name:file.name,data:await encoded(file)}) : null;
}

export async function saveArchive(path, data) {
  await includeFiles();
  remember([], [[path,data]]);
  const version = readArchiveVersion(path);
  const persisted = await persist([], [[path,data]]);
  return JSON.stringify({version,persisted});
}

export async function importIncludes(folder) {
  const chosen = await pick('', true, folder);
  // A directory picker can return binaries and .git data. Only read source
  // formats when importing a directory. Individual file picks are explicit.
  const files = folder ? chosen.filter(f => !f.webkitRelativePath.split('/').some(p => p.startsWith('.')) &&
    /\.(?:puml|plantuml|iuml|uml|pu|inc|txt|iss|json|ya?ml|csv|sprite|svg|png|jpe?g|gif|webp|zip|jar)$/i.test(f.name)) : chosen;
  if (!files.length) return 0;
  await includeFiles();
  const entries = [], packed = [];
  for (const file of files) {
    const path = folder ? file.webkitRelativePath.split('/').slice(1).join('/') : file.name;
    const extension = file.name.split('.').at(-1).toLowerCase();
    if (extension === 'zip' || extension === 'jar') packed.push([path, await encoded(file)]);
    else {
      const mime = {png:'image/png',jpg:'image/jpeg',jpeg:'image/jpeg',gif:'image/gif',webp:'image/webp'}[extension];
      entries.push([path, mime ? 'data:' + mime + ';base64,' + await encoded(file) : await file.text()]);
    }
  }
  remember(entries,packed);
  await persist(entries,packed);
  return entries.length + packed.length;
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
