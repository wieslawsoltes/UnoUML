import { mkdir, writeFile } from 'node:fs/promises';
import { resolve } from 'node:path';

let traceSequence = 0;

// Keep failure evidence readable through GitHub's log API even when the ZIP
// artifact cannot be extracted. Descriptions are interned by actual DOM object
// identity; values stay short, and both ends of a bounded event stream survive.
function compactNativeInputTrace(name, sequence, failure, report) {
  const available = report.events ?? [];
  let count = Math.min(96, available.length);
  for (;;) {
    const selected = available.length <= count ? available
      : [...available.slice(0, Math.floor(count / 2)), ...available.slice(-Math.ceil(count / 2))];
    const nodes = new Map();
    const short = value => typeof value === 'string' ? value.slice(0, 80) : value ?? null;
    const prefix = value => value?.prefix ? String.fromCharCode(...value.prefix.slice(0, 16)) : null;
    const describe = value => {
      if (!value) return null;
      if (!nodes.has(value.identity)) nodes.set(value.identity, {
        identity: value.identity, id: short(value.id), tag: short(value.tag),
        automationId: short(value.automationId), name: short(value.name)
      });
      return {
        node: value.identity, attached: value.connected,
        ...(value.value ? { length: value.value.length, prefix: prefix(value.value) } : {}),
        ...(value.start !== null || value.end !== null ? { selection: [value.start, value.end] } : {}),
        ...(value.disabled === true ? { disabled: true } : {}),
        ...(value.hidden ? { hidden: true } : {})
      };
    };
    const events = selected.map(entry => ({
      ms: Math.round(entry.elapsedMs * 100) / 100, phase: entry.phase, type: entry.type,
      documentFocused: entry.documentFocused,
      ...(entry.event ? {
        key: short(entry.event.key), code: short(entry.event.code),
        ctrl: entry.event.ctrl, meta: entry.event.meta, shift: entry.event.shift, alt: entry.event.alt,
        trusted: entry.event.trusted, prevented: entry.event.defaultPrevented,
        composing: entry.event.composing, inputType: short(entry.event.inputType),
        ...(entry.event.data ? { dataLength: entry.event.data.length, dataPrefix: prefix(entry.event.data) } : {})
      } : {}),
      target: describe(entry.target), current: describe(entry.current),
      active: describe(entry.active), sharedInput: describe(entry.sharedInput),
      ...(entry.managed !== undefined ? { managed: entry.managed } : {})
    }));
    const text = JSON.stringify({
      name: short(name), sequence, failure: String(failure?.message ?? failure).slice(0, 2000),
      id: short(report.id), automationId: short(report.automationId),
      captureError: typeof report.captureError === 'string' ? report.captureError.slice(0, 2000) : undefined,
      elapsedMs: report.elapsedMs,
      dropped: (report.dropped ?? 0) + available.length - selected.length,
      nodes: [...nodes.values()], events
    });
    if (text.length <= 48000 || count <= 8) return text;
    count = Math.max(8, count - 8);
  }
}

/**
 * Observe one failing native input sequence without changing its input path.
 * The action runs once. Listeners are passive; no focus, value, selection,
 * default-action, readiness or retry behavior is added by this diagnostic.
 */
