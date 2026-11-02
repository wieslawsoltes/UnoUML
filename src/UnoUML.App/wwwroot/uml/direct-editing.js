// Transient handles and text fields live outside the diagram SVG. Their screen-space
// bounds follow the transformed scene, and they never appear in exported diagrams.
export class DirectEditing {
  constructor(view) {
    this.view = view;
    this.port = document.createElement('button');
    this.port.type = 'button';
    this.port.className = 'connection-port';
    this.port.title = 'Drag to connect, or activate and choose a target';
    this.port.setAttribute('aria-label', 'Connect selected object');
    view.selection.append(this.port);
    this.port.hidden = true;
    this.port.addEventListener('keydown', e => {
      if (e.key !== ' ' && e.key !== 'Enter') return;
      e.preventDefault(); e.stopPropagation();
      this.startConnection();
      view.surface.focus({preventScroll: true});
    });
    this.box = document.createElement('div');
    this.box.className = 'marquee';
    this.box.hidden = true;
    view.surface.append(this.box);
  }

  startConnection() {
    const hit = this.view.index?.hits.get(this.view.marked);
    if (!hit) return;
    this.view.linkStart = hit;
    this.view.announcement.textContent = 'Choose the target object. Press Escape to cancel.';
  }

  beginLabel(label, revision) {
    const { marked, surface } = this.view;
    if (!marked?.isConnected || !surface.clientWidth) return false;
    this.finishLabel(false);
    const input = document.createElement('input');
    input.type = 'text';
    input.className = 'canvas-label-editor';
    input.setAttribute('aria-label', 'Edit diagram label');
    input.setAttribute('enterkeyhint', 'done');
    input.spellcheck = false;
    input.value = label;

    // Locate the actual glyphs where possible, including when the clicked hit is a
    // group spanning a complete connector, participant or class compartment.
    let anchor = marked;
    if (marked.localName !== 'text') {
      const texts = [...marked.querySelectorAll('text')];
      anchor = texts.find(el => el.textContent.trim() === label)
        || texts.find(el => el.textContent.trim().endsWith(label) && label.length > 0)
        || marked;
    }
    this.field = { input, original: label, revision, anchor };
    const stop = e => e.stopPropagation();
    for (const type of ['pointerdown','pointerup','pointermove','click','dblclick','cut','copy','paste','keyup','wheel'])
      input.addEventListener(type, stop);
    input.addEventListener('keydown', e => {
      e.stopPropagation();
      if (e.isComposing || e.keyCode === 229) return;
      if (e.key === 'Escape' || e.key === 'Enter' || e.key === 'Tab') {
        e.preventDefault();
        this.finishLabel(e.key !== 'Escape', true);
      }
    });
    input.addEventListener('blur', () => this.finishLabel(true));
    input.addEventListener('input', () => this.positionField());
    surface.append(input);
    this.positionField();
    input.focus({preventScroll: true});
    input.select();
    return true;
  }

  finishLabel(commit, refocus = false) {
    const field = this.field;
    if (!field) return;
    this.field = null;
    const text = field.input.value;
    field.input.remove();
    if (refocus) this.view.surface.focus({preventScroll: true});
    if (commit && text !== field.original)
      this.view.emit('label', {revision: field.revision, text});
  }

  positionField() {
    if (!this.field) return;
    const { input, anchor } = this.field;
    const bounds = this.view.rect(), target = anchor.getBoundingClientRect();
    const size = Math.max(13, Math.min(24, target.height * .78));
    const width = Math.max(1, Math.min(bounds.width - 8, Math.max(100, target.width + 18, input.value.length * size * .6 + 20)));
    const height = Math.min(40, Math.max(30, size + 14));
    const within = (n, max) => Math.max(4, Math.min(max - 4, n));
    Object.assign(input.style, {
      width: width + 'px', height: height + 'px', fontSize: size + 'px',
      left: within(target.left - bounds.left + (target.width - width) / 2, bounds.width - width) + 'px',
      top: within(target.top - bounds.top + (target.height - height) / 2, bounds.height - height) + 'px'
    });
  }

  marquee(drag, x, y) {
    const r = this.view.rect();
    const clamp = (n, min, max) => Math.max(min, Math.min(n, max));
    x = clamp(x, r.left, r.right); y = clamp(y, r.top, r.bottom);
    const left = Math.min(drag.x, x), top = Math.min(drag.y, y);
    const right = Math.max(drag.x, x), bottom = Math.max(drag.y, y);
    this.box.hidden = false;
    Object.assign(this.box.style, {left: left - r.left + 'px', top: top - r.top + 'px',
      width: right - left + 'px', height: bottom - top + 'px'});
    return {left, top, right, bottom};
  }

  selectRegion(drag, x, y) {
    const r = this.marquee(drag, x, y), chosen = new Set(), hits = [];
    // Read bounds once on release. Pointer moves only update the overlay rectangle.
    for (const el of this.view.objects || []) {
      const b = el.getBoundingClientRect();
      if (b.left < r.left || b.right > r.right || b.top < r.top || b.bottom > r.bottom) continue;
      if (chosen.has(el.parentElement?.closest('[data-uml-hit]'))) continue;
      chosen.add(el); hits.push(this.view.index.hits.get(el));
    }
    this.box.hidden = true;
    this.view.emit('region', {hits, add: drag.add});
    this.view.announcement.textContent = hits.length + ' objects inside the selection box';
  }
}
