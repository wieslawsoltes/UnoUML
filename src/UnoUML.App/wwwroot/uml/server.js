// The endpoint is intentionally memory-only and cleared when switching documents.
let endpoint = '', encodedUrl = false;
export function getServerEndpoint() { return endpoint; }
export function getServerTransport() { return encodedUrl ? 'GET' : 'POST'; }
export function setServerEndpoint(value, useEncodedUrl = false) {
  encodedUrl = useEncodedUrl;
  if (!value) { endpoint = ''; return; }
  const url = new URL(value);
  const loopback = url.hostname === 'localhost' || url.hostname === '[::1]' || /^127(?:\.\d{1,3}){3}$/.test(url.hostname);
  if ((url.protocol !== 'https:' && !(loopback && url.protocol === 'http:')) || url.username || url.password || url.hash)
    throw new Error('Use an HTTPS server endpoint, or HTTP on localhost.');
  endpoint = url.href;
}
// PlantUML uses raw DEFLATE and its own 64-character alphabet.
export async function encodeDiagram(source) {
  const stream = new Blob([source]).stream().pipeThrough(new CompressionStream('deflate-raw'));
  const bytes = new Uint8Array(await new Response(stream).arrayBuffer());
  const alphabet = '0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz-_';
  let output = '';
  for (let i = 0; i < bytes.length; i += 3) {
    const bits = (bytes[i] << 16) | ((bytes[i + 1] || 0) << 8) | (bytes[i + 2] || 0);
    output += alphabet[bits >>> 18] + alphabet[(bits >>> 12) & 63] +
      alphabet[(bits >>> 6) & 63] + alphabet[bits & 63];
  }
  return output;
}

export async function layoutOnServer(url, source, signal, method = 'POST') {
  const headers = { Accept: 'image/svg+xml' };
  if (method === 'GET') {
    const target = new URL(url);
    target.pathname = target.pathname.replace(/\/$/, '') + '/' + await encodeDiagram(source);
    url = target.href;
  } else {
    headers['Content-Type'] = 'text/plain;charset=UTF-8';
  }
  const response = await fetch(url, {
    method, mode: 'cors', credentials: 'omit', cache: 'no-store', redirect: 'error',
    referrerPolicy: 'no-referrer', signal,
    headers, body: method === 'GET' ? undefined : source
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
