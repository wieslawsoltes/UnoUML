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
    fire(type, target) {
      const event = { type, target, isTrusted: false };
      for (const phase of ['capture', 'bubble'])
        for (const handler of listeners.get(type)?.[phase] ?? []) handler(event);
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
