// Uno 7 renders TextBox text in the composition surface and exposes a semantic
// textarea plus a focused IME input. AutomationProperties.AutomationId is
// published as xamlautomationid; no UIElement.Handle or private Uno API is used.
// JSON preserves a leading U+FEFF across runtime string marshalling.
const registrations = new Map();
const composing = new WeakSet();
const compositionEdits = new WeakMap();
const commits = new WeakMap();
const events = ['focusin', 'beforeinput', 'input', 'compositionstart', 'compositionend'];
const commitEvents = ['input', 'compositionend'];

let projectionObserver = null;
let projectionRoot = null;

// Accessibility can expose a loaded TextBox long after its first native frame.
// Watch only that semantic tree, and only while a registered editor expects a
// BOM. Value writes and unrelated canvas/HTML mutations are never observed.
function observeProjections() {
  const required = [...registrations.values()].some(entry => entry.expected);
  const root = required ? document.getElementById('uno-semantics-root') : null;
  if (root === projectionRoot && projectionObserver) return;
  projectionObserver?.disconnect();
  projectionObserver = null;
  projectionRoot = null;
  if (!root?.isConnected) return;

  const observer = new MutationObserver(records => {
    // A detached observer may already have a delivery queued. It cannot repair
    // an editor after a plain-source refresh, removal, or root replacement.
    if (projectionObserver !== observer || projectionRoot !== root ||
        !root.isConnected || document.getElementById('uno-semantics-root') !== root) return;
    const candidates = new Set();
    for (const record of records) {
      for (const node of record.addedNodes) {
        if (node.nodeType !== 1) continue;
        if (node.matches('textarea[xamlautomationid]')) candidates.add(node);
        for (const input of node.querySelectorAll('textarea[xamlautomationid]')) candidates.add(input);
      }
    }
    for (const input of candidates) {
      const entry = registrations.get(input.getAttribute('xamlautomationid'));
      // Resolve the live registration and node at delivery time, rather than
      // retaining an old document's expected text in the mutation callback.
      if (!entry?.expected || !input.isConnected || !root.contains(input) ||
          semanticInput(entry) !== input) continue;
      repair(entry);
    }
  });
  projectionRoot = root;
  projectionObserver = observer;
  observer.observe(root, { childList: true, subtree: true });
}

function semanticInput(entry) {
  return document.querySelector('textarea[xamlautomationid="' + CSS.escape(entry.id) + '"]');
}

function inputs(entry) {
  const result = new Set();
  const semantic = semanticInput(entry);
  if (semantic?.isConnected) result.add(semantic);
  const active = document.activeElement;
  // Public managed focus state establishes ownership of the shared IME input.
  // An explicitly identified different semantic editor must never be repaired.
  if (entry.focused && active instanceof HTMLTextAreaElement && active.isConnected) {
    const automationId = active.getAttribute('xamlautomationid');
    if (!automationId || automationId === entry.id) result.add(active);
  }
  return result;
}

function repair(entry) {
  if (registrations.get(entry.id) !== entry || !entry.expected) return;
  const expected = entry.expected;
  for (const input of inputs(entry)) {
    if (composing.has(input) || input.value !== expected.slice(1)) continue;
    // Offsets arrive from the authoritative managed TextBox. CRLF normalization
    // changes DOM offsets, so syncSourceText maps them before applying them.
    restore(input, expected, entry.start, entry.end, input.selectionDirection);
  }
}

function restore(input, value, start, end, direction) {
  const top = input.scrollTop, left = input.scrollLeft;
  input.value = value;
  input.setSelectionRange(start, end, direction);
  input.scrollTop = top;
  input.scrollLeft = left;
}

function schedule(entry) {
  repair(entry);
  if (!entry.queued) {
    entry.queued = true;
    queueMicrotask(() => {
      entry.queued = false;
      repair(entry);
    });
  }
  // Coalesce the current native frame; the scoped semantic observer handles
  // editors created later without extending this into continuous frame polling.
  if (!entry.frame) {
    entry.frame = requestAnimationFrame(() => {
      entry.frame = 0;
      repair(entry);
    });
  }
}

