import { renderToString } from './vendor/plantuml.js';

// No cross-origin renderer or source upload. All standard-library loads resolve
// below this directory; file includes are explicitly imported by the editor.
window.PLANTUML_STDLIB_BASE = new URL('./vendor/', import.meta.url).href;
window.PLANTUML_STDLIB_LOADER = (name, ok, fail) => {
  if (!/^[a-z0-9][a-z0-9.-]*\.js$/i.test(name)) { fail('Invalid library name'); return true; }
  const script = document.createElement('script');
  script.src = new URL(name, window.PLANTUML_STDLIB_BASE);
  script.onload = ok;
  script.onerror = () => fail('Library is unavailable: ' + name);
  document.head.append(script);
  return true;
};

let graphviz;
async function renderDot(source) {
  const lines = source.split(/\r\n|\r|\n/);
  const start = lines.findIndex(line => /^\s*@startdot\b/i.test(line));
  const end = lines.findIndex((line, i) => i > start && /^\s*@enddot\b/i.test(line));
  if (end < 0) throw new Error('The DOT diagram is missing @enddot.');
  graphviz ??= window.Viz.instance();
  const viz = await graphviz;
  const svg = viz.renderSVGElement(lines.slice(start + 1, end).join('\n'));
  // Viz uses physical pt units. The viewport transforms are in CSS pixels.
  const bounds = svg.viewBox.baseVal;
  svg.setAttribute('width', bounds.width);
  svg.setAttribute('height', bounds.height);
  for (const group of svg.querySelectorAll('g.node, g.cluster')) {
    const name = group.querySelector(':scope > title')?.textContent;
    if (name) group.setAttribute('data-qualified-name', name);
  }
  return new XMLSerializer().serializeToString(svg);
}
window.layoutDiagram = (source, files) => {
  if (/^\s*@startdot\b/im.test(source)) return renderDot(source);
  window.PLANTUML_FILE_LOADER = (name, from, ok, fail) => {
    const url = new URL(name.replaceAll('\\', '/'), new URL(from ?? '/', 'https://local.invalid/'));
    const key = decodeURIComponent(url.pathname.slice(1));
    if (url.origin === 'https://local.invalid' && files.has(key)) ok('/' + key, files.get(key));
    else fail('Import the include file or its folder: ' + name);
  };
  return new Promise((resolve, reject) => renderToString(
    source.split(/\r\n|\n|\r/), resolve, message => reject(new Error(String(message))), { maxSvgSize: 32768 }));
};
