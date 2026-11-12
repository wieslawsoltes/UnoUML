import test from 'node:test';
import assert from 'node:assert/strict';
import { syncSourceText, removeSourceText } from '../src/UnoUML.App/wwwroot/uml/source-text.js';

function fixture() {
  const names = ['document', 'HTMLTextAreaElement', 'MutationObserver', 'CSS', 'queueMicrotask', 'requestAnimationFrame', 'cancelAnimationFrame'];
  const saved = new Map(names.map(name => [name, Object.getOwnPropertyDescriptor(globalThis, name)]));
  const semantic = new Map(), listeners = new Map(), microtasks = [], frames = new Map(), registrations = new Set();
  const observers = [];
  let frameId = 0;
  class Element {
    constructor() {
      this.nodeType = 1;
      this.isConnected = true;
      this.parentNode = null;
      this.children = [];
    }
    append(...nodes) {
      for (const node of nodes) {
        if (node.parentNode) node.parentNode.children = node.parentNode.children.filter(child => child !== node);
        node.parentNode = this;
        this.children.push(node);
      }
    }
    contains(node) { return node === this || this.children.some(child => child.contains(node)); }
    matches(selector) { return selector === 'textarea[xamlautomationid]' && this instanceof TextArea && this.automationId != null; }
    querySelectorAll(selector) {
      return this.children.flatMap(child => [...(child.matches(selector) ? [child] : []), ...child.querySelectorAll(selector)]);
    }
  }
  class TextArea extends Element {
    constructor(id = null) {
      super();
      this.automationId = id;
      this.value = '';
      this.selectionStart = this.selectionEnd = 0;
      this.selectionDirection = 'forward';
      this.scrollTop = 0;
      this.scrollLeft = 0;
    }
    getAttribute(name) { return name === 'xamlautomationid' ? this.automationId : null; }
    setSelectionRange(start, end, direction) {
      this.selectionStart = start;
      this.selectionEnd = end;
      this.selectionDirection = direction;
    }
  }
  let root = new Element();
  class MutationObserver {
    constructor(callback) { this.callback = callback; this.connected = false; observers.push(this); }
    observe(target, options) { this.target = target; this.options = { ...options }; this.connected = true; }
    disconnect() { this.connected = false; }
    deliver(...nodes) { this.callback([{ type: 'childList', target: this.target, addedNodes: nodes }], this); }
  }
  const document = {
    activeElement: null,
    getElementById(id) { return id === 'uno-semantics-root' ? root : null; },
    querySelector(selector) {
      const id = selector.match(/^textarea\[xamlautomationid="([^"]+)"\]$/)?.[1];
      return semantic.get(id) ?? null;
    },
    addEventListener(type, handler, options) {
      if (!listeners.has(type)) listeners.set(type, { capture: new Set(), bubble: new Set() });
      const phase = options === true || options?.capture ? 'capture' : 'bubble';
      listeners.get(type)[phase].add(handler);
    },
    removeEventListener(type, handler, options) {
      const phase = options === true || options?.capture ? 'capture' : 'bubble';
      listeners.get(type)?.[phase].delete(handler);
    }
  };
  Object.assign(globalThis, {
    document,
    HTMLTextAreaElement: TextArea,
    MutationObserver,
    CSS: { escape: value => value },
    queueMicrotask: callback => microtasks.push(callback),
    requestAnimationFrame: callback => { const id = ++frameId; frames.set(id, callback); return id; },
    cancelAnimationFrame: id => frames.delete(id)
  });
  return {
    document, semantic, listeners, frames, observers,
    get root() { return root; },
    activeObservers: () => observers.filter(observer => observer.connected),
    input(id = null) {
      const input = new TextArea(id);
      if (id) { semantic.set(id, input); root.append(input); }
      return input;
    },
    subtree(...children) {
      const node = new Element();
      root.append(node);
      node.append(...children);
      return node;
    },
    replaceRoot() {
      root.isConnected = false;
      root = new Element();
      return root;
    },
    added(...nodes) {
      for (const observer of observers.filter(observer => observer.connected)) observer.deliver(...nodes);
    },
    sync(id, source, focused = false, start = 0, length = 0) {
      registrations.add(id);
      syncSourceText(id, JSON.stringify(source), focused, start, length);
    },
    remove(id) { removeSourceText(id); registrations.delete(id); },
    fire(type, target, properties = {}, native = () => {}) {
      const event = { type, target, isTrusted: false, isComposing: false, ...properties };
      for (const handler of [...listeners.get(type)?.capture ?? []]) handler(event);
      native(event);
      for (const handler of [...listeners.get(type)?.bubble ?? []]) handler(event);
      return event;
    },
    listenerCounts: () => Object.fromEntries([...listeners].map(([type, phases]) =>
      [type, { capture: phases.capture.size, bubble: phases.bubble.size }])),
    flush() {
      for (const callback of microtasks.splice(0)) callback();
      const callbacks = [...frames.values()];
      frames.clear();
      for (const callback of callbacks) callback();
    },
    dispose() {
      for (const id of registrations) removeSourceText(id);
      for (const [name, descriptor] of saved) {
        if (descriptor) Object.defineProperty(globalThis, name, descriptor);
        else delete globalThis[name];
      }
    }
  };
}