function onEditorEvent(event) {
  const input = event.target;
  if (!(input instanceof HTMLTextAreaElement)) return;
  const owned = [...registrations.values()].filter(entry => inputs(entry).has(input));
  let composition;
  if (event.type === 'compositionstart') {
    composing.add(input);
    compositionEdits.delete(input);
    if (event.isTrusted && owned.length) compositionEdits.set(input, { owners: owned, value: null });
  } else if (event.type === 'compositionend') {
    composing.delete(input);
    composition = compositionEdits.get(input);
    compositionEdits.delete(input); // A completed native sequence cannot authorize another end.
  } else if (composing.has(input)) {
    const pending = compositionEdits.get(input);
    if (event.type === 'beforeinput') {
      if (pending) pending.value = null;
    } else if (event.type === 'input') {
      if (pending && event.isTrusted && event.isComposing && pending.owners.some(entry => owned.includes(entry)))
        pending.value = input.value;
      else compositionEdits.delete(input);
    }
  }
  // Chromium's native queued compositionend does not always carry isTrusted.
  // Accept only an exact value witnessed by trusted composing input following a
  // trusted start on the same owner. Synthetic, changed or replayed ends have no
  // such provenance; a removed/replaced registration cannot transfer it either.
  const nativeEnd = event.type === 'compositionend' && composition?.value != null && input.value === composition.value;
  const owners = event.isTrusted ? owned : nativeEnd ? composition.owners.filter(entry => owned.includes(entry)) : [];
  const committed = commitEvents.includes(event.type) && owners.length && !event.isComposing && !composing.has(input);
  const captured = committed && input.value.startsWith('\uFEFF') ? {
    input, value: input.value, start: input.selectionStart, end: input.selectionEnd,
    direction: input.selectionDirection, owners
  } : null;
  for (const entry of owned) {
    if (event.type === 'focusin') {
      schedule(entry);
    } else {
      // Native input and IME own their pending edit. Invalidate old expectations
      // before Uno handles the event; a subsequent managed Text change supplies
      // the new source. Intentional BOM removal is never undone by a queued repair.
      entry.expected = null;
    }
  }
  if (owned.length && event.type !== 'focusin') observeProjections();
  if (captured) commits.set(event, captured);
}

function onEditorCommit(event) {
  const captured = commits.get(event);
  if (!captured) return;
  commits.delete(event);
  const { input, value, start, end, direction, owners } = captured;
  // Uno imports the correct committed value, but its synchronous outgoing string
  // projection can consume U+FEFF. Chromium emits multiple input events for one
  // multiline insertion, so repair that exact echo before the next event reads
  // it back. A microtask can run before Uno's target listener and is too early.
  // This never generates another input event or revives a removed source editor.
  if (!input.isConnected || composing.has(input) || input.value !== value.slice(1)) return;
  if (!owners.some(entry => registrations.get(entry.id) === entry && inputs(entry).has(input))) return;
  restore(input, value, start, end, direction);
}

function setListeners(add) {
  for (const type of events) {
    if (add) document.addEventListener(type, onEditorEvent, true);
    else document.removeEventListener(type, onEditorEvent, true);
  }
  for (const type of commitEvents) {
    if (add) document.addEventListener(type, onEditorCommit);
    else document.removeEventListener(type, onEditorCommit);
  }
}

function domOffset(source, offset) {
  const clamped = Math.max(0, Math.min(source.length, Math.trunc(Number(offset) || 0)));
  return source.slice(0, clamped).replace(/\r\n?/g, '\n').length;
}

export function syncSourceText(id, json, focused = false, selectionStart = 0, selectionLength = 0) {
  if (typeof id !== 'string' || !id.trim()) throw new TypeError('An automation id is required.');
  const source = JSON.parse(json);
  if (typeof source !== 'string') throw new TypeError('Source text must be a JSON string.');
  let entry = registrations.get(id);
  if (!entry) {
    if (registrations.size === 0) setListeners(true);
    entry = { id, expected: null, focused: false, start: 0, end: 0, queued: false, frame: 0 };
    registrations.set(id, entry);
  }
  const start = Math.max(0, Math.min(source.length, Math.trunc(Number(selectionStart) || 0)));
  const length = Math.max(0, Math.trunc(Number(selectionLength) || 0));
  Object.assign(entry, {
    expected: source.startsWith('\uFEFF') ? source.replace(/\r\n?/g, '\n') : null,
    focused: focused === true,
    start: domOffset(source, start),
    end: domOffset(source, Math.min(source.length, start + length))
  });
  observeProjections();
  if (entry.expected) schedule(entry);
}

export function removeSourceText(id) {
  const entry = registrations.get(id);
  if (!entry) return;
  registrations.delete(id);
  if (entry.frame) cancelAnimationFrame(entry.frame);
  observeProjections();
  if (registrations.size === 0) setListeners(false);
}
