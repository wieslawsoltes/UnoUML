// Uno 7.0.32 projects TextBox.IsEnabled changes, but its semantic TextBox
// creation API omits the initial disabled state. Keep the current public
// input/textarea aligned with the owned native TextBox while it is loaded.
let session = null;

function validScope(automationId, generation) {
  return typeof automationId === 'string' && automationId.length > 0
    && Number.isSafeInteger(generation) && generation >= 0;
}

function currentRoot(state) {
  const root = state.document.getElementById('uno-semantics-root');
  if (root === state.root) return;
  state.observer.disconnect();
  state.root = root;
  if (root) state.observer.observe(root, {
    childList: true, subtree: true, attributes: true,
    // The factory appends the input before assigning its public AutomationId.
    // Disabled/aria-disabled writes are deliberately outside this filter.
    attributeFilter: ['xamlautomationid']
  });
}

function createSession() {
  const state = { document, root: null, scopes: new Map(), observer: null };
  state.observer = new MutationObserver(() => {
    if (session === state && state.scopes.size) reconcile(state);
  });
  currentRoot(state);
  return state;
}

function restore(state, scope, element) {
  const attributes = scope.owned.get(element);
  if (!attributes) return;
  if (element.isConnected && state.root?.contains(element)) {
    for (const [name, ownership] of attributes) {
      if (element.getAttribute(name) !== ownership.last) continue;
      if (ownership.original === null) element.removeAttribute(name);
      else element.setAttribute(name, ownership.original);
    }
  }
  scope.owned.delete(element);
}

function release(state, scope) {
  for (const element of scope.owned.keys()) restore(state, scope, element);
}

function write(scope, element, name, value) {
  const current = element.getAttribute(name);
  let attributes = scope.owned.get(element);
  let ownership = attributes?.get(name);
  if (current === value) {
    // A native property update may already have applied the new snapshot.
    if (ownership) ownership.last = value;
    return;
  }
  if (!attributes) scope.owned.set(element, attributes = new Map());
  if (!ownership) attributes.set(name, ownership = { original: current, last: value });
  ownership.last = value;
  if (value === null) element.removeAttribute(name);
  else element.setAttribute(name, value);
}

function reconcile(state) {
  currentRoot(state);
  const current = new Map();
  for (const element of state.root?.querySelectorAll('input[xamlautomationid], textarea[xamlautomationid]') ?? []) {
    // TextBox uses text inputs or textareas. Never project onto a checkbox,
    // slider, password control, or unrelated element reusing a public ID.
    if (element.localName !== 'textarea' && element.type !== 'text') continue;
    const id = element.getAttribute('xamlautomationid');
    if (!state.scopes.has(id)) continue;
    let elements = current.get(id);
    if (!elements) current.set(id, elements = new Set());
    elements.add(element);
  }
  for (const scope of state.scopes.values()) {
    const elements = current.get(scope.automationId) ?? new Set();
    for (const element of scope.owned.keys())
      if (!elements.has(element)) restore(state, scope, element);
    for (const element of elements) {
      // HTML's boolean disabled attribute reflects the actual disabled
      // property. Preserve every other property, including readonly/tabindex.
      write(scope, element, 'disabled', scope.isEnabled ? null : '');
      write(scope, element, 'aria-disabled', String(!scope.isEnabled));
    }
  }
}

/** Loaded snapshot; public semantic input creation can follow this callback. */
export function beginTextBoxScope(automationId, generation, isEnabled) {
  if (!validScope(automationId, generation) || typeof isEnabled !== 'boolean') return false;
  const state = session ?? (session = createSession());
  const previous = state.scopes.get(automationId);
  if (previous && previous.generation > generation) return false;
  if (previous?.generation === generation) previous.isEnabled = isEnabled;
  else {
    if (previous) release(state, previous);
    state.scopes.set(automationId, { automationId, generation, isEnabled, owned: new Map() });
  }
  reconcile(state);
  return true;
}

/** Public effective IsEnabled value, independent of read-only and focus state. */
export function updateTextBoxScope(automationId, generation, isEnabled) {
  if (typeof isEnabled !== 'boolean') return false;
  const state = session;
  const scope = state?.scopes.get(automationId);
  if (!scope || scope.generation !== generation) return false;
  scope.isEnabled = isEnabled;
  reconcile(state);
  return true;
}

/** Unloaded ends ownership; callbacks from an older load cannot end a reload. */
export function endTextBoxScope(automationId, generation) {
  const state = session;
  const scope = state?.scopes.get(automationId);
  if (!scope || scope.generation !== generation) return false;
  currentRoot(state);
  release(state, scope);
  state.scopes.delete(automationId);
  if (!state.scopes.size) {
    state.observer.disconnect();
    session = null;
  }
  return true;
}
