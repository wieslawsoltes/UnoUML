import { createReadStream } from 'node:fs';
import { stat } from 'node:fs/promises';
import { createServer } from 'node:http';
import { extname, resolve, sep } from 'node:path';

export const publishedPrefix = '/UnoUML/';
const mime = {
  '.css':'text/css', '.html':'text/html', '.js':'text/javascript', '.json':'application/json',
  '.mjs':'text/javascript', '.svg':'image/svg+xml', '.ttf':'font/ttf', '.woff':'font/woff',
  '.woff2':'font/woff2', '.wasm':'application/wasm', '.txt':'text/plain', '.png':'image/png'
};

export async function servePublishedSite(directory) {
  const root = resolve(directory);
  await stat(resolve(root,'index.html'));
  const server = createServer(async (request,response) => {
    try {
      const path = decodeURIComponent(new URL(request.url,'http://localhost').pathname);
      if (!path.startsWith(publishedPrefix)) throw new Error('Outside the GitHub Pages project path');
      const file = resolve(root,path.slice(publishedPrefix.length)||'index.html');
      if (!file.startsWith(root+sep)||!(await stat(file)).isFile()) throw new Error('Missing asset');
      response.writeHead(200,{'Content-Type':mime[extname(file)]||'application/octet-stream'});
      createReadStream(file).pipe(response);
    } catch { response.writeHead(404);response.end(); }
  });
  await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
  return server;
}
