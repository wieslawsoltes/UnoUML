import assert from 'node:assert/strict';
import { traceNativeInput } from './native-input-trace.mjs';

const nativeControlActions = new WeakMap();

/** Bounded test diagnostics; this never changes the application's input path. */
export function getNativeControlDiagnostics(page) {
  return [...(nativeControlActions.get(page) ?? [])];
}

function recordNativeAction(page, action, before) {
  let actions = nativeControlActions.get(page);
  if (!actions) nativeControlActions.set(page, actions = []);
  const entry = { at: new Date().toISOString(), action, before };
  if (actions.length === 128) actions.shift();
  actions.push(entry);
  return entry;
}

async function completeNativeAction(page, entry) {
  try {
    entry.after = await page.evaluate(id => {
      const element = document.getElementById(id);
      const rect = element?.getBoundingClientRect();
      const scroll = [];
      for (let parent = element?.parentElement; parent; parent = parent.parentElement) {
        if (parent.scrollHeight > parent.clientHeight || parent.scrollWidth > parent.clientWidth)
          scroll.push({ id: parent.id, top: parent.scrollTop, left: parent.scrollLeft,
            height: parent.clientHeight, extent: parent.scrollHeight });
      }
      return {
        attached: !!element,
        rect: rect ? { x: rect.x, y: rect.y, width: rect.width, height: rect.height } : null,
        scroll,
        dialogs: [...document.querySelectorAll('[role="dialog"][aria-modal="true"]')]
          .filter(dialog => !dialog.closest('[aria-hidden="true"]'))
          .map(dialog => dialog.getAttribute('aria-label')).slice(0, 8)
      };
    }, entry.before.id);
  } catch (error) {
    entry.diagnosticsError = String(error.message).slice(0, 2048);
  }
}

// Uno's virtualized ComboBox options can retain their initial zero bounds even
// while the native popup is visible. The public option focus and keyboard path
// still targets the actual ComboBoxItem; validate that live relationship before
// using it. Ordinary controls must continue through the canvas pointer gates.
function nativeVirtualOptionState(element) {
  const root = element.closest('#uno-semantics-root');
  const listbox = element.closest('[role="listbox"]');
  const rect = element.getBoundingClientRect();
  const exposed = node => {
    if (!node?.isConnected || node.closest('[aria-hidden="true"], [hidden], [inert]')) return false;
    for (let current = node; current; current = current.parentElement) {
      const style = getComputedStyle(current);
      if (style.display === 'none' || style.visibility === 'hidden' || style.visibility === 'collapse') return false;
    }
    return true;
  };
  const enabled = node => !!node && !node.closest('[aria-disabled="true"], [disabled]');
  const owners = root && listbox?.id ? [...root.querySelectorAll('[role="combobox"][aria-expanded="true"]')]
    .filter(owner => (owner.getAttribute('aria-controls') ?? '').split(/\s+/).includes(listbox.id)) : [];
  const owner = owners.length === 1 ? owners[0] : null;
  const ownerRect = owner?.getBoundingClientRect();
  const position = Number(element.getAttribute('aria-posinset'));
  const size = Number(element.getAttribute('aria-setsize'));
  const validPosition = Number.isInteger(position) && Number.isInteger(size) && position > 0 && position <= size;
  return {
    virtualOption: true,
    actionable: element.getAttribute('role') === 'option' && rect.width === 0 && rect.height === 0
      && !!element.id && !!root && listbox?.closest('#uno-semantics-root') === root
      && exposed(element) && exposed(listbox) && exposed(owner) && enabled(element) && enabled(owner)
      && ownerRect?.width > 0 && ownerRect?.height > 0 && validPosition,
    attached: element.isConnected,
    focused: element.ownerDocument.activeElement === element,
    id: element.id,
    name: element.getAttribute('aria-label'),
    listbox: listbox?.id,
    owner: owner?.getAttribute('aria-label'),
    owners: owners.length,
    position, size,
    rect: { x: rect.x, y: rect.y, width: rect.width, height: rect.height }
  };
}