test('BOM projection preserves managed UTF-16 selection, CRLF offsets, direction and scroll position', () => {
  const dom = fixture();
  try {
    const semantic = dom.input('source'), ime = dom.input();
    dom.document.activeElement = ime;
    for (const input of [semantic, ime]) {
      input.value = 'a\nbc';
      input.setSelectionRange(1, 3, 'backward');
      input.scrollTop = 31;
      input.scrollLeft = 9;
    }
    dom.sync('source', '\uFEFFa\r\nbc', true, 2, 3);
    for (const input of [semantic, ime]) {
      assert.equal(input.value, '\uFEFFa\nbc');
      assert.deepEqual([input.selectionStart, input.selectionEnd, input.selectionDirection], [2, 4, 'backward']);
      assert.deepEqual([input.scrollTop, input.scrollLeft], [31, 9]);
    }
  } finally { dom.dispose(); }
});

test('only the focused source owns the IME input and another identified editor is never changed', () => {
  const dom = fixture();
  try {
    const source = dom.input('source'), other = dom.input('other'), ime = dom.input();
    source.value = other.value = ime.value = 'abc';
    dom.document.activeElement = ime;
    dom.sync('source', '\uFEFFabc', false);
    assert.equal(source.value, '\uFEFFabc');
    assert.equal(ime.value, 'abc');
    dom.document.activeElement = other;
    dom.sync('source', '\uFEFFabc', true);
    assert.equal(other.value, 'abc');
  } finally { dom.dispose(); }
});

test('a user can remove the BOM before queued projection work completes', () => {
  const dom = fixture();
  try {
    const input = dom.input('source');
    dom.document.activeElement = input;
    input.value = 'abc';
    dom.sync('source', '\uFEFFabc', true, 1);
    input.value = 'abc';
    dom.fire('input', input);
    dom.flush();
    assert.equal(input.value, 'abc', 'Queued repair must not undo the native edit');
    dom.sync('source', 'abc', true);
    assert.deepEqual(dom.listenerCounts(), {
      focusin: { capture: 1, bubble: 0 },
      beforeinput: { capture: 1, bubble: 0 },
      input: { capture: 1, bubble: 1 },
      compositionstart: { capture: 1, bubble: 0 },
      compositionend: { capture: 1, bubble: 1 }
    }, 'Plain source retains one capture lifecycle and one commit-bubble repair until unload');
    assert.equal(dom.frames.size, 0, 'Plain source registration does not start a repair frame');
    dom.remove('source');
    assert.ok([...dom.listeners.values()].every(phases => phases.capture.size === 0 && phases.bubble.size === 0),
      'Unloading a plain-source registration removes both phases');
  } finally { dom.dispose(); }
});

