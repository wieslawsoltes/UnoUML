import test from 'node:test';
import assert from 'node:assert/strict';
import { syncSourceText, removeSourceText } from '../src/UnoUML.App/wwwroot/uml/source-text.js';

function fixture() {
  const names = ['document', 'HTMLTextAreaElement', 'CSS', 'queueMicrotask', 'requestAnimationFrame', 'cancelAnimationFrame'];
  const saved = new Map(names.map(name => [name, Object.getOwnPropertyDescriptor(globalThis, name)]));
  const semantic = new Map(), listeners = new Map(), microtasks = [], frames = new Map(), registrations = new Set();
  let frameId = 0;
  class TextArea {
    constructor(id = null) {
      this.automationId = id;
      this.isConnected = true;
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
  const document = {
    activeElement: null,
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
    CSS: { escape: value => value },
    queueMicrotask: callback => microtasks.push(callback),
    requestAnimationFrame: callback => { const id = ++frameId; frames.set(id, callback); return id; },
    cancelAnimationFrame: id => frames.delete(id)
  });
  return {
    document, semantic, listeners, frames,
    input(id = null) {
      const input = new TextArea(id);
      if (id) semantic.set(id, input);
      return input;
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

test('late semantic projection and focus are repaired without tree observers or polling', () => {
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