/** Activate Uno's public, keyboard-accessible entry after the window is ready. */
export async function activateAccessibility(page) {
  // The SVG and semantic tree can exist while Uno's startup splash still
  // covers the window. Wait for the same visible readiness a user needs.
  await page.locator('#loading').waitFor({ state: 'hidden', timeout: 120000 });
  const root = page.locator('#uno-semantics-root');
  await root.waitFor({ state: 'attached' });
  if (await root.locator(':scope > *').count() === 0) {
    await page.getByRole('button', { name: 'Enable accessibility', exact: true }).press('Enter');
  }
  await root.locator('button, [role="button"], input, textarea').first().waitFor({ state: 'visible' });
}

/**
 * Uno draws native controls on its canvas. Their public semantic elements have
 * pointer-events:none, so use their bounds to send real browser pointer input
 * to that canvas. DOM/SVG controls retain Playwright's normal actionability.
 */
export async function clickControl(page, locator, options = {}) {
  const timeout = options.timeout ?? 30_000;
  const deadline = timeout === 0 ? Infinity : Date.now() + timeout;
  const remaining = () => timeout === 0 ? 0 : Math.max(1, deadline - Date.now());
  let state;
  while (Date.now() < deadline) {
    try {
      await locator.waitFor({ state: 'attached', timeout: remaining() });
      const target = await locator.evaluate(element => {
        const rect = element.getBoundingClientRect();
        return {
          attached: element.isConnected, native: !!element.closest('#uno-semantics-root'),
          virtualOption: element.getAttribute('role') === 'option' && rect.width === 0 && rect.height === 0
        };
      });
      if (!target.attached) {
        state = { detached: true };
      } else if (!target.native) {
        return locator.click(options);
      } else {
        assert.ok(!options.force, 'Native controls must pass visibility, enabled, stability and canvas hit checks');
        if (target.virtualOption) {
          assert.ok((options.button ?? 'left') === 'left' && (options.clickCount ?? 1) === 1
            && !options.position && !(options.modifiers?.length),
          'A zero-bound native option supports one unmodified selection through its public keyboard contract');
          state = await locator.evaluate(nativeVirtualOptionState);
          state.actionable &&= await locator.isEnabled();
          if (state.actionable) {
            if (options.trial) break;
            await locator.focus({ timeout: remaining() });
            state = await locator.evaluate(nativeVirtualOptionState);
            state.actionable &&= state.focused && await locator.isEnabled();
            if (state.actionable) break;
          }
        } else {
          await locator.waitFor({ state: 'visible', timeout: remaining() });
          await locator.scrollIntoViewIfNeeded({ timeout: remaining() });
          if (await locator.isEnabled()) {
            state = await locator.evaluate(async (element, position) => {
              const read = () => {
                const rect = element.getBoundingClientRect();
                return { x: rect.x, y: rect.y, width: rect.width, height: rect.height };
              };
              await new Promise(resolve => requestAnimationFrame(resolve));
              const first = read();
              await new Promise(resolve => requestAnimationFrame(resolve));
              const rect = read();
              const x = rect.x + (position?.x ?? rect.width / 2);
              const y = rect.y + (position?.y ?? rect.height / 2);
              const hit = document.elementFromPoint(x, y);
              const scroll = [];
              for (let parent = element.parentElement; parent; parent = parent.parentElement) {
                if (parent.scrollHeight > parent.clientHeight || parent.scrollWidth > parent.clientWidth)
                  scroll.push({ id: parent.id, top: parent.scrollTop, left: parent.scrollLeft,
                    height: parent.clientHeight, extent: parent.scrollHeight });
              }
              return {
                x, y, rect, scroll, id: element.id,
                attached: element.isConnected && !!element.closest('#uno-semantics-root'),
                stable: Object.keys(rect).every(key => rect[key] === first[key]),
                inViewport: x >= 0 && y >= 0 && x < innerWidth && y < innerHeight,
                onCanvas: hit === document.getElementById('uno-canvas'),
                hit: hit ? `${hit.tagName.toLowerCase()}#${hit.id}` : null,
                name: element.getAttribute('aria-label')
              };
            }, options.position ?? null);
            state.actionable = state.attached && state.stable && state.rect.width > 0 && state.rect.height > 0
              && state.inViewport && state.onCanvas && await locator.isEnabled();
            if (state.actionable) break;
          } else {
            state = { disabled: true };
          }
        }
      }
    } catch (error) {
      // Uno may replace semantic nodes while rebuilding the native visual tree.
      // Re-resolve only that transient detachment; real actionability/selector
      // errors still fail, and every retry shares the original deadline.
      if (!/element is not attached to the DOM/i.test(error.message)) throw error;
      state = { detached: true };
    }
    await page.waitForTimeout(Math.min(25, remaining() || 25));
  }
  assert.ok(state?.actionable, `Native control was not actionable: ${JSON.stringify(state)}`);
  if (options.trial) return;
  if (state.virtualOption) {
    // Focus was verified above. Enter is dispatched once through the browser;
    // Uno's ComboBox popup commits the focused item and closes itself.
    const entry = recordNativeAction(page, 'Enter', state);
    try { return await locator.press('Enter', { timeout: remaining(), delay: options.delay }); }
    finally { await completeNativeAction(page, entry); }
  }

  const modifiers = options.modifiers ?? [];
  const entry = recordNativeAction(page, 'pointer', state);
  try {
    for (const key of modifiers) await page.keyboard.down(key);
    await page.mouse.click(state.x, state.y, {
      button: options.button,
      clickCount: options.clickCount,
      delay: options.delay
    });
  } finally {
    for (const key of [...modifiers].reverse()) await page.keyboard.up(key);
    await completeNativeAction(page, entry);
  }
}

