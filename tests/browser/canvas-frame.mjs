import { activateControl } from './native-controls.mjs';

/** Observe the actual native/DOM frame before measuring a pointer gesture. */
async function waitForCanvasFrame(page, centered) {
  const surface = page.locator('.surface').first();
  await surface.waitFor({state:'visible'});
  const witness = await surface.evaluateHandle(async (surface, centered) => {
    const module = await import(new URL('uml/viewport.js', document.baseURI).href);
    const witness = {surface, centered, rendererState:module.rendererState, signature:null, stable:0, last:null, svg:null};
    witness.diagnostics = () => {
      const host = surface.getRootNode().host;
      const canvas = document.getElementById('uno-canvas');
      const bounds = element => {
        if (!element) return null;
        const rect = element.getBoundingClientRect(), style = getComputedStyle(element);
        return {x:rect.x,y:rect.y,width:rect.width,height:rect.height,
          connected:element.isConnected,display:style.display,visibility:style.visibility,opacity:style.opacity};
      };
      const focus = [];
      let active = document.activeElement;
      while (active && focus.length < 6) {
        focus.push({tag:active.tagName,id:active.id,role:active.getAttribute('role'),
          automationId:active.getAttribute('xamlautomationid')});
        active = active.shadowRoot?.activeElement;
      }
      return {
        observed:witness.last,
        renderer:host ? witness.rendererState(host.id) : null,
        document:{visibility:document.visibilityState,hidden:document.hidden,hasFocus:document.hasFocus(),
          readyState:document.readyState,focus},
        capability:{secureContext:isSecureContext,webGpuApi:!!navigator.gpu,pixelRatio:devicePixelRatio || 1},
        host:host ? {id:host.id,...bounds(host)} : null,
        nativeCanvas:canvas ? {...bounds(canvas),pixelWidth:canvas.width,pixelHeight:canvas.height} : null
      };
    };
    return witness;
  }, centered);
  try {
    // Keep this predicate synchronous: a Promise itself is a truthy poll result.
    await page.waitForFunction(witness => {
      const {surface} = witness;
      const svg = surface.querySelector('.paper > svg');
      const rect = surface.getBoundingClientRect(), matrix = svg?.getScreenCTM();
      const box = svg?.viewBox.baseVal;
      if (!surface.isConnected || !matrix || !(rect.width > 0 && rect.height > 0 && box.width > 0 && box.height > 0)) {
        witness.stable = 0;
        return false;
      }
      const host = surface.getRootNode().host;
      const gpu = witness.rendererState(host.id);
      const ratio = devicePixelRatio || 1;
      const cx = box.x + box.width / 2, cy = box.y + box.height / 2;
      const center = {x:matrix.a * cx + matrix.c * cy + matrix.e, y:matrix.b * cx + matrix.d * cy + matrix.f};
      const error = {x:center.x - rect.left - rect.width / 2, y:center.y - rect.top - rect.height / 2};
      // The native GPU layer can acquire the new split-pane size before its
      // HTML presenter does. Require both dimensions and the newest camera /
      // scene acknowledgement, rather than accepting a stable but older CTM.
      const ready = gpu && !gpu.pending && (gpu.available && !gpu.reason
        ? gpu.backend === 'webgpu' && gpu.presentedRevision === gpu.revision
          && Math.abs(gpu.statistics?.pixelWidth - Math.round(rect.width * ratio)) <= 1
          && Math.abs(gpu.statistics?.pixelHeight - Math.round(rect.height * ratio)) <= 1
        : gpu.backend === 'svg');
      const fitted = !witness.centered || Math.abs(error.x) < 1 && Math.abs(error.y) < 1;
      const signature = JSON.stringify([rect.left,rect.top,rect.width,rect.height,
        matrix.a,matrix.b,matrix.c,matrix.d,matrix.e,matrix.f,box.x,box.y,box.width,box.height,
        surface.scrollLeft,surface.scrollTop,gpu?.revision,gpu?.presentedRevision]);
      witness.last = {rect:{x:rect.x,y:rect.y,width:rect.width,height:rect.height},
        matrix:[matrix.a,matrix.b,matrix.c,matrix.d,matrix.e,matrix.f],centerError:error,
        scroll:{x:surface.scrollLeft,y:surface.scrollTop},ready,fitted,
        gpu:gpu ?? null};
      witness.stable = ready && fitted && witness.svg === svg && witness.signature === signature ? witness.stable + 1 : 0;
      witness.svg = svg;
      witness.signature = signature;
      return witness.stable >= 3;
    }, witness, {polling:'raf',timeout:35000});
  } catch (cause) {
    const state = await witness.evaluate(witness => witness.diagnostics()).catch(error => ({diagnosticError:error.message}));
    // Diagnose the observed first-frame stall without accepting a late frame as
    // success. Foreground activation can distinguish native window scheduling
    // from compilation/device trouble when independent pages share Chromium.
    if (state.renderer?.available && state.renderer.pending && state.renderer.presentedRevision < 0) {
      const started = Date.now(), probe = state.foregroundProbe = {attempted:true,currentFramePresented:false};
      try {
        await page.bringToFront();
        const frame = await page.waitForFunction(witness => {
          const {surface} = witness, host = surface.getRootNode().host;
          const gpu = host && witness.rendererState(host.id), rect = surface.getBoundingClientRect();
          const ratio = devicePixelRatio || 1;
          return surface.isConnected && gpu?.available && !gpu.pending && !gpu.reason
            && gpu.backend === 'webgpu' && gpu.presentedRevision === gpu.revision
            && Math.abs(gpu.statistics?.pixelWidth - Math.round(rect.width * ratio)) <= 1
            && Math.abs(gpu.statistics?.pixelHeight - Math.round(rect.height * ratio)) <= 1;
        }, witness, {polling:100,timeout:3000});
        await frame.dispose();
        probe.currentFramePresented = true;
      } catch (error) {
        probe.error = error.message;
      }
      probe.elapsedMilliseconds = Date.now() - started;
      probe.after = await witness.evaluate(witness => witness.diagnostics()).catch(error => ({diagnosticError:error.message}));
    }
    throw new Error(`Canvas ${centered ? 'fit' : 'frame'} did not settle: ${JSON.stringify(state)}`, {cause});
  } finally {
    await witness.dispose();
  }
}

/** Fit once, after layout, then require its visible and presented result. */
export async function fitCanvas(page) {
  await waitForCanvasFrame(page, false);
  await activateControl(page, page.getByRole('button', {name:'Fit diagram',exact:true}));
  await waitForCanvasFrame(page, true);
}
