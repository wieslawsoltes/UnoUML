import assert from 'node:assert/strict';
import { mkdir, writeFile } from 'node:fs/promises';
import { resolve } from 'node:path';

async function state(page) {
  return page.locator('.surface').first().evaluate(async surface => {
    const module = await import(new URL('uml/viewport.js', document.baseURI).href);
    return module.rendererState(surface.getRootNode().host.id);
  });
}

async function presented(page) {
  await page.waitForFunction(async () => {
    const host = [...document.querySelectorAll('*')].find(element => element.shadowRoot?.querySelector('.surface'));
    if (!host) return false;
    const module = await import(new URL('uml/viewport.js', document.baseURI).href);
    const value = module.rendererState(host.id);
    return value?.backend === 'webgpu' && !value.pending && value.presentedRevision === value.revision;
  }, null, {timeout:60000});
  return state(page);
}

async function save(name, value) {
  if (!process.env.SCREENSHOT_DIR) return;
  await mkdir(process.env.SCREENSHOT_DIR, {recursive:true});
  await writeFile(resolve(process.env.SCREENSHOT_DIR, name), value);
}

// Compare the actual composited GPU viewport with the still-attached SVG at the
// same camera. Both screenshots include the same grid and exclude native editor chrome.
async function comparePaint(page, name) {
  const surface = page.locator('.surface').first();
  const gpu = await surface.screenshot({animations:'disabled'});
  await surface.evaluate(element => element.classList.remove('gpu-presented'));
  let reference;
  try { reference = await surface.screenshot({animations:'disabled'}); }
  finally { await surface.evaluate(element => element.classList.add('gpu-presented')); }
  const metrics = await page.evaluate(async ([a, b]) => {
    async function pixels(bytes) {
      const bitmap = await createImageBitmap(new Blob([new Uint8Array(bytes)], {type:'image/png'}));
      const canvas = new OffscreenCanvas(bitmap.width, bitmap.height);
      const context = canvas.getContext('2d', {willReadFrequently:true});
      context.drawImage(bitmap, 0, 0);
      const result = context.getImageData(0, 0, bitmap.width, bitmap.height);
      bitmap.close();
      return result;
    }
    const gpu = await pixels(a), svg = await pixels(b);
    if (gpu.width !== svg.width || gpu.height !== svg.height) throw new Error('Comparison dimensions changed.');
    let absolute = 0, major = 0, referenceInk = 0, gpuInk = 0;
    const count = gpu.width * gpu.height;
    for (let i = 0; i < gpu.data.length; i += 4) {
      let distance = 0;
      for (let c = 0; c < 3; c++) {
        const difference = Math.abs(gpu.data[i+c] - svg.data[i+c]);
        absolute += difference;
        distance = Math.max(distance, difference);
      }
      if (distance > 64) major++;
      if (Math.min(svg.data[i], svg.data[i+1], svg.data[i+2]) < 150) referenceInk++;
      if (Math.min(gpu.data[i], gpu.data[i+1], gpu.data[i+2]) < 150) gpuInk++;
    }
    return {
      width:gpu.width, height:gpu.height,
      meanAbsoluteChannelError:absolute/(count*3),
      majorDifferenceFraction:major/count, referenceInk, gpuInk
    };
  }, [Array.from(gpu), Array.from(reference)]);
  await Promise.all([
    save(name + '-webgpu.png', gpu),
    save(name + '-svg.png', reference),
    save(name + '-quality.json', JSON.stringify(metrics, null, 2))
  ]);
  assert.ok(metrics.referenceInk > 100, 'The quality reference must contain real diagram ink.');
  assert.ok(metrics.gpuInk > metrics.referenceInk * .6, 'The GPU surface must visibly contain the drawing.');
  assert.ok(metrics.gpuInk < metrics.referenceInk * 1.5, 'GPU paint must not fill large regions incorrectly.');
  assert.ok(metrics.meanAbsoluteChannelError < 16, 'GPU/SVG average channel error exceeds the smoke-test budget.');
  assert.ok(metrics.majorDifferenceFraction < .12, 'GPU/SVG disagreement exceeds the smoke-test budget.');
  return metrics;
}