// Semantic bounds can remain relative to the wrong native ancestor. Keyboard
// activation targets the public control identity and does not claim those
// bounds are correct; clickControl remains the explicit native pointer path.
function nativeCommandState(element) {
  const root = element.closest('#uno-semantics-root');
  const tag = element.localName;
  const role = element.getAttribute('role') ?? (tag === 'button' ? 'button'
    : tag === 'input' && element.type === 'checkbox' ? 'checkbox' : null);
  const key = ['button', 'checkbox', 'switch'].includes(role) ? 'Space'
    : ['combobox', 'menuitem', 'menuitemcheckbox', 'menuitemradio'].includes(role) ? 'Enter' : null;
  let exposed = element.isConnected && !element.closest('[aria-hidden="true"], [hidden], [inert]');
  for (let current = element; exposed && current; current = current.parentElement) {
    const style = getComputedStyle(current);
    exposed = style.display !== 'none' && style.visibility !== 'hidden' && style.visibility !== 'collapse';
  }
  const enabled = !element.closest('[aria-disabled="true"], [disabled]') && !element.matches(':disabled');
  // Playwright routes keyboard input to this page's target, including parallel
  // browser contexts. Its exact active element identifies the recipient;
  // top-level window activation is diagnostic, not a cross-page input lock.
  const focused = element.ownerDocument.activeElement === element;
  const rect = element.getBoundingClientRect();
  const active = element.ownerDocument.activeElement;
  return {
    native: !!root, attached: element.isConnected, exposed, enabled, focused,
    documentFocused: element.ownerDocument.hasFocus(),
    visible: rect.width > 0 && rect.height > 0,
    supported: key !== null, key, role, tag, id: element.id,
    name: element.getAttribute('aria-label'),
    active: active ? { id: active.id, tag: active.localName, role: active.getAttribute('role') } : null,
    rect: { x: rect.x, y: rect.y, width: rect.width, height: rect.height }
  };
}

/**
 * Activate a native command through its real focus and keyboard contract.
 * DOM/SVG interactions keep their original pointer behavior. Native toggle
 * buttons expose role button with aria-pressed and share the Space key path.
 */