export async function traceNativeInput(page, locator, name, action, { artifactDir = process.env.SCREENSHOT_DIR, failureOnly = false } = {}) {
  if (!/^[a-z0-9-]+$/.test(name)) throw new Error('Native input trace name must be a safe artifact stem');
  const sequence = ++traceSequence;
  const capture = await locator.evaluateHandle(async original => {
    const doc = original.ownerDocument;
    const root = original.closest('#uno-semantics-root');
    const automationId = original.getAttribute('xamlautomationid');
    const id = original.id;
    let readManaged = () => null;
    if (automationId) {
      try {
        const diagnostics = await import(new URL('uml/native-input-diagnostics.js', doc.baseURI).href);
        readManaged = () => diagnostics.readTextBoxDiagnostics(automationId);
      } catch (error) {
        const message = String(error?.message ?? error);
        readManaged = () => ({ error: message });
      }
    }
    const started = performance.now();
    const identities = new WeakMap();
    let identity = 0, active = true, total = 0;
    const first = [], tail = [], counts = {}, frames = new Set();
    const types = ['keydown', 'keyup', 'beforeinput', 'input', 'select', 'selectionchange',
      'focusin', 'focusout', 'compositionstart', 'compositionupdate', 'compositionend'];
    const number = element => {
      if (!element || typeof element !== 'object') return null;
      if (!identities.has(element)) identities.set(element, ++identity);
      return identities.get(element);
    };
    const units = value => typeof value === 'string'
      ? { length: value.length, prefix: value.slice(0, 32).split('').map(char => char.charCodeAt(0)) } : null;
    const current = () => automationId
      ? root?.querySelector('input[xamlautomationid="' + CSS.escape(automationId) + '"],textarea[xamlautomationid="' + CSS.escape(automationId) + '"]')
      : doc.getElementById(id);
    const describe = element => !element ? null : {
      identity: number(element), id: element.id ?? null, tag: element.localName ?? null,
      automationId: element.getAttribute?.('xamlautomationid') ?? null,
      name: element.getAttribute?.('aria-label') ?? null,
      connected: element.isConnected, semantic: !!element.closest?.('#uno-semantics-root'),
      hidden: !!element.closest?.('[aria-hidden="true"], [hidden], [inert]'),
      value: units(element.value), start: element.selectionStart ?? null, end: element.selectionEnd ?? null,
      direction: element.selectionDirection ?? null,
      disabled: element.disabled ?? null, readOnly: element.readOnly ?? null
    };
    const record = (phase, event = null) => {
      if (!active) return;
      const type = event?.type ?? null;
      const entry = {
        elapsedMs: performance.now() - started, phase, type,
        documentFocused: doc.hasFocus(), visibility: doc.visibilityState,
        event: event ? {
          trusted: event.isTrusted, defaultPrevented: event.defaultPrevented,
          key: event.key ?? null, code: event.code ?? null,
          ctrl: event.ctrlKey ?? null, meta: event.metaKey ?? null,
          shift: event.shiftKey ?? null, alt: event.altKey ?? null,
          repeat: event.repeat ?? null, composing: event.isComposing ?? null,
          inputType: event.inputType ?? null, data: units(event.data)
        } : null,
        target: describe(event?.target), active: describe(doc.activeElement),
        original: describe(original), current: describe(current()),
        sharedInput: describe(doc.getElementById('uno-input')),
        // Synchronous public native state at the actual keyboard boundary.
        // Keep reads limited to these phases; ordinary focus/input events remain passive DOM evidence.
        ...((phase === 'begin' || phase === 'end' ||
          (type === 'keydown' && (phase === 'capture' || phase === 'bubble')))
          ? { managed: readManaged() } : {})
      };
      counts[phase + ':' + (type ?? '')] = (counts[phase + ':' + (type ?? '')] ?? 0) + 1;
      total++;
      if (first.length < 96) first.push(entry);
      else { if (tail.length === 96) tail.shift(); tail.push(entry); }
    };
    const captureEvent = event => {
      record('capture', event);
      queueMicrotask(() => record('microtask', event));
      const frame = requestAnimationFrame(() => { frames.delete(frame); record('frame', event); });
      frames.add(frame);
    };
    const bubbleEvent = event => record('bubble', event);
    for (const type of types) {
      doc.addEventListener(type, captureEvent, { capture: true, passive: true });
      doc.addEventListener(type, bubbleEvent, { passive: true });
    }
    let previous = current();
    const observer = new MutationObserver(() => {
      const next = current();
      if (next !== previous) { previous = next; record('node-change'); }
    });
    if (root) observer.observe(root, { childList: true, subtree: true });
    record('begin');
    return {
      stop: () => {
        record('end');
        active = false;
        observer.disconnect();
        for (const type of types) {
          doc.removeEventListener(type, captureEvent, true);
          doc.removeEventListener(type, bubbleEvent);
        }
        for (const frame of frames) cancelAnimationFrame(frame);
        frames.clear();
        return {
          url: doc.URL, id, automationId, elapsedMs: performance.now() - started,
          counts, dropped: total - first.length - tail.length, events: [...first, ...tail]
        };
      }
    };
  });
  let failure;
  try {
    return await action();
  } catch (error) {
    failure = { message: String(error.message), stack: error.stack };
    throw error;
  } finally {
    let report;
    try { report = await capture.evaluate(value => value.stop()); }
    catch (error) { report = { captureError: String(error.message) }; }
    try { await capture.dispose(); }
    catch (error) { report.disposeError = String(error.message); }
    if (failure) console.error('Native input failure trace: ' + compactNativeInputTrace(name, sequence, failure, report));
    if (artifactDir && (!failureOnly || failure)) {
      try {
        await mkdir(artifactDir, { recursive: true });
        await writeFile(resolve(artifactDir, name + (failureOnly ? '-' + sequence : '') + '-input.json'), JSON.stringify({ name, sequence, failure, ...report }, null, 2));
      } catch (error) {
        // Diagnostics must not replace the action's original failure.
        console.warn(`Could not save ${name} native input trace: ${error.message}`);
      }
    }
  }
}
