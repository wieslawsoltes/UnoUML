import { verifyGpuScene } from './gpu-scene.mjs';
import assert from 'node:assert/strict';
import { mkdir, writeFile } from 'node:fs/promises';
import { resolve } from 'node:path';
import { chromium } from 'playwright';
import { graphicsLaunchOptions } from './graphics-options.mjs';
import { publishedPrefix, servePublishedSite } from './published-site.mjs';
import { verifyGpuPresentation, verifyGpuFallback } from './gpu-presentation.mjs';
import { verifyAccessibility } from './accessibility.mjs';
import { diagnoseNativeAccessibility } from './native-accessibility-diagnostics.mjs';

const external = process.env.WORKSPACE_URL;
if (!external && !process.argv[2]) throw new Error('Pass the published site directory or set WORKSPACE_URL.');
const server = external ? null : await servePublishedSite(process.argv[2]);
const browser = await chromium.launch(graphicsLaunchOptions(true));
const logs = [], errors = [];
let page;
try {
  page = await browser.newPage({viewport:{width:1390,height:860},colorScheme:'light',acceptDownloads:true});
  page.setDefaultTimeout(35000);
  page.on('console', message => { if(logs.length<2000) logs.push(message.type()+': '+message.text()); });
  page.on('pageerror', error => errors.push(error.message));
  const url = external || 'http://127.0.0.1:' + server.address().port + publishedPrefix;
  await page.goto(url,{waitUntil:'domcontentloaded'});
  await page.locator('.paper svg').first().waitFor({state:'visible',timeout:120000});
  await verifyGpuPresentation(page);
  try { await verifyAccessibility(page); }
  catch (error) { await diagnoseNativeAccessibility(page); throw error; }
  await verifyGpuScene(page);
  await verifyGpuFallback(browser,url);
  assert.deepEqual(errors,[]);
  console.log('Passed independent published Uno WebGPU, source input, accessibility and fallback smoke checks.');
} finally {
  if (page) {
    const current = await page.locator('.surface').first().evaluate(async surface => {
      const module = await import(new URL('uml/viewport.js', document.baseURI).href);
      return module.rendererState(surface.getRootNode().host.id);
    }).catch(error => ({diagnosticError:error.message}));
    console.log('Final GPU presentation diagnostics:', JSON.stringify(current));
    for (const line of logs.filter(line => /error:|warning:|UnoUML|backend|WebGPU/i.test(line)).slice(-100))
      console.log('Browser ' + line);
  }
  if (page && process.env.SCREENSHOT_DIR) {
    await mkdir(process.env.SCREENSHOT_DIR,{recursive:true});
    await writeFile(resolve(process.env.SCREENSHOT_DIR,'gpu-browser-console.txt'),logs.join('\n'));
    await writeFile(resolve(process.env.SCREENSHOT_DIR,'gpu-browser-errors.json'),JSON.stringify(errors,null,2));
    await writeFile(resolve(process.env.SCREENSHOT_DIR,'gpu-workspace.html'),await page.content());
    await page.screenshot({path:resolve(process.env.SCREENSHOT_DIR,'gpu-workspace.png')}).catch(()=>{});
  }
  await browser.close();
  if (server) await new Promise(resolve=>server.close(resolve));
}