export async function activateControl(page, locator, options = {}) {
  const timeout = options.timeout ?? 30_000;
  const deadline = timeout === 0 ? Infinity : Date.now() + timeout;
  const remaining = () => timeout === 0 ? 0 : Math.max(1, deadline - Date.now());
  let state;
  while (Date.now() < deadline) {
    try {
      await locator.waitFor({ state: 'attached', timeout: remaining() });
      state = await locator.evaluate(nativeCommandState);
      if (!state.attached) {
        state = { detached: true };
      } else if (!state.native || state.role === 'option') {
        // The existing option path validates its live ComboBox owner and item
        // position before native focus + Enter for zero-bound logical options.
        return clickControl(page, locator, options);
      } else {
        const unsupportedOptions = Object.keys(options).filter(key => !['timeout', 'delay', 'trial'].includes(key));
        assert.equal(unsupportedOptions.length, 0,
          `Native keyboard activation does not accept pointer options: ${unsupportedOptions.join(', ')}`);
        assert.ok(state.supported, `Native control has no command activation contract: ${JSON.stringify(state)}`);
        // A native semantic head can precede its control template/layout. In
        // particular, opening a zero-size ComboBox can precede item realization.
        // Logical options have their separate validated path above; commands
        // must be laid out before keyboard activation even when bounds are not
        // reliable pointer coordinates.
        await locator.waitFor({ state: 'visible', timeout: remaining() });
        state = await locator.evaluate(nativeCommandState);
        state.actionable = state.exposed && state.visible && state.enabled && await locator.isEnabled();
        if (state.actionable) {
          if (options.trial) return;
          await locator.focus({ timeout: remaining() });
          // Re-resolve after focus: a native visual-tree update can replace the
          // semantic node. Only the exact current focused target may receive a
          // key; retrying before dispatch cannot invoke the command twice.
          state = await locator.evaluate(nativeCommandState);
          state.actionable = state.native && state.attached && state.supported
            && state.exposed && state.visible && state.enabled && state.focused;
          if (state.actionable) break;
        }
      }
    } catch (error) {
      if (!/element is not attached to the DOM/i.test(error.message)) throw error;
      state = { detached: true };
    }
    await page.waitForTimeout(Math.min(25, remaining() || 25));
  }
  assert.ok(state?.actionable, `Native command was not keyboard actionable: ${JSON.stringify(state)}`);
  const entry = recordNativeAction(page, state.key, state);
  try {
    // Do not use locator.press here: the verified focus is authoritative, and
    // a locator retry must never send the same command to a replacement node.
    return await page.keyboard.press(state.key, { delay: options.delay });
  } finally {
    await completeNativeAction(page, entry);
  }
}

/** Replace native TextBox text through the same selection path as keyboard users. */
export async function fillControl(page, locator, value, options = {}) {
  await locator.waitFor({ state: 'attached', timeout: options.timeout });
  if (!await locator.evaluate(element => !!element.closest('#uno-semantics-root'))) {
    return locator.fill(value, options);
  }

  return traceNativeInput(page, locator, 'native-fill', () => fillNativeControl(page, locator, value, options),
    { failureOnly: true });
}

