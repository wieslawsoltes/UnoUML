// The endpoint is intentionally memory-only and cleared when switching documents.
let endpoint = '';
export function getServerEndpoint() { return endpoint; }
export function setServerEndpoint(value) {
  if (!value) { endpoint = ''; return; }
  const url = new URL(value);
  const loopback = url.hostname === 'localhost' || url.hostname === '[::1]' || /^127(?:\.\d{1,3}){3}$/.test(url.hostname);
  if ((url.protocol !== 'https:' && !(loopback && url.protocol === 'http:')) || url.username || url.password || url.hash)
    throw new Error('Use an HTTPS server endpoint, or HTTP on localhost.');
  endpoint = url.href;
}
export async function layoutOnServer(url, source, signal) {
  const response = await fetch(url, {
    method: 'POST', mode: 'cors', credentials: 'omit', cache: 'no-store', redirect: 'error',
    referrerPolicy: 'no-referrer', signal,
    headers: { 'Content-Type': 'text/plain;charset=UTF-8', Accept: 'image/svg+xml' }, body: source
  });
  if (!response.ok) {
    const diagnostic = response.headers.get('X-PlantUML-Diagram-Error');
    throw new Error(diagnostic || 'The PlantUML server returned HTTP ' + response.status + '.');
  }
  const bytes = await response.arrayBuffer();
  const signature = [137, 80, 78, 71, 13, 10, 26, 10];
  const header = new Uint8Array(bytes, 0, Math.min(8, bytes.byteLength));
  if (bytes.byteLength >= 24 && signature.every((b, i) => header[i] === b)) {
    // Ditaa produces a bitmap even when requested from some SVG endpoints.
    const dimensions = new DataView(bytes);
    const width = dimensions.getUint32(16), height = dimensions.getUint32(20);
    const url = await new Promise((resolve, reject) => {
      const reader = new FileReader();
      reader.onload = () => resolve(reader.result);
      reader.onerror = () => reject(reader.error);
      reader.readAsDataURL(new Blob([bytes], {type: 'image/png'}));
    });
    return `<svg xmlns="http://www.w3.org/2000/svg" width="${width}" height="${height}" viewBox="0 0 ${width} ${height}"><image href="${url}" width="${width}" height="${height}"/></svg>`;
  }
  return new TextDecoder().decode(bytes);
}