test('composition owns its text until a fresh managed commit arrives', () => {
  const dom = fixture();
  try {
    const input = dom.input('source');
    dom.document.activeElement = input;
    input.value = 'abc';
    dom.sync('source', '\uFEFFabc', true);
    dom.fire('compositionstart', input);
    input.value = 'abc';
    dom.sync('source', '\uFEFFabc', true);
    dom.flush();
    assert.equal(input.value, 'abc', 'A managed echo cannot replace active IME text');
    dom.fire('compositionend', input);
    dom.flush();
    assert.equal(input.value, 'abc', 'Composition end must not replay stale text');
    input.value = 'あbc';
    dom.sync('source', '\uFEFFあbc', true, 2);
    assert.equal(input.value, '\uFEFFあbc');
    assert.equal(input.selectionStart, 2);
  } finally { dom.dispose(); }
});

test('next-frame semantic projection and focus are repaired without continuous polling', () => {
  const dom = fixture();
  try {
    dom.sync('source', '\uFEFFabc', false, 4);
    const semantic = dom.input('source');
    semantic.value = 'abc';
    dom.flush();
    assert.equal(semantic.value, '\uFEFFabc');
    const ime = dom.input();
    ime.value = 'abc';
    dom.document.activeElement = ime;
    dom.sync('source', '\uFEFFabc', true, 4);
    dom.fire('focusin', ime);
    assert.equal(ime.value, '\uFEFFabc');
    assert.equal(ime.selectionStart, 4);
  } finally { dom.dispose(); }
});

test('a semantic textarea created after all queued projection work is repaired with managed caret and scroll', () => {
  const dom = fixture();
  try {
    dom.sync('source', '\uFEFFa\r\n🧠', false, 2, 4);
    dom.flush();
    assert.equal(dom.frames.size, 0, 'The original one-shot projection has already completed');
    assert.equal(dom.activeObservers().length, 1);
    assert.equal(dom.activeObservers()[0].target, dom.root);
    assert.deepEqual(dom.activeObservers()[0].options, { childList: true, subtree: true });

    const input = dom.input('source'), foreign = dom.input('other');
    input.value = foreign.value = 'a\n🧠';
    input.setSelectionRange(1, 3, 'backward');
    input.scrollTop = 17;
    input.scrollLeft = 6;
    dom.document.activeElement = foreign;
    dom.added(input);
    assert.equal(input.value, '\uFEFFa\n🧠');
    assert.deepEqual([input.selectionStart, input.selectionEnd, input.selectionDirection], [2, 5, 'backward']);
    assert.deepEqual([input.scrollTop, input.scrollLeft], [17, 6]);
    assert.equal(foreign.value, 'a\n🧠', 'A late source projection must not acquire a different editor');
    assert.equal(dom.frames.size, 0, 'Mutation delivery repairs directly without perpetual animation frames');

    input.setSelectionRange(0, 0, 'forward');
    dom.added(input);
    assert.deepEqual([input.selectionStart, input.selectionEnd], [0, 0],
      'A later mutation must not reset the caret of an already correct projection');
  } finally { dom.dispose(); }
});

test('nested replacement delivery repairs only the current matching semantic node', () => {
  const dom = fixture();
  try {
    const old = dom.input('source');
    old.value = 'abc';
    dom.sync('source', '\uFEFFabc', false, 2);
    dom.flush();
    old.value = 'abc';
    old.isConnected = false;
    const current = dom.input('source'), foreign = dom.input('other');
    current.value = foreign.value = 'abc';
    const branch = dom.subtree(dom.subtree(current));
    dom.added({ nodeType: 3 }, old, branch, foreign);
    assert.equal(current.value, '\uFEFFabc');
    assert.equal(current.selectionStart, 2);
    assert.equal(old.value, 'abc', 'Detached delivery cannot repair the replaced textarea');
    assert.equal(foreign.value, 'abc');

    const duplicate = dom.input('source');
    duplicate.value = current.value = 'abc';
    dom.semantic.set('source', current); // The first matching live node is still current.
    dom.added(duplicate);
    assert.equal(duplicate.value, 'abc');
    assert.equal(current.value, 'abc', 'A duplicate identity must not trigger repair of a different live node');
    dom.added(current);
    assert.equal(current.value, '\uFEFFabc');

    const changed = dom.input('source');
    changed.value = 'an independent edit';
    changed.setSelectionRange(3, 5, 'backward');
    dom.added(dom.subtree(changed));
    assert.equal(changed.value, 'an independent edit', 'Only the exact missing-BOM echo can be repaired');
    assert.deepEqual([changed.selectionStart, changed.selectionEnd, changed.selectionDirection], [3, 5, 'backward']);
  } finally { dom.dispose(); }
});

