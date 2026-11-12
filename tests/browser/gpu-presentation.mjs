import assert from 'node:assert/strict';
import { mkdir, writeFile } from 'node:fs/promises';
import { resolve } from 'node:path';
import { activateAccessibility, activateControl, fillControl } from './native-controls.mjs';

async function state(page) {
  return page.locator('.surface').first().evaluate(async surface => {
    const module = await import(new URL('uml/viewport.js', document.baseURI).href);
    return module.rendererState(surface.getRootNode().host.id);
  });
}

async function presented(page, afterRevision = -1) {
  // Playwright's polling predicate is synchronous: an async predicate returns a
  // truthy Promise before its boolean result is available. Own the bounded async
  // poll and return the same snapshot that satisfied readiness.
  return page.locator('.surface').first().evaluate(async (surface, afterRevision) => {
    const module = await import(new URL('uml/viewport.js', document.baseURI).href);
    const id = surface.getRootNode().host.id, deadline = performance.now() + 60000;
    while (true) {
      const value = module.rendererState(id);
      if (value?.backend === 'webgpu' && !value.pending && value.revision > Math.max(0, afterRevision) &&
          value.presentedRevision === value.revision && value.statistics?.revision === value.revision)
        return value;
      if (performance.now() > deadline)
        throw new Error('No validated GPU frame reached presentation: ' + JSON.stringify(value));
      await new Promise(resolve => setTimeout(resolve, 25));
    }
  }, afterRevision);
}

async function save(name, value) {
  if (!process.env.SCREENSHOT_DIR) return;
  await mkdir(process.env.SCREENSHOT_DIR, {recursive:true});
  await writeFile(resolve(process.env.SCREENSHOT_DIR, name), value);
}

