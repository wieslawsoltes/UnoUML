import { GpuPresentationHost } from './gpu/host.js';
import { DirectEditing } from './direct-editing.js';
import { CanvasEditing } from './canvas-editing.js';
import { GanttEditing } from './gantt-editing.js';
import { indexHits, parseSvg } from './svg.js';
import { download, includeFiles, includeRevision } from './files.js';
import { beginResourcePass, prepareResources } from './resources.js';
export { readResource } from './resources.js';
export { syncSourceText, removeSourceText } from './source-text.js';
export { openFile, saveFile, saveFileJson, importIncludes, readInclude, listIncludes, updateInclude, openArchive, saveArchive, readArchive, readArchiveVersion, listArchives, updateArchive, downloadArchive } from './files.js';

import { getServerEndpoint, getServerTransport, layoutOnServer } from './server.js';
export { setServerEndpoint } from './server.js';


const views = new Map();
const empty = { id: '', line: -1, label: '' };
let engine;
function loadEngine() {
  return engine ??= new Promise((resolve, reject) => {
    const frame = document.createElement('iframe');
    frame.setAttribute('aria-hidden', 'true');
    frame.tabIndex = -1;
    // SVG text measurement needs a layout context (getBBox returns zero in display:none).
    frame.style.cssText = 'position:fixed;left:-10000px;top:0;width:800px;height:600px;visibility:hidden;pointer-events:none;border:0';
    frame.src = new URL('engine.html', import.meta.url);
    frame.onload = () => {
      const layout = frame.contentWindow.layoutDiagram;
      if (typeof layout === 'function') resolve(layout);
      else { frame.remove(); engine = null; reject(new Error('Could not load the bundled PlantUML engine.')); }
    };
    frame.onerror = () => { frame.remove(); engine = null; reject(new Error('PlantUML could not load.')); };
    document.body.append(frame);
  });
}
const svgNS = 'http://www.w3.org/2000/svg';
function html(tag, css, parent) {
  const el = document.createElement(tag);
  el.className = css;
  parent.append(el);
  return el;
}
class Viewport {
  constructor(host, emit, renderManaged, layoutManaged) {
    this.renderManaged = renderManaged;
    this.localMode = 'auto';
    this.host = host;
    this.emit = (kind, value) => {
      const json = JSON.stringify({ kind, ...value });
      if (kind === 'gpuScene' && json.length > 48 * 1024 * 1024)
        throw new Error('The GPU scene exceeds the bounded bridge transport budget.');
      emit(json);
    };
    this.z = 1; this.x = 0; this.y = 0; this.autoFit = true;
    this.version = 0; this.cache = new Map(); this.budget = 0; this.pointers = new Map();
    const root = host.shadowRoot || host.attachShadow({ mode: 'open' });
    root.replaceChildren();
    const css = html('link', '', root);
    css.rel = 'stylesheet'; css.href = new URL('preview.css', import.meta.url);
    this.surface = html('div', 'surface', root);
    this.surface.tabIndex = 0; this.surface.dataset.tool = 'select';
    this.surface.setAttribute('role', 'region');
    this.surface.setAttribute('aria-label', 'Diagram canvas. Drag shapes to move; handles resize and rotate. Arrow keys nudge selected shapes, Shift-arrow moves one grid step, Control-arrow selects objects. Enter edits a label. Shift-drag the background to box-select; control-wheel or pinch to zoom.');
    this.announcement = html('div', 'announcement', this.surface);
    this.announcement.setAttribute('role', 'status');
    this.announcement.setAttribute('aria-live', 'polite');
    this.paper = html('div', 'paper', this.surface);
    this.selection = html('div', 'selection', this.surface);
    this.target = html('div', 'target', this.surface);
    this.guide = document.createElementNS(svgNS, 'svg'); this.guide.setAttribute('class', 'link-guide');
    this.guide.hidden = true; this.guide.setAttribute('hidden','');
    this.line = document.createElementNS(svgNS, 'line');
    this.guide.append(this.line); this.surface.append(this.guide);
    this.selection.hidden = this.target.hidden = true;
    this.placeholder = html('div', 'empty', this.surface);
    this.placeholder.textContent = 'Preparing diagram…';
    this.sheetPicker = html('select', 'sheet-picker', root);
    this.sheetPicker.hidden = true;
    this.sheetPicker.setAttribute('aria-label', 'Sequence sheet');
    this.sheetPicker.addEventListener('change', () => {
      this.direct.finishLabel(false);
      this.showSheet(this.sheetPicker.selectedIndex);
      this.hit(null);
      this.fit();
      this.announcement.textContent = this.sheetPicker.selectedOptions[0].text;
    });
    this.direct = new DirectEditing(this);
    this.canvas = new CanvasEditing(this, layoutManaged);
    this.gantt = new GanttEditing(this);
    this.gpu = new GpuPresentationHost(this);
    this.bindGestures();
    this.resize = new ResizeObserver(() => this.autoFit ? this.fit() : this.position());
    this.resize.observe(host);
  }
  dispose() { this.gpu.dispose(); this.direct.finishLabel(false); this.gantt.cancel(); this.resize.disconnect(); this.version++; this.host.shadowRoot.replaceChildren(); this.cache.clear(); }
  async render(source, fileName = '') {
    this.direct.finishLabel(false);
    this.canvas.cancel();
    this.gantt.configure(false);
    const endpoint = getServerEndpoint();
    const transport = getServerTransport();
    const key = this.localMode + '\0' + transport + endpoint + '\0' + includeRevision + '\0' + fileName + '\0' + source;
    if (this.pristine && key === this.displayKey) return;
    const version = ++this.version;
    this.fetchJob?.abort();
    this.pristine = null;
    this.sheetPicker.disabled = true;
    let svg = this.cache.get(key);
    if (!svg) {
      if (!endpoint && this.localMode === 'auto' && /!include|!theme|!import|%load_json|%file_exists/.test(source)) {
        await includeFiles();
        if (version !== this.version) return;
      }
      beginResourcePass();
      let output = !endpoint && this.localMode === 'auto' ? this.renderManaged?.(source) : null;
      for (let pass = 0; !output && !endpoint && this.localMode === 'auto' && pass < 64; pass++) {
        if (!await prepareResources()) break;
        if (version !== this.version) return;
        beginResourcePass();
        output = this.renderManaged?.(source);
      }
      const backend = endpoint ? 'jvm' : output ? 'managed' : 'javascript';
      if (!output) {
        const controller = endpoint ? new AbortController() : null;
        this.fetchJob = controller;
        const files = endpoint ? null : await includeFiles();
        if (version !== this.version) return;
        const layout = endpoint ? s => layoutOnServer(endpoint, s, AbortSignal.any([controller.signal, AbortSignal.timeout(45000)]), transport) : await loadEngine();
        while (this.active) await this.active.catch(() => {});
        if (version !== this.version) return;
        const job = Promise.resolve().then(() => layout(source, files));
        this.active = job;
        try { output = await job; } finally { if (this.active === job) this.active = null; }
      }
      if (version !== this.version) return;
      svg = parseSvg(output);
      svg.backend = backend;
      const cost = output.length + key.length;
      if (cost < 3_000_000) {
        while (this.cache.size >= 8 || this.budget + cost > 3_000_000) {
          const first = this.cache.keys().next().value;
          this.budget -= this.cache.get(first).cost;
          this.cache.delete(first);
        }
        svg.cost = cost; this.cache.set(key, svg); this.budget += cost;
      }
    }
    if (version !== this.version) return;
    this.pristine = svg;
    this.sheetPicker.disabled = false;
    this.displayKey = key;
    this.paper.dataset.renderer = svg.backend;
    this.emit('renderer', { backend: svg.backend });
    const previousTitle = this.sheetPicker.selectedOptions[0]?.text;
    const previousIndex = this.sheetPicker.selectedIndex;
    this.sheets = svg.backend === 'managed' ? [...svg.querySelectorAll(':scope > svg[data-sequence-page]')] : [];
    this.sheetPicker.replaceChildren();
    this.sheetPicker.hidden = this.sheets.length < 2;
    if (this.sheets.length) {
      this.sheetPicker.add(new Option('All sheets', '0'));
      this.sheets.forEach((sheet, i) => {
        const title = sheet.getAttribute('data-page-title');
        this.sheetPicker.add(new Option(title ? (i+1) + ' · ' + title : 'Sheet ' + (i+1), String(i+1)));
      });
    }
    this.input = source;
    const keep = this.sheetPicker.options[previousIndex]?.text === previousTitle;
    this.showSheet(keep && previousIndex > 0 ? previousIndex : 0);
  }
  showSheet(index) {
    if (!this.pristine) return;
    const priorOrigin = this.svg ? {x:this.svg.viewBox.baseVal.x,y:this.svg.viewBox.baseVal.y} : null;
    const svg = this.sheets[index-1] || this.pristine;
    this.sheetPicker.selectedIndex = index;
    this.exportSource = svg;
    this.svg = document.importNode(svg, true);
    this.svg.removeAttribute('x'); this.svg.removeAttribute('y');
    // A semantic render starts with the renderer's original box. Keep the same
    // world point under the camera before saved canvas bounds are projected.
    if (!this.autoFit && priorOrigin) {
      this.x += (this.svg.viewBox.baseVal.x-priorOrigin.x)*this.z;
      this.y += (this.svg.viewBox.baseVal.y-priorOrigin.y)*this.z;
    }
    this.index = indexHits(this.svg, this.input, this.pristine.backend === 'managed');
    const visited = new Set();
    this.objects = [...this.index.hits].filter(([el, hit]) => {
      // Lifelines and tails refer to the same participant as its header.
      if (el.matches('.participant-lifeline,.participant-tail')) return false;
      const key = hit.token ? 'token:' + hit.token.start : hit.field ? 'field:' + hit.field.start : hit.id ? 'id:' + hit.id : 'line:' + hit.line;
      if (visited.has(key)) return false;
      visited.add(key); return true;
    }).map(([el]) => el);
    this.drag = null; this.linkStart = null; this.clearGuide();
    this.paper.replaceChildren(this.svg);
    this.canvas.attach();
    this.gpu.setScene(this.svg, this.version);
    this.paper.classList.remove('is-new');
    requestAnimationFrame(() => this.paper.classList.add('is-new'));
    this.placeholder.hidden = true;
    this.marked = null; this.selection.hidden = true;
    if (this.autoFit) this.fit(); else this.position();
  }
  rect() { return this.surface.getBoundingClientRect(); }
  fit() {
    if (!this.svg) return;
    const r = this.rect(), b = this.svg.viewBox.baseVal;
    if (!r.width || !r.height || !b.width || !b.height) return;
    this.autoFit = true;
    const inset = Math.min(40, r.width / 10, r.height / 10);
    this.z = Math.min(1.65, (r.width - inset * 2) / b.width, (r.height - inset * 2) / b.height);
    this.x = (r.width - b.width * this.z) / 2; this.y = (r.height - b.height * this.z) / 2;
    this.position(); this.emit('zoom', { zoom: this.z });
  }
  zoom(factor, px, py) {
    const r = this.rect(); px ??= r.width / 2; py ??= r.height / 2;
    const next = Math.max(.015, Math.min(12, this.z * factor));
    this.x = px - (px - this.x) * next / this.z; this.y = py - (py - this.y) * next / this.z;
    this.z = next; this.autoFit = false; this.position(); this.emit('zoom', { zoom: this.z });
  }
  position() {
    this.paper.style.transform = 'translate(' + this.x + 'px,' + this.y + 'px) scale(' + this.z + ')';
    this.outline(this.selection, this.marked);
    this.direct.positionField();
    this.direct.positionEnds();
    this.canvas?.position();
    this.gantt?.position();
    this.gpu?.setCamera();
  }
  outline(box, el) {
    const hidden = !el;
    if (box.hidden !== hidden) box.hidden = hidden;
    if (!el) return;
    const r = el.getBoundingClientRect(), s = this.rect();
    const bounds = { left:r.left - s.left - 3 + 'px', top:r.top - s.top - 3 + 'px', width:r.width + 6 + 'px', height:r.height + 6 + 'px' };
    for (const [property, value] of Object.entries(bounds))
      if (box.style[property] !== value) box.style[property] = value;
  }
  pick(el) { return el?.closest('[data-uml-hit]') || null; }
  select(id, line) {
    // Repeated sequence headers share a source identity. Keep the exact sheet
    // hit by the pointer when managed selection synchronizes that identity.
    const current = this.index?.hits.get(this.marked);
    if (!current || (id ? current.id !== id : current.line !== line))
      this.marked = (id && this.index?.byId.get(id)) || this.index?.byLine.get(line) || null;
    this.outline(this.selection, this.marked);
    this.canvas?.position();
    this.gantt?.position();
  }
  setAdditional(values, text, byId = false) {
    this.clipboard = text;
    this.additional?.forEach(el => el.classList.remove('multi-mark'));
    this.additional = values ? values.split(',').map(value => byId
      ? this.index?.byId.get(decodeURIComponent(value)) : this.index?.byLine.get(Number(value))).filter(Boolean) : [];
    this.additional.forEach(el => el.classList.add('multi-mark'));
    this.gpu?.positionSelections();
    this.canvas?.position();
    this.gantt?.position();
  }
  hit(el, kind='select') {
    const value = this.index?.hits.get(el) || empty;
    const owner = item => this.canvas?.scene?.nodeFor(item)?.id || this.canvas?.scene?.edgeFor(item)?.id;
    let visual;
    if (kind === 'toggle' && owner(el)) {
      const current = [...new Set([this.marked,...this.additional||[]])].filter(Boolean);
      const exists = current.some(item=>owner(item)===owner(el));
      visual = exists ? current.filter(item=>owner(item)!==owner(el)) : [el,...current];
    }
    this.marked = el; this.outline(this.selection, el);
    this.announcement.textContent = value.label || value.id || (value.line >= 0 ? 'Source line ' + (value.line + 1) : 'Selection cleared');
    this.emit('hit', { action:kind, ...value });
    if (visual) this.selectVisual(visual);
    this.canvas?.position();
    this.gantt?.position();
    return value;
  }
  region(hits, add=false, exactVisual=false) {
    // DOT marquee selection owns nodes; their internal edges participate in
    // source operations automatically. Explicit canvas selections stay exact.
    if(!exactVisual && hits.some(hit=>hit.dot)) hits=hits.filter(hit=>hit.dot?.kind==='node'&&!hit.field);
    const requested = new Set(hits);
    const elements = [...this.index.hits].filter(([,hit])=>requested.has(hit)).map(([el])=>el);
    const geometric = elements.length > 0 && elements.every(el=>this.canvas?.scene?.nodeFor(el) || this.canvas?.scene?.edgeFor(el));
    const visual = geometric ? [...new Set([...(add?[this.marked,...this.additional||[]]:[]),...elements])].filter(Boolean) : null;
    this.emit('region',{hits,add});
    // Source constructs can own several independent visual junctions. Source
    // selection drives the inspector; canvas selection retains those junctions.
    if (visual) this.selectVisual(visual);
  }
  selectVisual(elements) {
    this.additional?.forEach(el=>el.classList.remove('multi-mark'));
    this.marked=elements[0]||null;this.additional=elements.slice(1);
    this.additional.forEach(el=>el.classList.add('multi-mark'));
    this.gpu?.positionSelections();
    this.outline(this.selection,this.marked);this.canvas?.position();
  }
  direction(el, x, y, drag) {
    const r = el.getBoundingClientRect();
    let dx = (x - r.left) / Math.max(1, r.width) - .5;
    let dy = (y - r.top) / Math.max(1, r.height) - .5;
    // When dropping precisely at center, preserve the side of approach.
    if (Math.max(Math.abs(dx), Math.abs(dy)) < .05) { dx = drag.x - x; dy = drag.y - y; }
    return Math.abs(dx) >= Math.abs(dy) ? (dx < 0 ? 'left' : 'right') : (dy < 0 ? 'up' : 'down');
  }
  setTool(tool) { this.canvas.cancel(); this.gantt.cancel(); this.surface.dataset.tool = tool; this.gantt.position(); this.linkStart = null; this.clearGuide(); }
  clearGuide() { this.guide.setAttribute('hidden',''); this.target.hidden = true; this.direct.box.hidden = true; }
  bindGestures() {
    const s = this.surface, pointers = this.pointers;
    s.addEventListener('keydown',e=>{if(e.code==='Space'&&!e.target.closest('input,textarea,[contenteditable]')){this.spacePan=true;e.preventDefault();}});
    s.addEventListener('keyup',e=>{if(e.code==='Space')this.spacePan=false;});
    s.addEventListener('blur',()=>{this.spacePan=false;});
    const distance = () => { const [a,b] = pointers.values(); return Math.hypot(b.x-a.x,b.y-a.y); };
    for (const kind of ['copy','cut','paste']) s.addEventListener(kind, event => {
      if (!event.clipboardData) return;
      if (kind === 'paste') {
        const text = event.clipboardData.getData('text/plain');
        if (!text) return;
        this.emit('command', {action: 'paste', text});
      } else {
        if (!this.clipboard) return;
        event.clipboardData.setData('text/plain', this.clipboard);
        if (kind === 'cut') this.emit('command', {action: 'cut', text: ''});
      }
      event.preventDefault();
      event.stopPropagation();
    });
    s.addEventListener('keydown', event => {
      if (event.defaultPrevented || event.isComposing || event.keyCode === 229 ||
          event.target.closest('input,textarea,select,[contenteditable]')) return;
      let action;
      if (event.key === 'Delete' || event.key === 'Backspace') action = 'delete';
      if ((event.ctrlKey || event.metaKey) && !event.altKey) {
        const key = event.key.toLowerCase();
        if (key === 'd') action = 'duplicate';
        if (key === 'g') action = event.shiftKey ? 'ungroup' : 'group';
        // The HTML canvas has its own focus owner. Native XAML focus can still
        // identify the source TextBox, whose history must not consume this key.
        if (key === 'z') action = event.shiftKey ? 'redo' : 'undo';
        if (key === 'y' && !event.shiftKey) action = 'redo';
      }
      if (action) {
        event.preventDefault(); event.stopPropagation();
        this.emit('command', {action, text: ''});
      }
    });
    s.addEventListener('dragover', e => {
      if (e.dataTransfer.types.includes('text/plain')) { e.preventDefault(); e.dataTransfer.dropEffect = 'copy'; }
    });
    s.addEventListener('drop', e => {
      const text = e.dataTransfer.getData('text/plain');
      if (text.startsWith('unouml:tool:')) {
        e.preventDefault(); e.stopPropagation();
        const p=this.canvas.scene?.world(e.clientX,e.clientY);
        this.emit('drop', {text,x:p?.x,y:p?.y,revision:this.canvas.revision});
      }
    });
    s.addEventListener('click', e => e.preventDefault());
    s.addEventListener('dblclick', e => { if (this.touchEditTime && performance.now() - this.touchEditTime < 400) return;
      const el = this.pick(this.host.shadowRoot.elementFromPoint(e.clientX, e.clientY)); if (el) this.hit(el,'edit'); });
    s.addEventListener('keydown', e => {
      if ((e.ctrlKey || e.metaKey) && !e.key.startsWith('Arrow') || e.altKey) return;
      if (e.key.startsWith('Arrow') || e.key === 'Home' || e.key === 'End') {
        if (!this.objects?.length) return;
        const backwards = e.key === 'ArrowLeft' || e.key === 'ArrowUp';
        const i = this.objects.indexOf(this.marked), n = this.objects.length;
        const index = e.key === 'Home' ? 0 : e.key === 'End' ? n - 1 :
          i < 0 ? (backwards ? n - 1 : 0) : (i + (backwards ? n - 1 : 1)) % n;
        const el = this.objects[index]; this.hit(el);
        const item = el.getBoundingClientRect(), r = this.rect();
        if (item.right < r.left || item.left > r.right || item.bottom < r.top || item.top > r.bottom) {
          this.autoFit = false;
          this.x += (r.left + r.right - item.left - item.right) / 2;
          this.y += (r.top + r.bottom - item.top - item.bottom) / 2; this.position();
        }
        e.preventDefault(); return;
      }
      if ((e.key === 'Enter' || e.key === 'F2') && this.marked) {
        if (this.linkStart && e.key === 'Enter') {
          this.emit('gesture', {action:this.linkStart.action, from:this.linkStart.hit, to:this.index.hits.get(this.marked)});
          this.linkStart = null;
        } else this.hit(this.marked, 'edit');
        e.preventDefault(); e.stopPropagation(); return;
      }
      if (e.key === 'Escape') { this.linkStart = null; this.clearGuide(); this.hit(null); }
      else if (e.key === '+' || e.key === '=') this.zoom(1.2);
      else if (e.key === '-') this.zoom(1 / 1.2);
      else if (e.key === '0') this.fit();
      else return;
      e.preventDefault();
    });
    s.addEventListener('wheel', e => {
      e.preventDefault();
      if (e.ctrlKey || e.metaKey) { const b = this.rect(); this.zoom(Math.exp(-e.deltaY*.012), e.clientX-b.left,e.clientY-b.top); }
      else { this.autoFit=false; const f=e.deltaMode===1 ? 16 : e.deltaMode===2 ? this.rect().height : 1;
        this.x -= (e.shiftKey ? e.deltaY : e.deltaX)*f; this.y -= (e.shiftKey ? e.deltaX : e.deltaY)*f; this.position(); }
    }, { passive:false });
    s.addEventListener('pointerdown', e => {
      if (e.button !== 0 && e.button !== 1) return;
      const port = e.target.dataset?.wire;
      const el = port ? this.marked : this.pick(e.target);
      s.focus({preventScroll:true}); s.setPointerCapture(e.pointerId);
      pointers.set(e.pointerId,{x:e.clientX,y:e.clientY});
      if (pointers.size === 2) { this.canvas.cancel(); this.gantt.cancel(); this.drag=null; this.pinch=distance(); this.clearGuide(); return; }
      let tool = this.spacePan ? 'pan' : s.dataset.tool;
      if (!port && this.gantt.pointerDown(e,el,tool)) { this.drag=null; return; }
      if (!port && this.canvas.pointerDown(e,el,tool)) { this.drag=null; return; }
      if (e.button === 1) tool = 'pan';
      else if (port) tool = port;
      else if (this.linkStart && el) tool = this.linkStart.action;
      else if (!el && e.shiftKey && tool === 'select') tool = 'marquee';
      else if (!el && tool !== 'marquee') tool = 'pan';
      this.drag = { el, tool, port, add:e.shiftKey || e.ctrlKey || e.metaKey, id:e.pointerId, x:e.clientX, y:e.clientY, ox:this.x, oy:this.y, moved:false };
    });
    s.addEventListener('pointermove', e => {
      if (!pointers.has(e.pointerId)) return;
      pointers.set(e.pointerId,{x:e.clientX,y:e.clientY});
      if (pointers.size === 2) {
        const next=distance(), [a,b]=pointers.values(), r=this.rect();
        if (this.pinch>0 && next>0) this.zoom(next / this.pinch,(a.x+b.x)/2-r.left,(a.y+b.y)/2-r.top);
        this.pinch=next; return;
      }
      if (this.gantt.pointerMove(e) || this.canvas.pointerMove(e)) return;
      const drag=this.drag; if (!drag || drag.id!==e.pointerId) return;
      const dx=e.clientX-drag.x, dy=e.clientY-drag.y;
      drag.moved ||= Math.hypot(dx,dy)>4;
      if (!drag.moved) return;
      if (drag.tool==='marquee') { this.direct.marquee(drag, e.clientX, e.clientY); return; }
      if (drag.tool==='pan') { this.autoFit=false; this.x=drag.ox+dx; this.y=drag.oy+dy; s.dataset.panning=''; this.position(); return; }
      if (drag.port || drag.tool === 'connect' || drag.tool === 'move' || drag.tool === 'place') {
        const r=this.rect(); this.guide.removeAttribute('hidden');
        for (const [name,value] of [['x1',drag.x-r.left],['y1',drag.y-r.top],['x2',e.clientX-r.left],['y2',e.clientY-r.top]]) this.line.setAttribute(name,value);
        const target = this.pick(this.host.shadowRoot.elementFromPoint(e.clientX,e.clientY));
        this.outline(this.target, target);
        this.target.dataset.side = target && drag.tool === 'place' ? this.direction(target,e.clientX,e.clientY,drag) : '';
      }
    });
    const finish = (e, cancelled) => {
      if (!pointers.has(e.pointerId)) return;
      pointers.delete(e.pointerId);
      if (this.gantt.pointerUp(e,cancelled)) { this.drag=null; this.clearGuide(); return; }
      if (this.canvas.pointerUp(e,cancelled)) { this.drag=null; this.clearGuide(); return; }
      const d=this.drag; this.drag=null; delete s.dataset.panning; this.clearGuide();
      if (!d || cancelled || d.id!==e.pointerId) return;
      const el=this.pick(this.host.shadowRoot.elementFromPoint(e.clientX,e.clientY));
      if (d.tool === 'marquee' && d.moved) { this.direct.selectRegion(d,e.clientX,e.clientY); return; }
      if (d.port && !d.moved) { this.direct.startConnection(d.port); return; }
      if (d.moved) {
        this.linkStart = null;
        if (d.el && el && el!==d.el && (d.port || d.tool==='connect' || d.tool==='move' || d.tool==='place')) {
          this.emit('gesture', { action:d.tool==='move' ? 'moveBefore' : d.tool,
            from:this.index.hits.get(d.el), to:this.index.hits.get(el), direction:this.direction(el,e.clientX,e.clientY,d) });
        }
      } else if ((d.tool==='connect' || d.tool==='retargetFrom' || d.tool==='retargetTo') && el) {
        const to=this.hit(el);
        if (this.linkStart) { this.emit('gesture',{action:this.linkStart.action,from:this.linkStart.hit,to}); this.linkStart=null; }
        else this.linkStart={action:'connect',hit:to};
      } else {
        this.linkStart=null;
        // A short second tap on the same object is an edit gesture on touch
        // hardware even when the browser does not synthesize a dblclick.
        if (e.pointerType === 'touch' && d.el && d.tool === 'select') {
          const now = performance.now();
          const doubleTap = this.tap?.el === d.el && now - this.tap.at < 400;
          this.tap = doubleTap ? null : {el:d.el, at:now};
          if (doubleTap) { this.touchEditTime = now; this.hit(d.el, 'edit'); return; }
        }
        this.hit(d.el, d.add ? 'toggle' : 'select');
      }
    };
    s.addEventListener('pointerup',e=>finish(e,false));
    s.addEventListener('pointercancel',e=>finish(e,true));
    s.addEventListener('lostpointercapture',e=>finish(e,true));
  }
}
export function mount(id, emit, renderManaged, layoutManaged) {
  const host=document.getElementById(id);
  if (!host) throw new Error('Uno canvas host is unavailable.');
  views.get(id)?.dispose(); views.set(id,new Viewport(host,emit,renderManaged,layoutManaged));
}
export function unmount(id) { views.get(id)?.dispose(); views.delete(id); }
export function setTool(id, value) { views.get(id)?.setTool(value); }
export function setSelection(id, name, line) { views.get(id)?.select(name,line); }
export function zoom(id, factor) { views.get(id)?.zoom(factor); }
export function fit(id) { views.get(id)?.fit(); }
export async function render(id, source, fileName = '') { await views.get(id).render(source, fileName); }
export async function renderSourceJson(id, sourceJson, fileName = '') { await render(id, JSON.parse(sourceJson), fileName); }
export async function exportDiagram(id, format, fileName) {
  const view = views.get(id);
  const svg = view?.pristine && view.svg;
  if (!svg) throw new Error('Render a diagram before exporting.');
  if (view.sheetPicker.selectedIndex > 0) fileName = fileName.replace(/(\.[^.]+)?$/, (_, ext) => '-sheet-' + view.sheetPicker.selectedIndex + (ext || ''));
  const clean = svg.cloneNode(true);
  clean.removeAttribute('x'); clean.removeAttribute('y');
  // Export the projected drawing, without runtime selection and gesture markers.
  // Keep renderer source identities so the image remains inspectable downstream.
  for (const el of clean.querySelectorAll('[data-uml-hit],[data-canvas-node],[data-canvas-edge],.multi-mark,.canvas-locked,.canvas-pinned,.canvas-collapsed')) {
    for (const name of ['data-uml-hit','data-canvas-node','data-canvas-edge']) el.removeAttribute(name);
    el.classList.remove('multi-mark','canvas-locked','canvas-pinned','canvas-collapsed');
  }
  const file = new Blob([new XMLSerializer().serializeToString(clean)],{type:'image/svg+xml;charset=utf-8'});
  if (format === 'svg') { download(fileName, file); return; }
  if (format === 'print') {
    const frame=document.createElement('iframe');
    frame.style.cssText='position:fixed;left:-10000px;top:0;border:0'; document.body.append(frame);
    const doc=frame.contentDocument; doc.title=fileName;
    const style=doc.createElement('style'); style.textContent='@page { margin: 10mm } body {margin:0} svg{display:block;width:100%;height:auto;max-height:95vh}';
    doc.head.append(style);
    const sheets = view.sheetPicker.selectedIndex > 0 || !view.sheets.length ? [clean] : view.sheets;
    for (const [i, source] of sheets.entries()) {
      const page = doc.createElement('section');
      if (i > 0) page.style.breakBefore = 'page';
      const printSvg = doc.importNode(source,true);
      printSvg.removeAttribute('x'); printSvg.removeAttribute('y');
      page.append(printSvg); doc.body.append(page);
    }
    frame.contentWindow.addEventListener('afterprint',()=>frame.remove(),{once:true});
    frame.contentWindow.focus(); frame.contentWindow.print(); return;
  }
  if (format !== 'png') throw new Error('Unknown export format.');
  const image = new Image(), url=URL.createObjectURL(file);
  try {
    image.src=url; await image.decode();
    const scale=Math.min(2,8192/image.naturalWidth,8192/image.naturalHeight,Math.sqrt(24_000_000/(image.naturalWidth*image.naturalHeight)));
    const canvas=document.createElement('canvas');
    canvas.width=Math.max(1,Math.round(image.naturalWidth*scale)); canvas.height=Math.max(1,Math.round(image.naturalHeight*scale));
    canvas.getContext('2d').drawImage(image,0,0,canvas.width,canvas.height);
    const png=await new Promise(resolve=>canvas.toBlob(resolve,'image/png'));
    if (!png) throw new Error('Could not encode this diagram as PNG.');
    download(fileName,png);
  } finally { URL.revokeObjectURL(url); }
}

