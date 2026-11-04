// Uno 6.7.135's managed hit testing uses cached scroll offsets rather than the
// DOM event target. Only explicitly registered viewers participate in this
// compatibility bridge; ordinary pointer dispatch and native scrolling remain
// in control of the browser and Uno.
const viewers = new Map();
const events = ['pointerdown', 'pointerup', 'pointercancel'];

function synchronizePointer(event) {
  for (const [id, registration] of viewers) {
    if (event.type === 'pointercancel') {
      registration.pointers.delete(event.pointerId);
      continue;
    }
    const pressed = event.type === 'pointerup' && registration.pointers.delete(event.pointerId);
    const owner = document.getElementById(id);
    if (!owner?.isConnected) continue;
    const inside = event.composedPath().includes(owner);
    if (event.type === 'pointerdown' && inside) registration.pointers.add(event.pointerId);
    if (!inside && !pressed) continue;

    // Resolve the current template on each event; nested TextBox presenters
    // belong to their own viewer and must not replace the inspector's offsets.
    const presenter = [...owner.querySelectorAll('.uno-scrollcontentpresenter')]
      .find(element => element.closest('.uno-scrollviewer') === owner);
    if (!presenter) continue;
    const horizontal = presenter.scrollLeft, vertical = presenter.scrollTop;
    if (Number.isFinite(horizontal) && Number.isFinite(vertical) && horizontal >= 0 && vertical >= 0)
      registration.synchronize(horizontal, vertical);
  }
}

export function configureScrollSynchronization(id, synchronize) {
  if (viewers.size === 0)
    for (const type of events) window.addEventListener(type, synchronizePointer, true);
  viewers.set(id, { synchronize, pointers: new Set() });
}

export function removeScrollSynchronization(id) {
  viewers.delete(id);
  if (viewers.size === 0)
    for (const type of events) window.removeEventListener(type, synchronizePointer, true);
}