// Compare the actual composited GPU viewport with the still-attached SVG at the
// same camera. Both screenshots include the same grid and exclude native editor chrome.
async function comparePaint(page, name, {overlaySelector = null} = {}) {
  const surface = page.locator('.surface').first();
  const gpu = await surface.screenshot({animations:'disabled'});
  const paintClasses = await surface.evaluate(element => {
    const classes = ['gpu-presented','gpu-overlays-presented'].filter(name => element.classList.contains(name));
    element.classList.remove(...classes);
    return classes;
  });
  let reference, withoutOverlay;
  try {
    reference = await surface.screenshot({animations:'disabled'});
    if (overlaySelector) {
      // The same-camera negative reference removes only selection paint. Its
      // changed pixels isolate the overlay from diagram ink and grid coverage.
      const overlay = surface.locator(overlaySelector);
      const opacity = await overlay.evaluate(element => {
        const previous = {value:element.style.getPropertyValue('opacity'), priority:element.style.getPropertyPriority('opacity')};
        element.style.setProperty('opacity','0','important');
        return previous;
      });
      try { withoutOverlay = await surface.screenshot({animations:'disabled'}); }
      finally {
        await overlay.evaluate((element,previous) => {
          if (previous.value) element.style.setProperty('opacity',previous.value,previous.priority);
          else element.style.removeProperty('opacity');
        }, opacity);
      }
    }
  } finally {
    await surface.evaluate((element,classes) => element.classList.add(...classes), paintClasses);
  }
  const metrics = await page.evaluate(async ([a, b, c]) => {
    async function pixels(bytes) {
      const bitmap = await createImageBitmap(new Blob([new Uint8Array(bytes)], {type:'image/png'}));
      const canvas = new OffscreenCanvas(bitmap.width, bitmap.height);
      const context = canvas.getContext('2d', {willReadFrequently:true});
      context.drawImage(bitmap, 0, 0);
      const result = context.getImageData(0, 0, bitmap.width, bitmap.height);
      bitmap.close();
      return result;
    }
    const gpu = await pixels(a), svg = await pixels(b), absent = c ? await pixels(c) : null;
    if (gpu.width !== svg.width || gpu.height !== svg.height ||
        absent && (absent.width !== svg.width || absent.height !== svg.height))
      throw new Error('Comparison dimensions changed.');
    let absolute = 0, major = 0, referenceInk = 0, gpuInk = 0;
    let overlayPixels = 0, overlayError = 0, absentError = 0, overlayMatches = 0;
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
      if (absent) {
        let separation = 0, expectedError = 0, missingError = 0;
        for (let channel = 0; channel < 3; channel++) {
          const difference = Math.abs(svg.data[i+channel] - absent.data[i+channel]);
          separation = Math.max(separation,difference);
          missingError += difference;
          expectedError += Math.abs(gpu.data[i+channel] - svg.data[i+channel]);
        }
        if (separation > 64) {
          overlayPixels++;
          overlayError += expectedError;
          absentError += missingError;
          if (expectedError < missingError) overlayMatches++;
        }
      }
    }
    return {
      width:gpu.width, height:gpu.height,
      meanAbsoluteChannelError:absolute/(count*3),
      majorDifferenceFraction:major/count, referenceInk, gpuInk,
      overlay:absent ? {pixels:overlayPixels,
        gpuMeanChannelError:overlayPixels ? overlayError/(overlayPixels*3) : null,
        absentMeanChannelError:overlayPixels ? absentError/(overlayPixels*3) : null,
        matchedFraction:overlayPixels ? overlayMatches/overlayPixels : 0} : null
    };
  }, [Array.from(gpu), Array.from(reference), withoutOverlay ? Array.from(withoutOverlay) : null]);
  await Promise.all([
    save(name + '-webgpu.png', gpu),
    save(name + '-svg.png', reference),
    withoutOverlay ? save(name + '-without-selection.png', withoutOverlay) : Promise.resolve(),
    save(name + '-quality.json', JSON.stringify(metrics, null, 2))
  ]);
  assert.ok(metrics.referenceInk > 100, 'The quality reference must contain real diagram ink.');
  assert.ok(metrics.gpuInk > metrics.referenceInk * .9, 'The GPU surface must preserve diagram ink.');
  assert.ok(metrics.gpuInk < metrics.referenceInk * 1.1, 'GPU paint must not introduce substantial extra ink.');
  assert.ok(metrics.meanAbsoluteChannelError < 3, 'GPU/SVG average channel error exceeds the quality budget.');
  assert.ok(metrics.majorDifferenceFraction < .015, 'GPU/SVG disagreement exceeds the quality budget.');
  if (overlaySelector) {
    assert.ok(metrics.overlay.pixels > 40, 'The overlay reference must contain substantial selection ink.');
    assert.ok(metrics.overlay.gpuMeanChannelError < metrics.overlay.absentMeanChannelError * .65,
      'Native GPU selection paint must match the reference substantially better than an absent overlay.');
    assert.ok(metrics.overlay.matchedFraction > .7,
      'Most selection ink pixels must be closer to the painted reference than the absent-overlay control.');
  }
  return metrics;
}