export function setAdditionalSelection(id, lines, clipboard) { views.get(id)?.setAdditional(lines, clipboard); }
export function setAdditionalDotSelection(id, names, clipboard) { views.get(id)?.setAdditional(names, clipboard, true); }

export function beginLabelEdit(id, label, revision, multiline = false, language = 'text') { return views.get(id)?.direct.beginLabel(label, revision, multiline, language) ?? false; }
export function setConnectable(id, value) { const v = views.get(id); if (v) v.direct.port.hidden = !value; }

export function setLocalMode(id, nativeEnabled) { const view = views.get(id); if (view) view.localMode = nativeEnabled ? 'auto' : 'reference'; }

export function setEndpoints(id, from, to) { views.get(id)?.direct.setEndpoints(from, to); }

export function selectJsonToken(id, start) { const v = views.get(id), el = v?.index?.byToken.get(start); if (el) { v.marked = el; v.outline(v.selection, el); } }

export function setCanvasState(id, json, revision) { views.get(id)?.canvas.load(json,revision); }
export function setGanttAuthoring(id, enabled) { views.get(id)?.gantt.configure(enabled); }
export function getCanvasState(id) { return views.get(id)?.canvas.info() || '{}'; }
export function canvasAction(id, action, options) { views.get(id)?.canvas.action(action,options?JSON.parse(options):{}); }
export function canvasCommitResult(id,accepted) { const c=views.get(id)?.canvas;if(c?.pendingCommit)c.pendingCommit.accepted=accepted; }


// Diagnostics expose the actual accepted presentation, never a requested preference.
export function rendererState(id) { return views.get(id)?.gpu.state() ?? null; }
export function setGpuAvailability(id, available, reason) { views.get(id)?.gpu.setAvailability(available, reason); }
export function presentGpuFrame(id, revision, statisticsJson) {
  const view = views.get(id);
  if (!view) return false;
  let statistics;
  try { statistics = JSON.parse(statisticsJson); }
  catch { view.gpu.fail(new Error('Invalid GPU frame statistics.')); return false; }
  return view.gpu.present(revision, statistics);
}

export function rejectGpuScene(id, reason) { views.get(id)?.gpu.rejectScene(reason); }
