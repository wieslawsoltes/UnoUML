// Uno 7.0.32's virtualized ComboBox options omit the initial aria-selected
// value. Project the public C# SelectedIndex onto the current semantic options;
// selection, focus, keyboard handling and popup lifetime remain native.
let session = null;

function validScope(automationId, generation) {
  return typeof automationId === 'string' && automationId.length > 0
    && Number.isSafeInteger(generation) && generation >= 0;
}

function validIndex(selectedIndex) {
  return Number.isSafeInteger(selectedIndex) && selectedIndex >= -1;
}

function currentRoot(state) {
  const root = state.document.getElementById('uno-semantics-root');
  if (root === state.root) return root;
  state.observer.disconnect();
  state.root = root;
  if (root) state.observer.observe(root, {
    childList: true, subtree: true, attributes: true,
    // Option creation is deferred to an animation frame, and relationships or
    // item positions may arrive later. Our aria-selected writes cannot loop.
    attributeFilter: ['aria-controls', 'aria-expanded', 'aria-posinset']
  });
  return root;
}

function createSession() {
  const state = { document, root: null, scopes: new Map(), observer: null };
  state.observer = new MutationObserver(() => {
    if (session === state && state.scopes.size) reconcile(state);
  });
  currentRoot(state);
  return state;
}

function connected(state, element) {
  return element.isConnected && state.root?.contains(element);
}

function restore(state, scope, element) {
  const ownership = scope.owned.get(element);
  if (!ownership) return;
  if (connected(state, element) && element.getAttribute('aria-selected') === ownership.last
      && ownership.original !== ownership.last) {
    if (ownership.original === null) element.removeAttribute('aria-selected');
    else element.setAttribute('aria-selected', ownership.original);
  }
  scope.owned.delete(element);
}

function release(state, scope) {
  for (const element of scope.owned.keys()) restore(state, scope, element);
}

function headFor(root, automationId) {
  for (const element of root.querySelectorAll('[role="combobox"][xamlautomationid]'))
    if (element.getAttribute('xamlautomationid') === automationId) return element;
  return null;
}

function optionsFor(state, scope) {
  const options = new Map();
  const head = state.root && headFor(state.root, scope.automationId);
  if (!head || head.getAttribute('aria-expanded') !== 'true') return options;
  for (const id of (head.getAttribute('aria-controls') ?? '').trim().split(/\s+/)) {
    if (!id) continue;
    const listbox = state.document.getElementById(id);
    if (!listbox || !state.root.contains(listbox) || listbox.getAttribute('role') !== 'listbox') continue;
    for (const option of listbox.querySelectorAll('[role="option"][aria-posinset]')) {
      if (option.closest('[role="listbox"]') !== listbox) continue;
      const position = option.getAttribute('aria-posinset');
      if (!/^[1-9]\d*$/.test(position)) continue;
      const index = Number(position) - 1;
      if (Number.isSafeInteger(index)) options.set(option, index);
    }
  }
  return options;
}

function reconcileScope(state, scope) {
  const options = optionsFor(state, scope);
  for (const element of scope.owned.keys())
    if (!options.has(element)) restore(state, scope, element);
  for (const [element, index] of options) {
    const value = String(index === scope.selectedIndex);
    let ownership = scope.owned.get(element);
    if (!ownership) {
      ownership = { original: element.getAttribute('aria-selected'), last: value };
      scope.owned.set(element, ownership);
    }
    ownership.last = value;
    if (element.getAttribute('aria-selected') !== value) element.setAttribute('aria-selected', value);
  }
}

function reconcile(state) {
  currentRoot(state);
  for (const scope of state.scopes.values()) reconcileScope(state, scope);
}

/** Opened snapshot from the actual ComboBox; native option DOM may arrive later. */
export function beginComboScope(automationId, generation, selectedIndex) {
  if (!validScope(automationId, generation) || !validIndex(selectedIndex)) return false;
  const state = session ?? (session = createSession());
  const previous = state.scopes.get(automationId);
  if (previous && previous.generation > generation) return false;
  if (previous?.generation === generation) {
    previous.selectedIndex = selectedIndex;
    reconcile(state);
    return true;
  }
  if (previous) release(state, previous);
  state.scopes.set(automationId, { automationId, generation, selectedIndex, owned: new Map() });
  reconcile(state);
  return true;
}

/** SelectionChanged updates metadata only; it never selects or focuses a node. */
export function updateComboScope(automationId, generation, selectedIndex) {
  if (!validIndex(selectedIndex)) return false;
  const state = session;
  const scope = state?.scopes.get(automationId);
  if (!scope || scope.generation !== generation) return false;
  scope.selectedIndex = selectedIndex;
  reconcile(state);
  return true;
}

/** Closed/Unloaded ends observation; stale callbacks cannot affect a reopen. */
export function endComboScope(automationId, generation) {
  const state = session;
  const scope = state?.scopes.get(automationId);
  if (!scope || scope.generation !== generation) return false;
  release(state, scope);
  state.scopes.delete(automationId);
  if (!state.scopes.size) {
    state.observer.disconnect();
    session = null;
  }
  return true;
}
