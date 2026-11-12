// Uno 7.0.32 retains modal focus scopes while its semantic DOM can be rebuilt.
// Keep the public ARIA contract on the current elements without replacing Uno's
// focus trap, moving focus, or reaching into its managed/runtime internals.
const semanticSelector = '[id^="uno-semantics-"]';
const modalAttributes = ['role', 'aria-modal', 'aria-hidden', 'tabindex'];
let session = null;

function validScope(automationId, generation) {
  return typeof automationId === 'string' && automationId.length > 0
    && Number.isSafeInteger(generation) && generation >= 0;
}

function snapshot(state, element) {
  let values = state.baselines.get(element);
  if (!values) {
    values = new Map(modalAttributes.map(name => [name, element.getAttribute(name)]));
    state.baselines.set(element, values);
  }
  return values;
}

function currentRoot(state) {
  const root = state.document.getElementById('uno-semantics-root');
  if (root === state.root) return root;
  state.observer.disconnect();
  state.root = root;
  if (root) {
    // Relationship attributes can arrive after a popup's children. The ARIA
    // attributes we write are deliberately absent, so our repairs cannot loop.
    state.observer.observe(root, {
      childList: true, subtree: true, attributes: true,
      attributeFilter: ['aria-controls', 'aria-expanded']
    });
  }
  return root;
}

function createSession() {
  const state = {
    document, root: null, scopes: [], baselines: new WeakMap(), owned: new Map(), observer: null
  };
  state.observer = new MutationObserver(() => {
    if (session === state && state.scopes.length) reconcile(state);
  });
  currentRoot(state);
  return state;
}

function connected(state, element) {
  return element.isConnected && state.root?.contains(element);
}

function setAttribute(element, name, value) {
  if (value === null) element.removeAttribute(name);
  else element.setAttribute(name, value);
}

function enforce(state, element, name, value) {
  let attributes = state.owned.get(element);
  if (!attributes) state.owned.set(element, attributes = new Map());
  let ownership = attributes.get(name);
  if (!ownership) {
    ownership = { original: snapshot(state, element).get(name), last: value };
    attributes.set(name, ownership);
  }
  ownership.last = value;
  if (element.getAttribute(name) !== value) setAttribute(element, name, value);
  // Record equal native writes too. A nested native scope can later restore
  // its parent's hidden values after that parent has already closed. The
  // pre-native baseline, not a per-dialog hidden snapshot, is authoritative.
}

function release(state, element, name) {
  const attributes = state.owned.get(element);
  const ownership = attributes?.get(name);
  if (!ownership) return;
  // Native cleanup or another owner may have already changed the attribute.
  // Never overwrite a value that no longer matches the one we enforced.
  if (connected(state, element) && element.getAttribute(name) === ownership.last
      && ownership.original !== ownership.last)
    setAttribute(element, name, ownership.original);
  attributes.delete(name);
  if (!attributes.size) state.owned.delete(element);
}

function releaseAll(state) {
  for (const [element, attributes] of [...state.owned])
    for (const name of [...attributes.keys()]) release(state, element, name);
}

function prune(state) {
  for (const element of state.owned.keys())
    if (!connected(state, element)) state.owned.delete(element);
}

function dialogFor(root, automationId) {
  // Compare the public value directly, without depending on localized names,
  // private native handles, or a selector escaping convention.
  for (const element of root.querySelectorAll('[xamlautomationid]'))
    if (element.getAttribute('xamlautomationid') === automationId) return element;
  return null;
}

function controlledRegions(state, dialog) {
  const regions = new Set([dialog]);
  const pending = [dialog];
  for (let index = 0; index < pending.length; index++) {
    const region = pending[index];
    const controls = [...region.querySelectorAll('[aria-controls]')];
    if (region.hasAttribute('aria-controls')) controls.unshift(region);
    for (const control of controls) {
      if (control.getAttribute('aria-expanded') === 'false') continue;
      for (const id of control.getAttribute('aria-controls').trim().split(/\s+/)) {
        if (!id) continue;
        const target = state.document.getElementById(id);
        if (!target || target === state.root || !state.root.contains(target)
            || target.contains(dialog) || regions.has(target)) continue;
        regions.add(target);
        pending.push(target);
      }
    }
  }
  return regions;
}

