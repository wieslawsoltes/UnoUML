import { activateControl } from './native-controls.mjs';

/** Observe the actual native/DOM frame before measuring a pointer gesture. */
async function waitForCanvasFrame(page, centered) {
  const surface = page.locator('.surface').first();
  await surface.waitFor({state:'visible'});
  const witness = await surface.evaluateHandle((surface, centered) =>
    ({surface, centered, signature:null, stable:0, last:null, svg:null}), centered);
  try {
    await page.waitForFunction(async witness => {
      const {surface} = witness;
      const svg = surface.querySelector('.paper > svg');
      const rect = surface.getBoundingClientRect(), matrix = svg?.getScreenCTM();
      const box = svg?.viewBox.baseVal;
      if (!surface.isConnected || !matrix || !(rect.width > 0 && rect.height > 0 && box.width > 0 && box.height > 0)) {
        witness.stable = 0;
        return false;
      }
      const host = surface.getRootNode().host;
      const module = await import(new URL('uml/viewport.js', document.baseURI).href);
      const gpu = module.rendererState(host.id);
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
        gpu:gpu ? {backend:gpu.backend,pending:gpu.pending,reason:gpu.reason,
          revision:gpu.revision,presentedRevision:gpu.presentedRevision,
          pixelWidth:gpu.statistics?.pixelWidth,pixelHeight:gpu.statistics?.pixelHeight} : null};
      witness.stable = ready && fitted && witness.svg === svg && witness.signature === signature ? witness.stable + 1 : 0;
      witness.svg = svg;
      witness.signature = signature;
      return witness.stable >= 3;
    }, witness, {polling:'raf',timeout:35000});
  } catch (cause) {
    const state = await witness.evaluate(witness => witness.last).catch(error => ({diagnosticError:error.message}));
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
