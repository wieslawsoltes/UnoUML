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
  const labels = new Map(), repeated = new Set(), macroAliases = new Map();
  // Text-only diagrams may omit all semantic groups. Only associate a label
  // with a statement if its source line is unambiguous.
  const addLabel = (name, i) => {
    if (!name) return;
    if (labels.has(name) && labels.get(name) !== i) repeated.add(name);
    else labels.set(name, i);
  };
  const previousClose = new Int32Array(lines.length);
  let lastClose = -1;
  for (const [i, line] of lines.entries()) {
    previousClose[i] = lastClose;
    if (/^\s*\}/.test(line)) lastClose = i;
    const t = line.trim();
    if (!t || /^['!@]/.test(t)) continue;
    const quoted = [...t.matchAll(/"([^"]+)"/g)];
    for (const m of quoted) addLabel(m[1], i);
    addLabel(t.replace(/^(?:[*+#-]+\s+|:)\s*/, '').replace(/;$/, ''), i);
    // PlantUML renders class visibility and member modifiers as separate
    // glyphs, while the label itself only contains the signature.
    addLabel(t.replace(/^(?:[+#~-]\s*)?(?:\{(?:static|abstract|field|method)\}\s*)?/i, ''), i);
    const tail = t.indexOf(':');
    if (tail >= 0) addLabel(t.slice(tail + 1).trim().replace(/;$/, ''), i);
    const macro = /^\w+\(\s*([\w.$]+)\s*,/.exec(t);
    if (macro) macroAliases.set(macro[1], macroAliases.has(macro[1]) ? -1 : i);
  }
  const hits = new Map(), byId = new Map(), byLine = new Map();
  const inferred = label => repeated.has(label) ? -1 : labels.get(label) ?? -1;
  function register(el, id, line, label) {
    el.setAttribute('data-uml-hit', '');
    const hit = { id, line, label };
    hits.set(el, hit);
    // Use the visible header as the representative of a sequence participant.
    // The engine emits lifelines first and may repeat the same source line on
    // participants declared implicitly by a message.
    if (id && (!byId.has(id) || el.classList.contains('participant-head'))) byId.set(id, el);
    if (line >= 0 && (!byLine.has(line) || el.matches('.participant-head, .message'))) byLine.set(line, el);
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
    if ((macroAliases.get(id) ?? -1) >= 0) line = macroAliases.get(id);
    if (line < 0) line = inferred(label);
    if (id || line >= 0) register(el, id, line, label);
  }
  for (const el of svg.querySelectorAll('text')) {
    const label = el.textContent.trim(), line = inferred(label);
    if (line < 0) continue;
    const parent = el.closest('[data-uml-hit]');
    if (parent) {
      const start = hits.get(parent)?.line ?? -1;
      if (!parent.classList.contains('entity') || start < 0 || line <= start || !/\{\s*$/.test(lines[start])) continue;
      // Do not steal a click from this entity for a same-named label outside
      // its body, including in another class.
      if (previousClose[line] > start) continue;
    }
    register(el, '', line, label);
  }
  return { hits, byId, byLine };
}