export async function verifyGpuPresentation(page) {
  const required = process.env.UNOUML_REQUIRE_WEBGPU === '1';
  let initial = await state(page);
  if (!required) {
    console.log('Graphics presentation (not required by this run):', JSON.stringify(initial));
    return;
  }
  initial = await presented(page);
  assert.equal(initial.statistics.sampleCount, 4, 'The diagram renderer must use the required 4× MSAA target.');
  assert.equal(initial.statistics.backend, 'Uno shared WebGPU');
  assert.equal(initial.statistics.pipelineCount, 6);
  assert.ok(initial.statistics.renderBundles > 0);
  assert.ok(initial.statistics.drawCalls > 0);
  assert.ok(initial.statistics.vertexBytes > 0);
  assert.ok(initial.statistics.textureBytes > 0, 'The starter diagram must upload text resources.');
  assert.equal(initial.statistics.commands, initial.statistics.visibleCommands + initial.statistics.culledCommands);
  const quality = await comparePaint(page, 'gpu-starter');

  const measurement = await page.locator('.surface').first().evaluate(async surface => {
    const module = await import(new URL('uml/viewport.js', document.baseURI).href);
    const id = surface.getRootNode().host.id;
    const frame = () => new Promise(resolve => requestAnimationFrame(resolve));
    const samples = [];
    for (let i = 0; i < 24; i++) {
      const started = performance.now();
      const before = module.rendererState(id).revision;
      module.zoom(id, i % 2 ? 1 / 1.002 : 1.002);
      for (let n = 0; n < 240; n++) {
        await frame();
        const current = module.rendererState(id);
        if (!current.pending && current.revision > before && current.presentedRevision === current.revision && current.backend === 'webgpu') {
          samples.push({wallMilliseconds:performance.now()-started, ...current.statistics});
          break;
        }
      }
    }
    const adapter = await navigator.gpu?.requestAdapter();
    const info = adapter?.info;
    return {
      userAgent:navigator.userAgent, devicePixelRatio,
      probeAdapter:info ? {vendor:info.vendor, architecture:info.architecture, device:info.device,
        description:info.description, isFallbackAdapter:info.isFallbackAdapter} : null,
      samples
    };
  });
  assert.equal(measurement.samples.length, 24, 'Every camera update must reach a presented frame.');
  assert.ok(measurement.samples.every(sample => Number.isFinite(sample.cpuEncodeMilliseconds)));
  const final = await presented(page);
  assert.equal(final.statistics.vertexBytes, initial.statistics.vertexBytes,
    'Small camera changes within the same quality bucket must reuse geometry.');
  assert.equal(final.statistics.textureBytes, initial.statistics.textureBytes,
    'Small camera changes within the same quality bucket must reuse text/image resources.');
  assert.ok(final.statistics.cameraUploadBytes > initial.statistics.cameraUploadBytes, 'Camera updates must reach the shared GPU.');
  for (const counter of ['geometryUploadBytes','textureUploadBytes','drawUniformUploadBytes','gradientUploadBytes','bundleBuilds'])
    assert.equal(final.statistics[counter], initial.statistics[counter], 'Camera updates must retain ' + counter);
  const timings = measurement.samples.map(sample => sample.wallMilliseconds).sort((a,b)=>a-b);
  const report = {
    purpose:'Correctness and reproducible camera-update smoke measurements; not a hardware performance claim.',
    driver:process.env.UNOUML_GPU_DRIVER || (process.platform === 'linux' ? 'software' : 'platform'),
    quality, initial, final, ...measurement,
    cameraWallMilliseconds:{median:timings[Math.floor(timings.length*.5)], p95:timings[Math.floor(timings.length*.95)]}
  };
  await save('gpu-presentation.json', JSON.stringify(report, null, 2));
  console.log('Verified shared-device WebGPU presentation, 4× MSAA, visible paint, retained resources and camera acknowledgements.');
}

export async function verifyGpuFallback(browser, url) {
  if (process.env.UNOUML_REQUIRE_WEBGPU !== '1') return;
  const context = await browser.newContext({viewport:{width:1280,height:850}, colorScheme:'light'});
  await context.addInitScript(() => {
    Object.defineProperty(Navigator.prototype, 'gpu', {configurable:true, get:()=>undefined});
  });
  const page = await context.newPage();
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  try {
    await page.goto(url, {waitUntil:'domcontentloaded'});
    await page.locator('.paper svg').first().waitFor({state:'visible',timeout:120000});
    const value = await state(page);
    assert.equal(value.backend, 'svg', 'Unavailable WebGPU must preserve a visible SVG diagram.');
    assert.equal(await page.locator('.paper').first().evaluate(element => getComputedStyle(element).opacity), '1');
    await page.getByRole('button', {name:'Split',exact:true}).click();
    const source = page.getByRole('textbox', {name:'PlantUML source',exact:true});
    const original = await source.inputValue();
    await source.fill(original.replace('Storefront','Fallback storefront'));
    await page.locator('.paper text').filter({hasText:/Fallback storefront/}).first().waitFor();
    assert.deepEqual(errors, []);
    await save('gpu-fallback.png', await page.screenshot());
    console.log('Verified Uno Skia/SVG startup and source editing without navigator.gpu.');
  } finally { await context.close(); }
}
