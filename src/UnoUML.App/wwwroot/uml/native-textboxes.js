// Uno 7.0.32 projects TextBox.IsEnabled changes, but its semantic TextBox
// creation API omits the initial disabled state. Keep the current public
// input/textarea aligned with the owned native TextBox while it is loaded.
// Native focus can survive replacement of its focused semantic node; preserve
// that DOM focus only when a fresh public native-owner query agrees.
let session = null;

function validScope(automationId, generation) {
  return typeof automationId === 'string' && automationId.length > 0
    && Number.isSafeInteger(generation) && generation >= 0;
}


function setFocusWitness(scope, element) {
  scope.focusEpoch++;
  scope.focusWitness = element;
}

function clearFocusWitnesses(state) {
  for (const scope of state.scopes.values()) setFocusWitness(scope, null);
}

function inputScope(state, input) {
  if (!input || !state.root?.contains(input) ||
      (input.localName !== 'textarea' && !(input.localName === 'input' && input.type === 'text'))) return null;
  return state.scopes.get(input.getAttribute('xamlautomationid')) ?? null;
}

function exposedInput(state, input) {
  if (!input?.isConnected || !state.root?.contains(input) || input.disabled ||
      input.closest('[hidden], [inert], [aria-hidden="true"], [aria-disabled="true"]')) return false;
  const window = state.document.defaultView;
  for (let current = input; current; current = current.parentElement) {
    const style = window.getComputedStyle(current);
    if (style.display === 'none' || style.visibility === 'hidden' || style.visibility === 'collapse') return false;
  }
  const bounds = input.getBoundingClientRect();
  return bounds.width > 0 && bounds.height > 0;
}

function nativeOwnsFocus(state, scope) {
  const read = scope.readNativeFocus;
  if (session !== state || state.scopes.get(scope.automationId) !== scope ||
      !scope.isEnabled || typeof read !== 'function') return false;
  try {
    // This callback reads the public FocusManager owner now. A cached focus
    // snapshot must never authorize a replacement after native ownership moves.
    return read() === true && session === state &&
      state.scopes.get(scope.automationId) === scope && scope.readNativeFocus === read;
  } catch {
    return false;
  }
}

function fallbackFocus(state) {
  const active = state.document.activeElement;
  return active === state.document.body || active === state.document.documentElement || active === state.root;
}

function witnessCurrentFocus(state, scope, input, verifyNative = false) {
  if (!scope || session !== state || state.scopes.get(scope.automationId) !== scope ||
      typeof scope.readNativeFocus !== 'function' || !scope.isEnabled ||
      state.document.activeElement !== input || !state.document.hasFocus() ||
      state.composing.has(input) || inputScope(state, input) !== scope || !exposedInput(state, input)) return;
  if (verifyNative && !nativeOwnsFocus(state, scope)) return;
  // A native query can synchronously reenter the bridge. Recheck both lifetime
  // and the actual active element before retaining this new focus witness.
  if (session === state && state.scopes.get(scope.automationId) === scope &&
      state.document.activeElement === input && input.isConnected)
    setFocusWitness(scope, input);
}

function recordDomFocus(state, input) {
  clearFocusWitnesses(state);
  const scope = inputScope(state, input);
  if (scope) witnessCurrentFocus(state, scope, input);
}

function projectReplacementFocus(state, scope, elements) {
  const previous = scope.focusWitness;
  if (!previous || previous.isConnected || state.projectingFocus) return;
  if (!scope.isEnabled || previous.getAttribute('xamlautomationid') !== scope.automationId ||
      state.composing.has(previous) || !state.document.hasFocus() || !fallbackFocus(state)) {
    setFocusWitness(scope, null);
    return;
  }
  if (elements.size === 0) return; // A later semantic insertion can complete this replacement.
  if (elements.size !== 1) { setFocusWitness(scope, null); return; }
  const input = elements.values().next().value;
  if (state.composing.has(input) || !exposedInput(state, input)) return;
  const epoch = scope.focusEpoch;
  if (!nativeOwnsFocus(state, scope)) { setFocusWitness(scope, null); return; }
  // Query again after managed code returns; callbacks cannot redirect a stale
  // request onto another same-id editor or revive a removed loaded generation.
  const current = [...state.root.querySelectorAll('input[xamlautomationid], textarea[xamlautomationid]')]
    .filter(element => inputScope(state, element) === scope);
  if (session !== state || state.scopes.get(scope.automationId) !== scope ||
      scope.focusWitness !== previous || scope.focusEpoch !== epoch ||
      current.length !== 1 || current[0] !== input || !fallbackFocus(state) ||
      !state.document.hasFocus() || !exposedInput(state, input)) return;

  setFocusWitness(scope, null);
  state.projectingFocus = true;
  try {
    // The native projection already supplies value and selection. Transfer only
    // focus, keeping the current node's caret, range and viewport unchanged.
    input.focus({ preventScroll: true });
  } finally {
    state.projectingFocus = false;
  }
}

