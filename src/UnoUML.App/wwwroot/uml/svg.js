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

export function indexHits(svg, source, exactSourceMap = false) {
  const lines = exactSourceMap ? [] : source.split(/\r\n|\r|\n/);
  const labels = new Map(), repeated = new Set(), macroAliases = new Map(), c4Aliases = new Map();
  const c4Node = /^(?:Person(?:_Ext)?|(?:System|Container|Component)(?:Db|Queue)?(?:_Ext)?|(?:Enterprise|System|Container)_Boundary|Boundary|(?:Deployment_)?Node)$/;
  const c4Relation = /^(?:BiRel|Rel)(?:_(?:R|Right|L|Left|U|Up|D|Down|Neighbor|Back|Back_Neighbor))?$/;
  let staticC4 = true, dynamicC4 = false;
  let continuedUntil = -1;
  const c4Alias = text => {
    let quote = '', start = text.indexOf('(') + 1;
    const arguments_ = [];
    for (let i = start; i < text.length; i++) {
      const character = text[i];
      if (quote) { if (character === quote) quote = ''; continue; }
      if (character === '"' || character === "'") { quote = character; continue; }
      if (character !== ',' && character !== ')') continue;
      arguments_.push(text.slice(start, i).trim()); start = i + 1;
      if (character === ')') break;
    }
    const named = arguments_.find(argument => /^\$alias\s*=/.test(argument));
    const alias = named ? named.replace(/^\$alias\s*=\s*/, '') : arguments_[0];
    return /^[a-zA-Z_][\w.]*$/.test(alias || '') ? alias : '';
  };
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
    if (i <= continuedUntil) continue;
    let t = line.trim();
    const startingCall = /^(\w+)\s*\(/.exec(t);
    if (startingCall && (c4Node.test(startingCall[1]) || c4Relation.test(startingCall[1]))) {
      let last = i;
      while (t.endsWith('\\') && last + 1 < lines.length && last < i + 256) {
        t = t.slice(0, -1) + ' ' + lines[++last].trim();
      }
      continuedUntil = last;
    }
    if (t.startsWith('!') && !/^!include\s+<c4\/c4_(?:context|container|component|deployment)>\s*(?:'.*)?$/i.test(t)) {
      staticC4 = false;
      dynamicC4 = true;
    }
    if (!t || /^['!@]/.test(t)) continue;
    const quoted = [...t.matchAll(/"([^"]+)"/g)];
    for (const m of quoted) addLabel(m[1], i);
    addLabel(t.replace(/^(?:[*+#-]+\s+|:)\s*/, '').replace(/;$/, ''), i);
    // PlantUML renders class visibility and member modifiers as separate
    // glyphs, while the label itself only contains the signature.
    addLabel(t.replace(/^(?:[+#~-]\s*)?(?:\{(?:static|abstract|field|method)\}\s*)?/i, ''), i);
    const tail = t.indexOf(':');
    if (tail >= 0) addLabel(t.slice(tail + 1).trim().replace(/;$/, ''), i);
    const macro = /^(\w+)\s*\(\s*([\w.$]+)\s*,/.exec(t);
    const call = /^(\w+)\s*\(/.exec(t);
    // A mixed diagram can contain an ordinary node such as other.ctrl alongside
    // the global C4 alias ctrl. Suffixes are unique only within a pure C4 source.
    if (call ? !c4Node.test(call[1]) && !c4Relation.test(call[1]) :
        !/^}(?:\s*'.*)?$|^title\s|^LAYOUT_(?:LEFT_RIGHT|TOP_DOWN)\(\)\s*(?:'.*)?$/.test(t)) staticC4 = false;
    if (macro || call && c4Node.test(call[1])) {
      const name = call?.[1] || macro[1];
      const alias = c4Node.test(name) ? c4Alias(t) : macro[2];
      if (!alias) { staticC4 = false; continue; }
      if (!/^(?:BiRel|Rel)(?:_|$)/.test(name))
        macroAliases.set(alias, macroAliases.has(alias) ? -1 : i);
      if (c4Node.test(name)) {
        if (/^[a-zA-Z_]\w*$/.test(alias)) c4Aliases.set(alias, c4Aliases.has(alias) ? -1 : i);
        else staticC4 = false;
      }
    }
  }
  const hits = new Map(), byId = new Map(), byLine = new Map(), byToken = new Map();
  const inferred = label => repeated.has(label) ? -1 : labels.get(label) ?? -1;
  function register(el, id, line, label) {
    el.setAttribute('data-uml-hit', '');
    const hit = { id, line, label };
    if (exactSourceMap && el.closest('[data-source-generated="true"]')) hit.generated = true;
    const origin = el.closest('[data-source-file][data-file-line]');
    if (exactSourceMap && origin) {
      const fileLine = Number(origin.getAttribute('data-file-line'));
      if (Number.isSafeInteger(fileLine) && fileLine >= 0) {
        hit.sourceFile = origin.getAttribute('data-source-file'); hit.fileLine = fileLine;
        if (origin.hasAttribute('data-source-archive')) hit.sourceArchive = origin.getAttribute('data-source-archive');
      }
    }
    if (!exactSourceMap && dynamicC4 && c4Aliases.has(id.slice(id.lastIndexOf('.') + 1))) hit.generated = true;
    if (exactSourceMap && el.hasAttribute('data-json-start')) {
      const start = Number(el.getAttribute('data-json-start')), length = Number(el.getAttribute('data-json-length'));
      const kind = el.getAttribute('data-json-kind');
      if (Number.isSafeInteger(start) && Number.isSafeInteger(length) && start >= 0 && length > 0 &&
          start <= source.length - length && ['key','string','scalar'].includes(kind)) {
        hit.token = {start, length, kind};
        byToken.set(start, el);
      }
    }
    if (exactSourceMap && !hit.generated && el.hasAttribute('data-edit-start')) {
      const start = Number(el.getAttribute('data-edit-start')), length = Number(el.getAttribute('data-edit-length'));
      const kind = el.getAttribute('data-edit-kind'), role = el.getAttribute('data-edit-role') || 'label';
      if (Number.isSafeInteger(start) && Number.isSafeInteger(length) && start >= 0 && (length > 0 || length === 0 && ['yaml-string','salt-text'].includes(kind)) &&
          start <= source.length - length && kind && ['label','key','value','member'].includes(role)) {
        hit.field = {start,length,kind,role};
        if (kind === 'salt-state') {
          const checked = /x/i.test(source.slice(start,start + length));
          const caption = el.closest('.salt-widget')?.querySelector('text')?.textContent || 'control';
          hit.label = (checked ? 'Checked ' : 'Unchecked ') + caption;
        }
        byToken.set(start,el);
        if (el.localName === 'g') el.setAttribute('pointer-events', 'bounding-box');
      }
    }
    const dot = exactSourceMap && !hit.generated ? el.closest('[data-dot-kind]') : null;
    if (dot) {
      const value = name => dot.getAttribute('data-dot-' + name) || '';
      const integer = name => {
        const text = dot.getAttribute('data-dot-' + name), number = Number(text);
        return text !== null && Number.isSafeInteger(number) ? number : -1;
      };
      hit.dot = {id:value('id'),kind:value('kind'),name:dot.getAttribute('data-qualified-name') || '',
        text:value('label-text'),format:value('label-format') || 'text',
        start:integer('start'),length:integer('length'),nameStart:integer('name-start'),nameLength:integer('name-length'),
        labelStart:integer('label-start'),labelLength:integer('label-length'),labelInsert:integer('label-insert'),labelKind:value('label-kind'),
        explicit:value('explicit') === 'true',directed:svg.getAttribute('data-dot-directed') === 'true',merged:value('merged') === 'true',
        fromStart:integer('from-start'),fromLength:integer('from-length'),fromId:value('from-id'),
        toStart:integer('to-start'),toLength:integer('to-length'),toId:value('to-id'),
        arrowStart:integer('arrow-start'),arrowLength:integer('arrow-length')};
    }
    if (el.matches('.participant-lifeline,.participant-head,.participant-tail')) hit.participant = true;
    hits.set(el, hit);
    // Use the visible header as the representative of a sequence participant.
    // The engine emits lifelines first and may repeat the same source line on
    // participants declared implicitly by a message.
    if (id && (!byId.has(id) || el.classList.contains('participant-head'))) byId.set(id, el);
    if (line >= 0 && (!byLine.has(line) || el.matches('.participant-head, .message'))) byLine.set(line, el);
  }
  for (const el of svg.querySelectorAll('[data-source-line],[data-qualified-name],[data-edit-start],[data-canvas-id]')) {
    if (!el.children.length && !(exactSourceMap && (el.hasAttribute('data-json-start') || el.hasAttribute('data-edit-start')))) continue;
    // C4 aliases are global even inside visual boundaries. The renderer's
    // qualified names describe geometry while the source editor uses aliases.
    let id = (exactSourceMap && el.closest('[data-dot-id]')?.getAttribute('data-dot-id')) ||
      (exactSourceMap && el.getAttribute('data-c4-alias')) || el.getAttribute('data-qualified-name') || el.getAttribute('data-canvas-id') || '';
    const label = (el.localName === 'text' ? el.textContent : null) || el.querySelector('title')?.textContent || el.querySelector('text:last-of-type')?.textContent || id;
    const n = el.closest('[data-source-line]')?.getAttribute('data-source-line') ?? null;
    let line = n !== null && /^\d+$/.test(n) ? Number(n) : -1;
    if (!exactSourceMap && line >= lines.length) line = -1;
    // Preprocessing replaces lines. Prefer an original macro invocation over
    // a coincidentally in-bounds expanded line number.
    if ((macroAliases.get(id) ?? -1) >= 0 && (staticC4 || !c4Aliases.has(id))) line = macroAliases.get(id);
    if (!exactSourceMap && staticC4) {
      const alias = id.slice(id.lastIndexOf('.') + 1);
      if ((c4Aliases.get(alias) ?? -1) >= 0) {
        id = alias;
        line = c4Aliases.get(alias);
      }
    }
    if (line < 0) line = inferred(label);
    if (id || line >= 0 || exactSourceMap && el.hasAttribute('data-edit-start')) register(el, id, line, label);
  }
  if (exactSourceMap) return { hits, byId, byLine, byToken };
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
  return { hits, byId, byLine, byToken };
}