test('callbacks from removed registrations and replaced semantic roots cannot repair a newer lifetime', () => {
  const dom = fixture();
  try {
    dom.sync('source', '\uFEFFabc', false, 4);
    dom.flush();
    const oldObserver = dom.activeObservers()[0];
    dom.remove('source');
    assert.equal(oldObserver.connected, false);
    dom.sync('source', '\uFEFFabc', false, 2);
    dom.flush();
    const input = dom.input('source');
    input.value = 'abc';
    oldObserver.deliver(input);
    assert.equal(input.value, 'abc', 'An already queued callback must not acquire a re-registered owner');
    dom.added(input);
    assert.equal(input.value, '\uFEFFabc');
    assert.equal(input.selectionStart, 2);

    const priorRootObserver = dom.activeObservers()[0];
    dom.replaceRoot();
    const replacement = dom.input('source');
    replacement.value = 'abc';
    priorRootObserver.deliver(replacement);
    assert.equal(replacement.value, 'abc', 'Root identity is checked even before the next managed sync');
    // This is not an exact echo, so a fresh snapshot cannot repair it immediately.
    replacement.value = 'pending native projection';
    dom.sync('source', '\uFEFFabc', false, 3);
    dom.flush();
    replacement.value = 'abc';
    priorRootObserver.deliver(replacement);
    assert.equal(replacement.value, 'abc', 'Disconnected root delivery cannot invoke the replacement observer');
    dom.added(replacement);
    assert.equal(replacement.value, '\uFEFFabc');
    assert.equal(replacement.selectionStart, 3);
  } finally { dom.dispose(); }
});

test('plain refresh and intentional BOM deletion stop observation after the last expected source', () => {
  const dom = fixture();
  try {
    dom.sync('plain', '', false);
    assert.equal(dom.activeObservers().length, 0, 'Plain registrations do not observe the semantic tree');
    dom.sync('first', '\uFEFFabc', false);
    dom.sync('second', '\uFEFFdef', false);
    dom.flush();
    assert.equal(dom.activeObservers().length, 1, 'Loaded BOM sources share one scoped observer');
    dom.sync('first', '', false);
    const plain = dom.input('first');
    plain.value = 'abc';
    dom.added(plain);
    assert.equal(plain.value, 'abc');
    assert.equal(dom.activeObservers().length, 1, 'The remaining expected source keeps observation alive');
    dom.remove('second');
    assert.equal(dom.activeObservers().length, 0);

    const input = dom.input('source');
    input.value = 'abc';
    dom.sync('source', '\uFEFFabc', true, 1);
    dom.flush();
    const deletedObserver = dom.activeObservers()[0];
    dom.fire('beforeinput', input, { isTrusted: true });
    assert.equal(dom.activeObservers().length, 0, 'Native editing invalidates the expectation before its mutation');
    input.value = 'abc';
    input.setSelectionRange(0, 0, 'forward');
    dom.fire('input', input, { isTrusted: true });
    deletedObserver.deliver(input);
    dom.flush();
    assert.equal(input.value, 'abc', 'A deliberate deletion cannot be reversed by queued mutation delivery');
    assert.deepEqual([input.selectionStart, input.selectionEnd], [0, 0]);
    dom.sync('source', '', true);
    const replacement = dom.input('source');
    replacement.value = 'abc';
    deletedObserver.deliver(replacement);
    assert.equal(replacement.value, 'abc');
    assert.equal(dom.activeObservers().length, 0);
  } finally { dom.dispose(); }
});

