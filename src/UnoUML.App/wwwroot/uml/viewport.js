import { indexHits, parseSvg } from './svg.js';
import { download, includeFiles, includeRevision } from './files.js';
export { openFile, saveFile, importIncludes } from './files.js';

import { getServerEndpoint, layoutOnServer } from './server.js';
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
  constructor(host, emit) {
    this.host = host;
    this.emit = (kind, value) => emit(JSON.stringify({ kind, ...value }));
    this.z = 1; this.x = 0; this.y = 0; this.autoFit = true;
    this.version = 0; this.cache = new Map(); this.budget = 0; this.pointers = new Map();
    const root = host.shadowRoot || host.attachShadow({ mode: 'open' });
    root.replaceChildren();
    const css = html('link', '', root);
    css.rel = 'stylesheet'; css.href = new URL('preview.css', import.meta.url);
    this.surface = html('div', 'surface', root);
    this.surface.tabIndex = 0; this.surface.dataset.tool = 'select';
    this.surface.setAttribute('role', 'region');
    this.surface.setAttribute('aria-label', 'Diagram canvas. Drag the background to pan. Control-wheel or pinch to zoom.');
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
    this.bindGestures();
    this.resize = new ResizeObserver(() => this.autoFit ? this.fit() : this.position());
    this.resize.observe(host);
  }
  dispose() { this.resize.disconnect(); this.version++; this.host.shadowRoot.replaceChildren(); this.cache.clear(); }
  async render(source) {
    const version = ++this.version;
    this.fetchJob?.abort();
    this.pristine = null;
    const endpoint = getServerEndpoint();
    const files = endpoint ? null : await includeFiles(), key = endpoint + '\0' + includeRevision + '\0' + source;
    if (version !== this.version) return;
    let svg = this.cache.get(key);
    if (!svg) {
      const controller = endpoint ? new AbortController() : null;
      this.fetchJob = controller;
      const layout = endpoint ? s => layoutOnServer(endpoint, s, AbortSignal.any([controller.signal, AbortSignal.timeout(45000)])) : await loadEngine();
      // Serialize the shared TeaVM renderer while dropping superseded requests
      // before they reach expensive graph layout.
      while (this.active) await this.active.catch(() => {});
      if (version !== this.version) return;
      const job = Promise.resolve().then(() => layout(source, files));
      this.active = job;
      let output;
      try { output = await job; } finally { if (this.active === job) this.active = null; }
      if (version !== this.version) return;
      svg = parseSvg(output);
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
    this.svg = document.importNode(svg, true);
    this.index = indexHits(this.svg, source);
    this.drag = null; this.linkStart = null; this.clearGuide();
    this.paper.replaceChildren(this.svg);
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
  }
  outline(box, el) {
    box.hidden = !el;
    if (!el) return;
    const r = el.getBoundingClientRect(), s = this.rect();
    Object.assign(box.style,{ left:r.left - s.left - 3 + 'px', top:r.top - s.top - 3 + 'px', width:r.width + 6 + 'px', height:r.height + 6 + 'px' });
  }
  pick(el) { return el?.closest('[data-uml-hit]') || null; }
  select(id, line) {
    this.marked = (id && this.index?.byId.get(id)) || this.index?.byLine.get(line) || null;
    this.outline(this.selection, this.marked);
  }
  hit(el, kind='select') {
    const value = this.index?.hits.get(el) || empty;
    this.marked = el; this.outline(this.selection, el);
    this.emit('hit', { action:kind, ...value });
    return value;
  }
  setTool(tool) { this.surface.dataset.tool = tool; this.linkStart = null; this.clearGuide(); }
  clearGuide() { this.guide.setAttribute('hidden',''); this.target.hidden = true; }
  bindGestures() {
    const s = this.surface, pointers = this.pointers;
    const distance = () => { const [a,b] = pointers.values(); return Math.hypot(b.x-a.x,b.y-a.y); };
    s.addEventListener('dragover', e => {
      if (e.dataTransfer.types.includes('text/plain')) { e.preventDefault(); e.dataTransfer.dropEffect = 'copy'; }
    });
    s.addEventListener('drop', e => {
      const text = e.dataTransfer.getData('text/plain');
      if (text.startsWith('unouml:tool:')) { e.preventDefault(); e.stopPropagation(); this.emit('drop', { text }); }
    });
    s.addEventListener('click', e => e.preventDefault());
    s.addEventListener('dblclick', e => { const el = this.pick(this.host.shadowRoot.elementFromPoint(e.clientX, e.clientY)); if (el) this.hit(el,'edit'); });
    s.addEventListener('keydown', e => {
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
      const el = this.pick(e.target);
      s.focus({preventScroll:true}); s.setPointerCapture(e.pointerId);
      pointers.set(e.pointerId,{x:e.clientX,y:e.clientY});
      if (pointers.size === 2) { this.drag=null; this.pinch=distance(); this.clearGuide(); return; }
      let tool = s.dataset.tool;
      if (e.button===1 || !el) tool='pan';
      this.drag = { el, tool, id:e.pointerId, x:e.clientX, y:e.clientY, ox:this.x, oy:this.y, moved:false };
    });
    s.addEventListener('pointermove', e => {
      if (!pointers.has(e.pointerId)) return;
      pointers.set(e.pointerId,{x:e.clientX,y:e.clientY});
      if (pointers.size === 2) {
        const next=distance(), [a,b]=pointers.values(), r=this.rect();
        if (this.pinch>0 && next>0) this.zoom(next / this.pinch,(a.x+b.x)/2-r.left,(a.y+b.y)/2-r.top);
        this.pinch=next; return;
      }
      const drag=this.drag; if (!drag || drag.id!==e.pointerId) return;
      const dx=e.clientX-drag.x, dy=e.clientY-drag.y;
      drag.moved ||= Math.hypot(dx,dy)>4;
      if (!drag.moved) return;
      if (drag.tool==='pan') { this.autoFit=false; this.x=drag.ox+dx; this.y=drag.oy+dy; s.dataset.panning=''; this.position(); return; }
      if (drag.tool === 'connect' || drag.tool === 'move') {
        const r=this.rect(); this.guide.removeAttribute('hidden');
        for (const [name,value] of [['x1',drag.x-r.left],['y1',drag.y-r.top],['x2',e.clientX-r.left],['y2',e.clientY-r.top]]) this.line.setAttribute(name,value);
        this.outline(this.target,this.pick(this.host.shadowRoot.elementFromPoint(e.clientX,e.clientY)));
      }
    });
    const finish = (e, cancelled) => {
      pointers.delete(e.pointerId);
      const d=this.drag; this.drag=null; delete s.dataset.panning; this.clearGuide();
      if (!d || cancelled || d.id!==e.pointerId) return;
      const el=this.pick(this.host.shadowRoot.elementFromPoint(e.clientX,e.clientY));
      if (d.moved) {
        if (d.el && el && el!==d.el && (d.tool==='connect' || d.tool==='move')) {
          this.emit('gesture',{action:d.tool==='move'?'moveBefore':'connect',from:this.index.hits.get(d.el),to:this.index.hits.get(el)});
        }
      } else if (d.tool==='connect' && el) {
        const to=this.hit(el);
        if (this.linkStart) { this.emit('gesture',{action:'connect',from:this.linkStart,to}); this.linkStart=null; }
        else this.linkStart=to;
      } else { this.linkStart=null; this.hit(d.el); }
    };
    s.addEventListener('pointerup',e=>finish(e,false));
    s.addEventListener('pointercancel',e=>finish(e,true));
  }
}
export function mount(id, emit) {
  const host=document.getElementById(id);
  if (!host) throw new Error('Uno canvas host is unavailable.');
  views.get(id)?.dispose(); views.set(id,new Viewport(host,emit));
}
export function unmount(id) { views.get(id)?.dispose(); views.delete(id); }
export function setTool(id, value) { views.get(id)?.setTool(value); }
export function setSelection(id, name, line) { views.get(id)?.select(name,line); }
export function zoom(id, factor) { views.get(id)?.zoom(factor); }
export function fit(id) { views.get(id)?.fit(); }
export async function render(id, source) { await views.get(id).render(source); }
export async function exportDiagram(id, format, fileName) {
  const svg = views.get(id)?.pristine;
  if (!svg) throw new Error('Render a diagram before exporting.');
  const file = new Blob([new XMLSerializer().serializeToString(svg)],{type:'image/svg+xml;charset=utf-8'});
  if (format === 'svg') { download(fileName, file); return; }
  if (format === 'print') {
    const frame=document.createElement('iframe');
    frame.style.cssText='position:fixed;left:-10000px;top:0;border:0'; document.body.append(frame);
    const doc=frame.contentDocument; doc.title=fileName;
    const style=doc.createElement('style'); style.textContent='@page { margin: 10mm } body {margin:0} svg{display:block;width:100%;height:auto;max-height:95vh}';
    doc.head.append(style); doc.body.append(doc.importNode(svg,true));
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
