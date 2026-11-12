// Uno 7.0.32's virtualized ComboBox options omit the initial aria-selected
// value, and its focus resolver does not include the ComboBox option regions.
// Project public C# selection and actual focused-item snapshots independently;
// selection, keyboard handling and popup lifetime remain native.
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
    // item positions may arrive later. Modal exposure can follow insertion.
    // Our aria-selected writes and focus calls cannot cause mutation loops.
    attributeFilter: ['aria-controls', 'aria-expanded', 'aria-posinset', 'aria-hidden']
  });
  return root;
}

function createSession() {
  const state = { document, root: null, scopes: new Map(), observer: null, projectingFocus: false };
  state.cancelFocus = () => {
    // The original intent is consumed before our own focus event. Any pending
    // intent left here would otherwise override a later, independent focus.
    for (const scope of state.scopes.values()) scope.pendingFocus = null;
  };
  state.observer = new MutationObserver(() => {
    if (session === state && state.scopes.size) reconcile(state);
  });
  state.document.addEventListener('focusin', state.cancelFocus, true);
  state.document.defaultView?.addEventListener('blur', state.cancelFocus);
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
  scope.pendingFocus = null;
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
  projectFocus(state, scope, options);
}

function canProjectFrom(state, element) {
  // Native focus can currently resolve to a structural semantic ancestor. A
  // queued native snapshot must not steal focus from the HTML/SVG editor or
  // another external DOM control whose focus the native manager cannot see.
  return state.document.hasFocus() && (element === state.document.body || element === state.document.documentElement
    || element === state.root || !!state.root?.contains(element));
}

function projectFocus(state, scope, options) {
  const intent = scope.pendingFocus;
  if (!intent || state.projectingFocus) return;
  if (session !== state || state.scopes.get(scope.automationId) !== scope
      || intent.revision !== scope.focusRevision
      || !state.document.hasFocus()
      || state.document.activeElement !== intent.expectedActiveElement) {
    scope.pendingFocus = null;
    return;
  }
  const option = [...options].find(([, index]) => index === intent.index)?.[0];
  // Zero-size virtualized options are intentionally focusable semantic nodes.
  // Wait for public modal exposure, without inventing bounds or tab stops.
  if (!option || option.closest('[aria-hidden="true"], [hidden], [inert]')
      || option.getAttribute('aria-disabled') === 'true') return;

  // Consume before calling focus: native focus callbacks can synchronously
  // reenter the bridge, and a newer request will receive its own revision.
  scope.pendingFocus = null;
  if (state.document.activeElement === option) return;
  state.projectingFocus = true;
  try { option.focus({ preventScroll: true }); }
  finally { state.projectingFocus = false; }
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
  state.scopes.set(automationId, {
    automationId, generation, selectedIndex, owned: new Map(),
    focusRevision: -1, pendingFocus: null
  });
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

/** Actual FocusManager item index; -1 cancels rather than inferring selection. */
export function focusComboScope(automationId, generation, focusRevision, focusedIndex) {
  if (!validIndex(focusedIndex) || !Number.isSafeInteger(focusRevision) || focusRevision < 0) return false;
  const state = session;
  const scope = state?.scopes.get(automationId);
  if (!scope || scope.generation !== generation || focusRevision <= scope.focusRevision) return false;
  scope.focusRevision = focusRevision;
  scope.pendingFocus = null;
  if (focusedIndex === -1) return true;
  currentRoot(state);
  const expectedActiveElement = state.document.activeElement;
  if (!canProjectFrom(state, expectedActiveElement)) return true;
  scope.pendingFocus = { revision: focusRevision, index: focusedIndex, expectedActiveElement };
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
    state.document.removeEventListener('focusin', state.cancelFocus, true);
    state.document.defaultView?.removeEventListener('blur', state.cancelFocus);
    session = null;
  }
  return true;
}