test('late projection observation preserves active composition and requires a fresh managed commit', () => {
  const dom = fixture();
  try {
    const input = dom.input('source');
    input.value = 'abc';
    dom.sync('source', '\uFEFFabc', true, 1);
    dom.flush();
    dom.fire('compositionstart', input, { isTrusted: true });
    assert.equal(dom.activeObservers().length, 0);
    input.value = 'abc';
    input.setSelectionRange(1, 2, 'backward');
    dom.sync('source', '\uFEFFabc', true, 4);
    dom.flush();
    dom.added(dom.subtree(input));
    assert.equal(input.value, 'abc', 'Subtree insertion does not take text ownership away from an active IME');
    assert.deepEqual([input.selectionStart, input.selectionEnd, input.selectionDirection], [1, 2, 'backward']);
    dom.fire('compositionend', input);
    assert.equal(dom.activeObservers().length, 0);
    dom.added(input);
    assert.equal(input.value, 'abc', 'Ending composition cannot replay the old expected text');
    input.value = 'あbc';
    dom.sync('source', '\uFEFFあbc', true, 2);
    assert.equal(input.value, '\uFEFFあbc');
    assert.equal(input.selectionStart, 2);
    assert.equal(dom.activeObservers().length, 1);
    dom.remove('source');
    assert.equal(dom.activeObservers().length, 0);
  } finally { dom.dispose(); }
});

test('different text and correct projections retain their value and caret', () => {
  const dom = fixture();
  try {
    const input = dom.input('source');
    input.value = 'a user edit';
    input.setSelectionRange(2, 5, 'backward');
    dom.sync('source', '\uFEFFabc', true, 1);
    assert.equal(input.value, 'a user edit');
    assert.deepEqual([input.selectionStart, input.selectionEnd], [2, 5]);
    input.value = '\uFEFFabc';
    dom.sync('source', '\uFEFFabc', true, 1);
    assert.deepEqual([input.selectionStart, input.selectionEnd], [2, 5], 'A no-op projection must not reset the caret');
  } finally { dom.dispose(); }
});

test('document refresh synchronously invalidates an old BOM projection before a plain file is assigned', () => {
  const dom = fixture();
  try {
    // The old source was queued while native semantic/IME projection was absent.
    dom.sync('source', '\uFEFFsame\r\nbody', true, 2, 4);
    assert.equal(dom.frames.size, 1);
    const semantic = dom.input('source'), ime = dom.input();
    dom.document.activeElement = ime;

    // BrowserSourceText.Synchronize sends this envelope before assigning the new
    // document. The plain file deliberately equals the old text without its BOM.
    dom.sync('source', '', true);
    for (const input of [semantic, ime]) {
      input.value = 'same\nbody';
      input.setSelectionRange(1, 6, 'backward');
      input.scrollTop = 31;
      input.scrollLeft = 9;
    }
    dom.fire('focusin', ime);
    dom.flush();
    dom.flush();
    for (const input of [semantic, ime]) {
      assert.equal(input.value, 'same\nbody', 'Old microtask/frame/focus work must not restore a removed document BOM');
      assert.deepEqual([input.selectionStart, input.selectionEnd, input.selectionDirection], [1, 6, 'backward']);
      assert.deepEqual([input.scrollTop, input.scrollLeft], [31, 9]);
    }
  } finally { dom.dispose(); }
});

test('equivalent LF and CRLF refreshes publish fresh BOM expectations without a text-change event', () => {
  const dom = fixture();
  try {
    const input = dom.input('source');
    dom.document.activeElement = input;
    const lf = '\uFEFFhead\n🧠\ntail';
    for (const source of [lf.replace(/\n/g, '\r\n'), lf]) {
      input.value = lf;
      input.setSelectionRange(1, 1, 'forward');
      dom.sync('source', lf, true, 1);
      // Text equality can suppress the framework property callback. Refresh
      // still invalidates the previous expectation and queues the final state.
      dom.sync('source', '', true);
      const start = source.indexOf('🧠'), end = source.indexOf('tail');
      dom.sync('source', source, true, start, end - start);
      assert.equal(input.value, lf);
      assert.deepEqual([input.selectionStart, input.selectionEnd], [1, 1],
        'An already correct projection must not move its caret');

      // A later native projection consumes FEFF. Pending work must use this
      // refresh's selection rather than the old document's selection.
      input.value = lf.slice(1);
      input.setSelectionRange(0, 0, 'backward');
      input.scrollTop = 27;
      input.scrollLeft = 4;
      dom.flush();
      assert.equal(input.value, lf);
      assert.deepEqual([input.selectionStart, input.selectionEnd, input.selectionDirection], [6, 9, 'backward']);
      assert.deepEqual([input.scrollTop, input.scrollLeft], [27, 4]);
    }
  } finally { dom.dispose(); }
});