async function fillNativeControl(page, locator, value, options) {
  assert.equal(typeof value, 'string', 'Native textbox replacement requires text');
  assert.ok(!options.force, 'Native textbox replacement must pass visibility and editability checks');
  const timeout = options.timeout ?? 30_000;
  const deadline = timeout === 0 ? Infinity : Date.now() + timeout;
  const remaining = () => timeout === 0 ? 0 : Math.max(1, deadline - Date.now());
  await locator.waitFor({ state: 'visible', timeout: remaining() });
  assert.equal(await locator.isEditable(), true, 'Native textbox must be editable');

  // Locator.fill selects only the DOM value. Uno's semantic TextBox forwards
  // character input to its managed editor, whose selection must change too.
  // A real Select All key updates both before the single replacement edit.
  await locator.focus({ timeout: remaining() });
  await locator.press('ControlOrMeta+A', { timeout: remaining() });
  let state;
  while (Date.now() < deadline) {
    state = await locator.evaluate(element => ({
      attached: element.isConnected,
      focused: element.ownerDocument.activeElement === element,
      id: element.id,
      automationId: element.getAttribute('xamlautomationid'),
      tag: element.localName,
      name: element.getAttribute('aria-label'),
      start: element.selectionStart,
      end: element.selectionEnd,
      length: element.value?.length
    }));
    if (state.attached && state.focused && state.start === 0 && state.end === state.length) break;
    await page.waitForTimeout(Math.min(25, remaining() || 25));
  }
  assert.ok(state?.attached && state.focused && state.start === 0 && state.end === state.length,
    `Native textbox did not select its complete value: ${JSON.stringify(state)}`);
  assert.equal(await locator.isEditable(), true, 'Native textbox remains editable before replacement');

  // Capture only the observed leading-BOM regression. These passive listeners
  // observe the original insertion, including any native projection feedback;
  // they never cancel, modify, redispatch or retry an input event.
  const trace = value.startsWith('\uFEFF') ? recordNativeAction(page, 'fill', {
    ...state, requestedLength: value.length, requestedPrefix: value.slice(0, 16).split('').map(c => c.charCodeAt(0))
  }) : null;
  let traceHandle;
  if (trace) {
    traceHandle = await locator.evaluateHandle(element => {
      const doc = element.ownerDocument, id = element.id, automationId = element.getAttribute('xamlautomationid');
      const events = [], frames = new Set(), started = performance.now();
      let active = true, dropped = 0;
      const types = ['beforeinput', 'input', 'compositionstart', 'compositionupdate', 'compositionend'];
      const current = () => automationId
        ? doc.querySelector('textarea[xamlautomationid="' + CSS.escape(automationId) + '"],input[xamlautomationid="' + CSS.escape(automationId) + '"]')
        : doc.getElementById(id);
      const valueState = input => input ? {
        id: input.id, attached: input.isConnected, length: input.value?.length,
        prefix: typeof input.value === 'string' ? input.value.slice(0, 16).split('').map(c => c.charCodeAt(0)) : null,
        start: input.selectionStart, end: input.selectionEnd
      } : null;
      const belongs = event => event.target === element || event.target.id === id
        || automationId && event.target.getAttribute?.('xamlautomationid') === automationId;
      const record = (event, phase) => {
        if (!active) return false;
        if (events.length === 64) { dropped++; return false; }
        events.push({ elapsedMs: performance.now() - started, phase, type: event.type,
          inputType: event.inputType, composing: event.isComposing, trusted: event.isTrusted,
          dataLength: typeof event.data === 'string' ? event.data.length : null,
          dataPrefix: typeof event.data === 'string' ? event.data.slice(0, 16).split('').map(c => c.charCodeAt(0)) : null,
          target: valueState(event.target), current: valueState(current()) });
        return true;
      };
      const capture = event => {
        if (!belongs(event) || !record(event, 'capture')) return;
        queueMicrotask(() => record(event, 'microtask'));
        const frame = requestAnimationFrame(() => { frames.delete(frame); record(event, 'frame'); });
        frames.add(frame);
      };
      const bubble = event => { if (belongs(event)) record(event, 'bubble'); };
      for (const type of types) {
        doc.addEventListener(type, capture, { capture: true, passive: true });
        doc.addEventListener(type, bubble, { passive: true });
      }
      return { stop: () => {
        active = false;
        for (const type of types) { doc.removeEventListener(type, capture, true); doc.removeEventListener(type, bubble); }
        for (const frame of frames) cancelAnimationFrame(frame);
        return { events, dropped, current: valueState(current()) };
      } };
    });
  }

  try {
    if (value.length) await page.keyboard.insertText(value);
    else await page.keyboard.press('Backspace');

    // Browser textareas normalize line endings; single-line inputs remove them.
    // Never retry insertion: a failed replacement must retain its actual result
    // for the scenario's diagnostics, rather than applying another edit.
    const expected = state.tag === 'textarea' ? value.replace(/\r\n?/g, '\n') : value.replace(/[\r\n]/g, '');
    let actual;
    while (Date.now() < deadline) {
      actual = await locator.inputValue({ timeout: remaining() });
      if (actual === expected) return;
      await page.waitForTimeout(Math.min(25, remaining() || 25));
    }
    assert.equal(actual, expected, 'Native textbox replacement must replace the complete previous value');
  } finally {
    if (traceHandle) {
      try { trace.input = await traceHandle.evaluate(value => value.stop()); }
      catch (error) { trace.inputCaptureError = String(error.message).slice(0, 2048); }
      await traceHandle.dispose();
    }
    if (trace) await completeNativeAction(page, trace);
  }
}
