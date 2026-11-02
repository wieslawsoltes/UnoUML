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
  const svg = await response.text();
  if (!response.ok) {
    const diagnostic = response.headers.get('X-PlantUML-Diagram-Error');
    throw new Error(diagnostic || 'The PlantUML server returned HTTP ' + response.status + '.');
  }
  return svg;
}