test('surrogate pairs stay intact and managed offsets are clamped to normalized input', () => {
  const dom = fixture();
  try {
    const input = dom.input('source');
    input.value = '🧠\nα';
    dom.sync('source', '\uFEFF🧠\r\nα', false, 3, 100);
    assert.equal(input.value, '\uFEFF🧠\nα');
    assert.deepEqual([input.selectionStart, input.selectionEnd], [3, 5]);
  } finally { dom.dispose(); }
});

test('unload cancels frames and a replacement registration does not inherit pending text', () => {
  const dom = fixture();
  try {
    dom.sync('source', '\uFEFFold');
    dom.remove('source');
    assert.equal(dom.frames.size, 0);
    const input = dom.input('source');
    input.value = 'old';
    dom.sync('source', '\uFEFFnew');
    dom.flush();
    assert.equal(input.value, 'old');
    assert.deepEqual(dom.listenerCounts(), {
      focusin: { capture: 1, bubble: 0 },
      beforeinput: { capture: 1, bubble: 0 },
      input: { capture: 1, bubble: 1 },
      compositionstart: { capture: 1, bubble: 0 },
      compositionend: { capture: 1, bubble: 1 }
    }, 'A replacement installs exactly one handler in each required event phase');
    dom.remove('source');
    assert.ok([...dom.listeners.values()].every(phases => phases.capture.size === 0 && phases.bubble.size === 0));
  } finally { dom.dispose(); }
});

test('invalid source payloads fail before installing lifecycle listeners', () => {
  const dom = fixture();
  try {
    assert.throws(() => syncSourceText('source', '{}'), TypeError);
    assert.throws(() => syncSourceText('', '"text"'), TypeError);
    assert.equal(dom.listeners.size, 0);
  } finally { dom.dispose(); }
});

function nativeComposition(dom, input, value) {
  dom.fire('compositionstart', input, { isTrusted: true });
  dom.fire('beforeinput', input, { isTrusted: true, isComposing: true });
  input.value = value;
  input.setSelectionRange(value.length, value.length, 'forward');
  dom.fire('input', input, { isTrusted: true, isComposing: true });
}

function echoWithoutBom(input) {
  input.value = input.value.replace(/^\uFEFF/, '');
  input.setSelectionRange(input.value.length, input.value.length, 'forward');
}

test('trusted multiline input repairs only the exact same-event native echo with its original caret', () => {
  const dom = fixture();
  try {
    const input = dom.input('source');
    dom.document.activeElement = input;
    dom.sync('source', '', true);
    input.value = '\uFEFFfirst\nsecond';
    input.setSelectionRange(2, 5, 'backward');
    dom.fire('input', input, { isTrusted: true }, () => echoWithoutBom(input));
    assert.equal(input.value, '\uFEFFfirst\nsecond');
    assert.deepEqual([input.selectionStart, input.selectionEnd, input.selectionDirection], [2, 5, 'backward']);
    dom.fire('input', input, { isTrusted: true }, () => { input.value = 'A different native value'; });
    assert.equal(input.value, 'A different native value');
  } finally { dom.dispose(); }
});

test('a native composition commit may have an untrusted end after trusted input and retains UTF-16 selection', () => {
  const dom = fixture();
  try {
    const input = dom.input('source');
    dom.document.activeElement = input;
    dom.sync('source', '', true);
    nativeComposition(dom, input, '\uFEFF@startuml\n日本🙂');
    const expected = input.value;
    input.setSelectionRange(expected.length - 2, expected.length, 'backward');
    dom.fire('compositionend', input, {}, () => echoWithoutBom(input));
    assert.equal(input.value, expected, 'Blink native compositionend can be queued without SetTrusted');
    assert.deepEqual([input.selectionStart, input.selectionEnd, input.selectionDirection],
      [expected.length - 2, expected.length, 'backward']);
    input.value = expected;
    dom.fire('compositionend', input, {}, () => echoWithoutBom(input));
    assert.equal(input.value, expected.slice(1), 'A consumed native sequence cannot authorize a later synthetic end');
  } finally { dom.dispose(); }
});

