const NS = 'http://www.w3.org/2000/svg';
const elements = new Set('svg g a defs desc title image path polygon polyline circle ellipse rect line text tspan linearGradient radialGradient stop pattern marker use symbol clipPath mask filter feGaussianBlur feOffset feMerge feMergeNode feColorMatrix feBlend feFlood feComposite feDropShadow'.split(' '));
const attributes = new Set('id class x y x1 x2 y1 y2 cx cy r rx ry dx dy d points width height viewBox preserveAspectRatio transform gradientTransform gradientUnits offset stop-color stop-opacity opacity fill fill-opacity fill-rule stroke stroke-opacity stroke-width stroke-linecap stroke-linejoin stroke-dasharray stroke-dashoffset stroke-miterlimit font-family font-size font-weight font-style text-anchor dominant-baseline textLength lengthAdjust text-decoration letter-spacing vector-effect filter clip-path clip-rule mask marker-start marker-mid marker-end refX refY orient markerWidth markerHeight markerUnits patternUnits patternTransform patternContentUnits clipPathUnits maskUnits filterUnits color-interpolation-filters stdDeviation in in2 result mode flood-color flood-opacity values type operator k1 k2 k3 k4 xml:space'.split(' '));
const localUrl = /^url\(\s*["']?#[\w.-]+["']?\s*\)$/i;
const allowedStyle = /^(?:white-space|fill|stroke|stroke-width|font-family|font-size|font-weight|font-style|text-decoration|background-color|opacity|color)$/;
export function parseSvg(text) {
  const xml = new DOMParser().parseFromString(text, 'image/svg+xml');
  const svg = xml.documentElement;
  if (svg.localName !== 'svg' || svg.namespaceURI !== NS) throw new Error('PlantUML returned invalid SVG.');
  for (const el of [svg, ...svg.querySelectorAll('*')]) {
    if (el.namespaceURI !== NS || !elements.has(el.localName)) { el.remove(); continue; }
    for (const a of [...el.attributes]) {
      const n = a.name, value = a.value;
      if (n === 'xmlns' || n === 'xmlns:xlink' || /^data-[a-z0-9-]+$/.test(n)) continue;
      if (a.localName === 'href') {
        // Links are inert while editing. Local symbols and embedded raster sprites
        // are retained; arbitrary remote image references must never fetch on open.
        if (el.localName === 'use' && /^#[\w.-]+$/.test(value)) continue;
        if (el.localName === 'image' && /^data:image\/(png|jpe?g|gif|webp);base64,[a-z0-9+/=\s]+$/i.test(value)) continue;
        if (el.localName === 'a' && /^https?:\/\//i.test(value)) continue;
      } else if (n === 'style') {
        const safe = value.split(';').filter(s => {
          const i = s.indexOf(':');
          return i > 0 && allowedStyle.test(s.slice(0, i).trim()) && !/[()@\\]/.test(s.slice(i + 1));
        }).join(';');
        el.setAttribute(n, safe);
        continue;
      } else if (attributes.has(n) && !/[\\@]/.test(value) && (!/url\s*\(/i.test(value) || localUrl.test(value))) continue;
      el.removeAttributeNode(a);
    }
  }
  const first = svg.querySelector('text')?.textContent || '';
  if (/^PlantUML version/i.test(first)) {
    const lines = [...svg.querySelectorAll('text')].map(x => x.textContent.trim());
    const location = lines.find(x => /^\[From .+\(line \d+\)/i.test(x));
    if (location) throw new Error(location.replace(/^\[|\s*\]$/g, '') + ' — ' + lines.at(-1));
  }
  return svg;
}

export function indexHits(svg, source) {
  const lines = source.split(/\r\n|\r|\n/);
  const labels = new Map(), repeated = new Set();
  // Text-only diagrams may omit all semantic groups. Only associate a label
  // with a statement if its source line is unambiguous.
  const addLabel = (name, i) => {
    if (!name) return;
    if (labels.has(name) && labels.get(name) !== i) repeated.add(name);
    else labels.set(name, i);
  };
  for (const [i, line] of lines.entries()) {
    const t = line.trim();
    if (!t || /^['!@]/.test(t)) continue;
    const quoted = [...t.matchAll(/"([^"]+)"/g)];
    for (const m of quoted) addLabel(m[1], i);
    addLabel(t.replace(/^(?:[*+#-]+\s+|:)\s*/, '').replace(/;$/, ''), i);
    const tail = t.indexOf(':');
    if (tail >= 0) addLabel(t.slice(tail + 1).trim().replace(/;$/, ''), i);
    const macro = /^\w+\(\s*([\w.$]+)\s*,/.exec(t);
    if (macro) addLabel(macro[1], i);
  }
  const hits = new Map(), byId = new Map(), byLine = new Map();
  const inferred = label => repeated.has(label) ? -1 : labels.get(label) ?? -1;
  function register(el, id, line, label) {
    el.setAttribute('data-uml-hit', '');
    const hit = { id, line, label };
    hits.set(el, hit);
    if (id && !byId.has(id)) byId.set(id, el);
    if (line >= 0 && !byLine.has(line)) byLine.set(line, el);
  }
  for (const el of svg.querySelectorAll('[data-source-line],[data-qualified-name]')) {
    if (!el.children.length) continue;
    const id = el.getAttribute('data-qualified-name') || '';
    const label = el.querySelector('title')?.textContent || el.querySelector('text:last-of-type')?.textContent || id;
    const n = el.getAttribute('data-source-line');
    let line = n !== null && /^\d+$/.test(n) ? Number(n) : -1;
    if (line >= lines.length) line = -1;
    // Preprocessing replaces lines. Prefer an original macro invocation over
    // a coincidentally in-bounds expanded line number.
    if (id && inferred(id) >= 0) line = inferred(id);
    if (line < 0) line = inferred(label);
    if (id || line >= 0) register(el, id, line, label);
  }
  for (const el of svg.querySelectorAll('text')) {
    if (el.closest('[data-uml-hit]')) continue;
    const label = el.textContent.trim(), line = inferred(label);
    if (line >= 0) register(el, '', line, label);
  }
  return { hits, byId, byLine };
}
