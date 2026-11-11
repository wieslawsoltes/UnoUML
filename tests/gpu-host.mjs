import test from 'node:test';
import assert from 'node:assert/strict';
import { GpuPresentationHost } from '../src/UnoUML.App/wwwroot/uml/gpu/host.js';

function fixture() {
  const document = { createElement: () => new Element() };
  class Element {
    constructor() {
      this.ownerDocument = document;
      this.children = [];
      this.dataset = {};
      this.hidden = false;
      this.isConnected = true;
      this.handlers = new Map();
      const classes = new Set();
      this.classList = {
        toggle(name, enabled) { if (enabled) classes.add(name); else classes.delete(name); },
        remove(...names) { names.forEach(name => classes.delete(name)); },
        contains(name) { return classes.has(name); }
      };
    }
    setAttribute() {}
    append(...elements) { elements.forEach(element => { element.parent = this; this.children.push(element); }); }
    remove() { if (this.parent) this.parent.children.splice(this.parent.children.indexOf(this), 1); this.isConnected = false; }
    get lastElementChild() { return this.children.at(-1); }
    addEventListener(name, callback) { this.handlers.set(name, callback); }
    removeEventListener(name, callback) { if (this.handlers.get(name) === callback) this.handlers.delete(name); }
  }
  const emitted = [], compilers = [];
  const view = {
    surface: new Element(), svg: new Element(), version: 1, additional: [],
    emit: (kind, value) => emitted.push({kind, ...value}),
    pick: element => element,
    outline: (box, element) => { box.hidden = !element; box.element = element; }
  };
  let revision = 0;
  const host = new GpuPresentationHost(view, (_view, emit, options) => {
    const compiler = {
      options, lastRevision: -1, pending: false, disposed: false, scenes: 0, cameras: 0, resets: 0,
      setScene(svg, sourceVersion) {
        this.scenes++; this.svg = svg; this.sourceVersion = sourceVersion;
        this.pending = true; this.lastRevision = ++revision; options.onPending(true);
      },
      finish() { this.pending = false; emit({revision:this.lastRevision}); options.onPending(false); },
      setCamera() { this.cameras++; if (!this.pending) this.lastRevision = ++revision; },
      resetAfterRejection() { this.resets++; this.pending = true; },
      presented(value) { return !this.disposed && !this.pending && value === this.lastRevision; },
      dispose() { this.disposed = true; }
    };
    compilers.push(compiler);
    return compiler;
  });
  return {view, host, compilers, emitted, Element};
}

test('SVG paint is retained until the exact completed GPU transaction is acknowledged', () => {
  const {host, view, compilers} = fixture();
  host.setScene(view.svg);
  assert.equal(compilers.length, 0);
  host.setAvailability(true);
  const compiler = compilers[0];
  assert.equal(host.state().backend, 'svg');
  assert.equal(host.state().pending, true);
  assert.equal(host.present(compiler.lastRevision, {}), false, 'a still-compiling scene cannot hide SVG');
  compiler.finish();
  assert.equal(host.present(compiler.lastRevision + 1, {}), false);
  assert.equal(host.present(compiler.lastRevision, {drawCalls:3}), true);
  assert.equal(host.state().backend, 'webgpu');
  assert.equal(view.surface.classList.contains('gpu-presented'), true);
  assert.equal(host.state().statistics.drawCalls, 3);
});

test('new source and dirty geometry reject stale frame acknowledgements', () => {
  const {host, view, compilers, Element} = fixture();
  host.setAvailability(true);
  const compiler = compilers[0];
  compiler.finish(); host.present(compiler.lastRevision, {});
  const stale = compiler.lastRevision;
  view.svg = new Element(); view.version++;
  host.setScene(view.svg, view.version);
  assert.equal(host.state().backend, 'svg');
  assert.equal(host.present(stale, {}), false);
  compiler.finish();
  assert.equal(host.present(compiler.lastRevision, {}), true);
  compiler.options.onPending(true);
  assert.equal(host.state().backend, 'svg');
  assert.equal(view.svg.isConnected, true, 'semantic geometry must remain attached');
});

test('camera updates do not rebuild a scene and only their latest frame is presented', () => {
  const {host, compilers} = fixture();
  host.setAvailability(true);
  const compiler = compilers[0];
  compiler.finish(); host.present(compiler.lastRevision, {});
  const stale = compiler.lastRevision;
  host.setCamera(); host.setCamera();
  assert.equal(compiler.scenes, 1);
  assert.equal(compiler.cameras, 2);
  assert.equal(host.present(stale, {}), false);
  assert.equal(host.present(compiler.lastRevision, {}), true);
});