function installFocusWitnesses(state) {
  state.focusIn = event => recordDomFocus(state, event.target);
  state.focusOut = event => {
    const input = event.target, scope = inputScope(state, input);
    if (!scope || scope.focusWitness !== input) return;
    if (event.relatedTarget) { setFocusWitness(scope, null); return; }
    const epoch = scope.focusEpoch;
    queueMicrotask(() => {
      if (session !== state || state.scopes.get(scope.automationId) !== scope ||
          scope.focusWitness !== input || scope.focusEpoch !== epoch) return;
      // Native removal dispatches focusout before disconnecting the element.
      // An explicit blur leaves it connected and must never be undone.
      if (input.isConnected || !fallbackFocus(state)) setFocusWitness(scope, null);
      else reconcile(state);
    });
  };
  state.pointerDown = event => recordDomFocus(state, event.target);
  state.keyDown = event => {
    if (event.key === 'Tab') clearFocusWitnesses(state);
    else recordDomFocus(state, event.target);
  };
  state.compositionStart = event => {
    state.composing.add(event.target);
    clearFocusWitnesses(state);
  };
  state.compositionEnd = event => {
    state.composing.delete(event.target);
    const scope = inputScope(state, event.target);
    // Resume retention only for the still-focused editor with a fresh native
    // owner check; never restore focus or replay input during IME completion.
    if (scope) witnessCurrentFocus(state, scope, event.target, true);
  };
  state.windowBlur = () => clearFocusWitnesses(state);
  for (const [type, handler] of [
    ['focusin', state.focusIn], ['focusout', state.focusOut],
    ['pointerdown', state.pointerDown], ['keydown', state.keyDown],
    ['compositionstart', state.compositionStart], ['compositionend', state.compositionEnd]
  ]) state.document.addEventListener(type, handler, true);
  state.document.defaultView?.addEventListener('blur', state.windowBlur);
}

function uninstallFocusWitnesses(state) {
  clearFocusWitnesses(state);
  for (const [type, handler] of [
    ['focusin', state.focusIn], ['focusout', state.focusOut],
    ['pointerdown', state.pointerDown], ['keydown', state.keyDown],
    ['compositionstart', state.compositionStart], ['compositionend', state.compositionEnd]
  ]) state.document.removeEventListener(type, handler, true);
  state.document.defaultView?.removeEventListener('blur', state.windowBlur);
}

function currentRoot(state) {
  const root = state.document.getElementById('uno-semantics-root');
  if (root === state.root) return;
  state.observer.disconnect();
  clearFocusWitnesses(state);
  state.root = root;
  if (root) state.observer.observe(root, {
    childList: true, subtree: true, attributes: true,
    // The factory appends the input before assigning its public AutomationId.
    // Disabled/aria-disabled writes are deliberately outside this filter.
    attributeFilter: ['xamlautomationid']
  });
}

function createSession() {
  const state = { document, root: null, scopes: new Map(), observer: null,
    composing: new WeakSet(), projectingFocus: false };
  state.observer = new MutationObserver(() => {
    if (session === state && state.scopes.size) reconcile(state);
  });
  currentRoot(state);
  installFocusWitnesses(state);
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
  setFocusWitness(scope, null);
  scope.readNativeFocus = null;
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
    projectReplacementFocus(state, scope, elements);
  }
}

/** Loaded snapshot; public semantic input creation can follow this callback. */
export function beginTextBoxScope(automationId, generation, isEnabled, readNativeFocus = null) {
  if (!validScope(automationId, generation) || typeof isEnabled !== 'boolean') return false;
  const state = session ?? (session = createSession());
  const previous = state.scopes.get(automationId);
  if (previous && previous.generation > generation) return false;
  const read = typeof readNativeFocus === 'function' ? readNativeFocus : null;
  if (previous?.generation === generation) {
    previous.isEnabled = isEnabled;
    if (previous.readNativeFocus !== read) setFocusWitness(previous, null);
    previous.readNativeFocus = read;
  } else {
    if (previous) release(state, previous);
    state.scopes.set(automationId, { automationId, generation, isEnabled, owned: new Map(),
      readNativeFocus: read, focusWitness: null, focusEpoch: 0 });
  }
  reconcile(state);
  const scope = state.scopes.get(automationId);
  witnessCurrentFocus(state, scope, state.document.activeElement);
  return true;
}

/** Public effective IsEnabled value, independent of read-only and focus state. */
export function updateTextBoxScope(automationId, generation, isEnabled) {
  if (typeof isEnabled !== 'boolean') return false;
  const state = session;
  const scope = state?.scopes.get(automationId);
  if (!scope || scope.generation !== generation) return false;
  scope.isEnabled = isEnabled;
  if (!isEnabled) setFocusWitness(scope, null);
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
    uninstallFocusWitnesses(state);
    session = null;
  }
  return true;
}