async function verifySelectionOverlay(page) {
  const surface = page.locator('.surface').first();
  const selection = surface.locator('.selection');
  // This runs after the retained-camera benchmark; native semantics expose the
  // unchanged document history without entering a source-editing operation.
  await activateAccessibility(page);
  const history = async () => ({
    undoDisabled:await page.getByRole('button',{name:'Undo',exact:true}).isDisabled(),
    redoDisabled:await page.getByRole('button',{name:'Redo',exact:true}).isDisabled()
  });
  const documentState = () => surface.evaluate(async element => {
    const module = await import(new URL('uml/viewport.js',document.baseURI).href);
    const state = module.rendererState(element.getRootNode().host.id);
    const stored = localStorage.getItem('UnoUML:unouml.document.v1');
    return {
      sourceVersion:state.compilation?.sourceVersion, viewVersion:state.compilation?.viewVersion,
      sourceText:[...document.querySelectorAll('textarea[aria-label="PlantUML source"]')].map(input=>input.value),
      draftSource:stored ? JSON.parse(stored).Source : null
    };
  });
  const originalDocument = await documentState(), originalHistory = await history();
  assert.deepEqual(originalHistory,{undoDisabled:true,redoDisabled:true},
    'The starter overlay check begins with an empty document history.');
  assert.equal(await selection.isHidden(),true,'The overlay check begins without a selected object.');
  const before = await presented(page);
  const target = surface.locator('.paper .participant-head[data-uml-hit]').filter({hasText:'Storefront'}).first();
  let selected, decoration, quality, failure, cleared;
  try {
    await target.click();
    await page.mouse.move(1,1);
    await selection.waitFor({state:'visible'});
    selected = await presented(page,before.revision);
    decoration = await selection.evaluate(element => {
      const style = getComputedStyle(element), box = element.getBoundingClientRect();
      const surface = element.closest('.surface');
      return {connected:element.isConnected,hidden:element.hidden,opacity:style.opacity,
        width:box.width,height:box.height,pointerEvents:style.pointerEvents,
        gpuPaint:surface.classList.contains('gpu-presented'),
        overlayPaint:surface.classList.contains('gpu-overlays-presented')};
    });
    assert.equal(decoration.connected,true);
    assert.equal(decoration.hidden,false);
    assert.ok(decoration.width>10&&decoration.height>10,'Selection geometry remains available for DOM interaction.');
    assert.equal(decoration.opacity,'0','The DOM selection remains attached while its paint is suppressed.');
    assert.equal(decoration.gpuPaint,true);
    assert.equal(decoration.overlayPaint,true);
    assert.ok(Number.isSafeInteger(selected.statistics.viewportCommands)&&selected.statistics.viewportCommands>0,
      'The accepted native scene must retain viewport-space overlay commands.');
    assert.ok(Number.isSafeInteger(selected.statistics.visibleViewportCommands)&&selected.statistics.visibleViewportCommands>0,
      'Native culling must retain visible viewport-space overlay commands.');
    assert.ok(selected.statistics.visibleViewportCommands<=selected.statistics.viewportCommands);
    quality = await comparePaint(page,'gpu-selection-overlay',{overlaySelector:'.selection'});
  } catch (error) { failure=error; }
  try {
    const revision = (await presented(page)).revision;
    await surface.press('Escape');
    await page.mouse.move(1,1);
    await selection.waitFor({state:'hidden'});
    cleared = await presented(page,revision);
  } catch (error) {
    if (failure) throw new AggregateError([failure,error],'Overlay validation and selection cleanup failed');
    throw error;
  }
  if (failure) throw failure;
  assert.deepEqual(await documentState(),originalDocument,'Pointer selection and Escape preserve the source document.');
  assert.deepEqual(await history(),originalHistory,'Overlay interaction must not create undo or redo entries.');
  return {before,selected,cleared,decoration,quality};
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
  const overlay = await verifySelectionOverlay(page);
  const timings = measurement.samples.map(sample => sample.wallMilliseconds).sort((a,b)=>a-b);
  const report = {
    purpose:'Correctness and reproducible camera-update smoke measurements; not a hardware performance claim.',
    driver:process.env.UNOUML_GPU_DRIVER || (process.platform === 'linux' ? 'software' : 'platform'),
    quality, initial, final, overlay, ...measurement,
    cameraWallMilliseconds:{median:timings[Math.floor(timings.length*.5)], p95:timings[Math.floor(timings.length*.95)]}
  };
  await save('gpu-presentation.json', JSON.stringify(report, null, 2));
  console.log('Verified shared-device WebGPU presentation, 4× MSAA, visible paint, retained resources, camera acknowledgements and native selection overlays.');
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
    await activateAccessibility(page);
    await activateControl(page, page.getByRole('button', {name:'Split',exact:true}));
    const source = page.getByRole('textbox', {name:'PlantUML source',exact:true});
    const original = await source.inputValue();
    await fillControl(page, source, original.replace('Storefront','Fallback storefront'));
    await page.locator('.paper text').filter({hasText:/Fallback storefront/}).first().waitFor();
    assert.deepEqual(errors, []);
    await save('gpu-fallback.png', await page.screenshot());
    console.log('Verified Uno Skia/SVG startup and source editing without navigator.gpu.');
  } finally { await context.close(); }
}
