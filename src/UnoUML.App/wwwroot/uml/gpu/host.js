import { ScenePresentation } from './scene.js';

// Owns the hand-off between the live semantic SVG and Uno's shared-device canvas.
// Geometry, hit testing and export remain attached even when SVG paint is suppressed.
export class GpuPresentationHost {
  constructor(view, createPresentation = (view, emit, options) => new ScenePresentation(view, emit, options)) {
    this.view = view;
    this.createPresentation = createPresentation;
    this.available = false;
    this.backend = 'svg';
    this.pending = false;
    this.presentedRevision = -1;
    this.stats = null;
    this.compileStats = null;
    this.reason = '';
    this.disposed = false;
    const document = view.surface.ownerDocument;
    this.selections = document.createElement('div');
    this.selections.className = 'gpu-selections';
    this.selections.setAttribute('aria-hidden', 'true');
    this.hover = document.createElement('div');
    this.hover.className = 'gpu-hover';
    this.hover.hidden = true;
    this.hover.setAttribute('aria-hidden', 'true');
    view.surface.append(this.selections, this.hover);
    this.pointerOver = event => {
      this.hovered = view.pick(event.target);
      this.positionSelections();
    };
    this.pointerLeave = () => { this.hovered = null; this.hover.hidden = true; };
    view.surface.addEventListener('pointerover', this.pointerOver);
    view.surface.addEventListener('pointerleave', this.pointerLeave);
    view.surface.dataset.presentation = 'svg';
  }

  create() {
    if (this.renderer || !this.available || this.disposed) return;
    this.renderer = this.createPresentation(this.view, update => {
      if (!this.disposed && this.available) this.view.emit('gpuScene', { update });
    }, {
      onPending: pending => {
        if (this.disposed) return;
        if (pending) {
          this.pending = true;
          this.setPaint(false);
        }
      },
      onStats: statistics => { if (!this.disposed) this.compileStats = statistics; },
      onError: error => this.fail(error)
    });
  }

  setAvailability(available, reason = '') {
    if (this.disposed) return;
    available = !!available;
    if (available && this.available && this.renderer) return;
    this.available = available;
    this.reason = reason || '';
    this.view.surface.classList.toggle('gpu-enabled', available);
    if (!available) {
      this.renderer?.dispose();
      this.renderer = null;
      this.pending = false;
      this.setPaint(false);
      return;
    }
    this.create();
    if (this.view.svg) this.setScene(this.view.svg, this.view.version);
  }

  setScene(svg, sourceVersion = this.view.version) {
    if (this.disposed) return;
    if (this.available) this.reason = '';
    this.setPaint(false);
    if (!this.available) return;
    this.create();
    this.pending = true;
    this.renderer.setScene(svg, sourceVersion);
  }

  setCamera() {
    if (this.disposed) return;
    this.renderer?.setCamera();
    this.positionSelections();
  }

  present(revision, statistics) {
    if (this.disposed || !this.available || !Number.isSafeInteger(revision)) return false;
    if (!this.renderer?.presented(revision)) return false;
    this.presentedRevision = revision;
    this.stats = statistics;
    this.pending = false;
    this.reason = '';
    this.setPaint(true);
    return true;
  }

  rejectScene(reason) {
    if (this.disposed) return;
    this.renderer?.resetAfterRejection();
    this.fail(reason);
  }

  fail(error) {
    if (this.disposed) return;
    this.reason = error instanceof Error ? error.message : String(error || 'GPU scene compilation failed.');
    this.pending = false;
    // Keep observing scene-local failures so a later edit can recover GPU presentation.
    this.setPaint(false);
    console.warn('UnoUML GPU presentation:', this.reason);
  }

  setPaint(active) {
    this.view.surface.classList.toggle('gpu-presented', active);
    this.view.surface.dataset.presentation = active ? 'webgpu' : 'svg';
    const backend = active ? 'webgpu' : 'svg';
    if (this.backend !== backend || this.lastReason !== this.reason) {
      this.backend = backend;
      this.lastReason = this.reason;
      this.view.emit('presentation', { backend, reason: this.reason });
    }
    this.positionSelections();
  }

  positionSelections() {
    const active = this.backend === 'webgpu';
    const selected = active ? (this.view.additional || []).filter(element => element.isConnected) : [];
    while (this.selections.children.length > selected.length) this.selections.lastElementChild.remove();
    while (this.selections.children.length < selected.length) {
      const box = this.view.surface.ownerDocument.createElement('div');
      box.className = 'gpu-selection';
      this.selections.append(box);
    }
    selected.forEach((element, index) => this.view.outline(this.selections.children[index], element));
    this.view.outline(this.hover, active && this.hovered?.isConnected ? this.hovered : null);
  }

  state() {
    return {
      available: this.available, backend: this.backend, pending: this.pending,
      revision: this.renderer?.lastRevision ?? -1, presentedRevision: this.presentedRevision,
      reason: this.reason, statistics: this.stats, compiler: this.compileStats
    };
  }

  dispose() {
    if (this.disposed) return;
    this.disposed = true;
    this.renderer?.dispose();
    this.renderer = null;
    this.view.surface.removeEventListener('pointerover', this.pointerOver);
    this.view.surface.removeEventListener('pointerleave', this.pointerLeave);
    this.view.surface.classList.remove('gpu-enabled', 'gpu-presented');
    this.selections.remove();
    this.hover.remove();
  }
}