function reconcile(state) {
  const root = currentRoot(state);
  prune(state);
  if (!root || !state.scopes.length) return;

  // A pending/momentarily absent top scope remains authoritative. Falling back
  // to an older dialog while the top's DOM is being emitted would expose the
  // wrong content. The next activation/child-list delivery resolves it again.
  const top = state.scopes[state.scopes.length - 1];
  if (!top.ready) return;
  const dialog = dialogFor(root, top.automationId);
  if (!dialog) {
    // The native modal may activate before its semantic node exists, or the
    // node may disappear during a rebuild. Keep the current background closed
    // throughout that gap; a lower scope must not become the effective modal.
    for (const element of root.querySelectorAll(semanticSelector)) {
      enforce(state, element, 'aria-hidden', 'true');
      enforce(state, element, 'tabindex', '-1');
    }
    return;
  }

  const regions = controlledRegions(state, dialog);
  const contentRoots = [...regions];
  const ancestors = new Set();
  for (const region of regions)
    for (let ancestor = region.parentElement; ancestor && ancestor !== root; ancestor = ancestor.parentElement)
      ancestors.add(ancestor);

  // Only public Uno semantic nodes participate in background isolation. A
  // controlled popup's ancestors are exposed, while their unrelated branches
  // remain hidden. Roving tabindex inside an allowed region stays native.
  const nodes = new Set(root.querySelectorAll(semanticSelector));
  for (const element of ancestors) nodes.add(element);
  for (const region of regions) nodes.add(region);
  for (const element of nodes) {
    snapshot(state, element);
    const content = contentRoots.some(region => region === element || region.contains(element));
    if (content) {
      release(state, element, 'aria-hidden');
      release(state, element, 'tabindex');
    } else if (ancestors.has(element)) {
      enforce(state, element, 'aria-hidden', null);
      release(state, element, 'tabindex');
    } else {
      enforce(state, element, 'aria-hidden', 'true');
      enforce(state, element, 'tabindex', '-1');
    }
  }
  for (const region of regions) enforce(state, region, 'aria-hidden', null);

  const activeDialogs = new Set();
  for (const scope of state.scopes) {
    const current = dialogFor(root, scope.automationId);
    if (!current) continue;
    activeDialogs.add(current);
    enforce(state, current, 'role', 'dialog');
    enforce(state, current, 'aria-modal', current === dialog ? 'true' : null);
  }
  for (const element of [...state.owned.keys()]) {
    if (activeDialogs.has(element)) continue;
    release(state, element, 'role');
    release(state, element, 'aria-modal');
  }
}

/** Called synchronously by the application's Opened handler, before Uno's. */
export function beginDialogScope(automationId, generation) {
  if (!validScope(automationId, generation)) return false;
  const state = session ?? (session = createSession());
  const existing = state.scopes.find(scope => scope.automationId === automationId);
  if (existing && existing.generation >= generation) return existing.generation === generation;
  if (existing) state.scopes.splice(state.scopes.indexOf(existing), 1);
  const root = currentRoot(state);
  if (root) {
    // Capture before the native trap hides the application/Popup ancestors.
    // Capture the current root even when the dialog itself has not been emitted.
    for (const element of root.querySelectorAll(semanticSelector)) snapshot(state, element);
    const dialog = dialogFor(root, automationId);
    for (let element = dialog; element && element !== root; element = element.parentElement)
      snapshot(state, element);
  }
  state.scopes.push({ automationId, generation, ready: false });
  return true;
}

/** Called after all native Opened handlers have installed their focus scope. */
export function activateDialogScope(automationId, generation) {
  const state = session;
  const scope = state?.scopes.find(scope => scope.automationId === automationId && scope.generation === generation);
  if (!scope) return false;
  scope.ready = true;
  reconcile(state);
  return true;
}

/** Called after native Closed cleanup; stale callbacks cannot close a reopen. */
export function endDialogScope(automationId, generation) {
  const state = session;
  const index = state?.scopes.findIndex(scope => scope.automationId === automationId && scope.generation === generation) ?? -1;
  if (index < 0) return false;
  state.scopes.splice(index, 1);
  if (state.scopes.length) reconcile(state);
  else {
    state.observer.disconnect();
    releaseAll(state);
    session = null;
  }
  return true;
}