test('backend loss restores SVG and reinitialization uses a fresh compiler', () => {
  const {host, compilers} = fixture();
  host.setAvailability(true);
  const first = compilers[0];
  first.finish(); host.present(first.lastRevision, {});
  host.setAvailability(false, 'Device unavailable');
  assert.equal(first.disposed, true);
  assert.equal(host.state().backend, 'svg');
  assert.equal(host.state().reason, 'Device unavailable');
  assert.equal(host.present(first.lastRevision, {}), false);
  host.setAvailability(true);
  assert.equal(compilers.length, 2);
  assert.equal(host.state().pending, true);
  assert.equal(compilers[1].scenes, 1);
});

test('selection and hover decorations remain outside the suppressed SVG paint', () => {
  const {host, view, compilers, Element} = fixture();
  const selected = new Element(), hovered = new Element();
  view.additional = [selected];
  host.setAvailability(true);
  compilers[0].finish(); host.present(compilers[0].lastRevision, {});
  assert.equal(host.selections.children.length, 1);
  assert.equal(host.selections.children[0].element, selected);
  view.surface.handlers.get('pointerover')({target:hovered});
  assert.equal(host.hover.hidden, false);
  assert.equal(host.hover.element, hovered);
  view.surface.handlers.get('pointerleave')();
  assert.equal(host.hover.hidden, true);
  selected.isConnected = false;
  host.positionSelections();
  assert.equal(host.selections.children.length, 0);
  assert.equal(view.svg.isConnected, true);
});

test('compiler failure restores SVG, later edits recover, and disposal removes all owned listeners', () => {
  const {host, view, compilers, emitted} = fixture();
  host.setAvailability(true);
  const compiler = compilers[0];
  compiler.finish(); host.present(compiler.lastRevision, {});
  const warn = console.warn;
  try { console.warn = () => {}; compiler.options.onError(new Error('Texture budget exceeded')); }
  finally { console.warn = warn; }
  assert.equal(compiler.disposed, false, 'scene errors preserve the mutation observer for recovery');
  assert.equal(host.state().backend, 'svg');
  assert.equal(host.state().reason, 'Texture budget exceeded');
  assert.equal(emitted.at(-1).backend, 'svg');
  compiler.setScene(view.svg, view.version); compiler.finish();
  assert.equal(host.present(compiler.lastRevision, {}), true, 'a corrected scene returns to GPU');
  host.dispose(); host.dispose();
  assert.equal(view.surface.handlers.size, 0);
  assert.equal(view.surface.children.length, 0);
  assert.equal(view.svg.isConnected, true);
  assert.equal(host.present(compiler.lastRevision, {}), false);
  host.setAvailability(true);
  assert.equal(compilers.length, 1);
});

test('hover remains visible during recoverable SVG fallback without duplicating selection outlines', () => {
  const {host, view, compilers, Element} = fixture();
  const selected = new Element(), hovered = new Element();
  view.additional = [selected];
  host.setAvailability(true);
  compilers[0].finish(); host.present(compilers[0].lastRevision, {});
  view.surface.handlers.get('pointerover')({target:hovered});
  const warn = console.warn;
  try { console.warn = () => {}; compilers[0].options.onError(new Error('Texture budget exceeded')); }
  finally { console.warn = warn; }
  assert.equal(host.state().backend, 'svg');
  assert.equal(view.surface.classList.contains('gpu-enabled'), true, 'native hover filters remain suppressed');
  assert.equal(host.hover.hidden, false, 'replacement hover survives the scene-local fallback');
  assert.equal(host.hover.element, hovered);
  assert.equal(host.selections.children.length, 0, 'native SVG selection outlines are not duplicated');
  view.surface.handlers.get('pointerleave')();
  assert.equal(host.hover.hidden, true);
  view.surface.handlers.get('pointerover')({target:hovered});
  assert.equal(host.hover.hidden, false, 'hover continues to respond while SVG is visible');
  host.setAvailability(false, 'Device unavailable');
  assert.equal(host.hover.hidden, true, 'native hover resumes when GPU availability is removed');
  assert.equal(view.surface.classList.contains('gpu-enabled'), false);
  host.dispose();
});


test('atomic backend rejection resets the compiler baseline without discarding device availability', () => {
  const {host, compilers} = fixture();
  host.setAvailability(true);
  const compiler = compilers[0];
  compiler.finish(); host.present(compiler.lastRevision, {});
  const warn = console.warn;
  try { console.warn = () => {}; host.rejectScene('Scene budget exceeded'); }
  finally { console.warn = warn; }
  assert.equal(compiler.resets, 1);
  assert.equal(compiler.disposed, false);
  assert.equal(host.available, true);
  assert.equal(host.state().backend, 'svg');
  assert.equal(host.state().reason, 'Scene budget exceeded');
  assert.equal(host.present(compiler.lastRevision, {}), false);
  host.dispose();
});