test('standalone and entirely synthetic composition events cannot authorize echo repair', () => {
  const dom = fixture();
  try {
    const input = dom.input('source');
    dom.document.activeElement = input;
    dom.sync('source', '', true);
    input.value = '\uFEFFstandalone';
    dom.fire('compositionend', input, {}, () => echoWithoutBom(input));
    assert.equal(input.value, 'standalone');
    dom.fire('compositionstart', input);
    input.value = '\uFEFFsynthetic';
    dom.fire('input', input, { isComposing: true });
    dom.fire('compositionend', input, {}, () => echoWithoutBom(input));
    assert.equal(input.value, 'synthetic');
  } finally { dom.dispose(); }
});

test('changed or deleted composition values cannot reuse a trusted native reading', () => {
  const dom = fixture();
  try {
    const input = dom.input('source');
    dom.document.activeElement = input;
    dom.sync('source', '', true);
    nativeComposition(dom, input, '\uFEFFnative');
    input.value = '\uFEFFunrelated replacement';
    dom.fire('compositionend', input, {}, () => echoWithoutBom(input));
    assert.equal(input.value, 'unrelated replacement');
    nativeComposition(dom, input, '\uFEFFnative');
    input.value = 'native';
    dom.fire('compositionend', input, {}, () => echoWithoutBom(input));
    assert.equal(input.value, 'native', 'Intentional BOM removal remains authoritative');
  } finally { dom.dispose(); }
});

test('an unfinished or unrelated input phase invalidates the previous trusted composition reading', () => {
  const dom = fixture();
  try {
    const input = dom.input('source');
    dom.document.activeElement = input;
    dom.sync('source', '', true);
    for (const [type, properties] of [
      ['beforeinput', { isTrusted: true, isComposing: true }],
      ['input', { isTrusted: false, isComposing: true }],
      ['input', { isTrusted: true, isComposing: false }],
      ['compositionstart', { isTrusted: true }]
    ]) {
      nativeComposition(dom, input, '\uFEFFnative');
      dom.fire(type, input, properties);
      dom.fire('compositionend', input, {}, () => echoWithoutBom(input));
      assert.equal(input.value, 'native', `${type} ${JSON.stringify(properties)} must not reuse stale native input`);
    }
  } finally { dom.dispose(); }
});

test('composition provenance belongs to the original connected textarea and registration identity', () => {
  const dom = fixture();
  try {
    const input = dom.input('source');
    dom.document.activeElement = input;
    dom.sync('source', '', true);
    nativeComposition(dom, input, '\uFEFForiginal');
    dom.remove('source');
    dom.sync('source', '', true);
    dom.fire('compositionend', input, {}, () => echoWithoutBom(input));
    assert.equal(input.value, 'original', 'Re-registering an owner cannot inherit old composition evidence');
    nativeComposition(dom, input, '\uFEFFdetached');
    const replacement = dom.input('source');
    replacement.value = '\uFEFFreplacement';
    input.isConnected = false;
    dom.document.activeElement = replacement;
    dom.fire('compositionend', input, {}, () => echoWithoutBom(input));
    dom.fire('compositionend', replacement, {}, () => echoWithoutBom(replacement));
    assert.equal(input.value, 'detached');
    assert.equal(replacement.value, 'replacement', 'Same public ID does not transfer native composition to a new element');
  } finally { dom.dispose(); }
});

test('a shared IME textarea loses composition ownership when its native source focus leaves', () => {
  const dom = fixture();
  try {
    const input = dom.input();
    dom.document.activeElement = input;
    dom.sync('source', '', true);
    nativeComposition(dom, input, '\uFEFFshared');
    dom.sync('source', '', false);
    dom.fire('compositionend', input, {}, () => echoWithoutBom(input));
    assert.equal(input.value, 'shared');
  } finally { dom.dispose(); }
});
