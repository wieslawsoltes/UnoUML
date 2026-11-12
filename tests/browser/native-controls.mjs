import assert from 'node:assert/strict';

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
      const target = await locator.evaluate(element => ({
        attached: element.isConnected, native: !!element.closest('#uno-semantics-root')
      }));
      if (!target.attached) {
        state = { detached: true };
      } else if (!target.native) {
        return locator.click(options);
      } else {
        assert.ok(!options.force, 'Native controls must pass visibility, enabled, stability and canvas hit checks');
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
            return {
              x, y, rect,
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
    } catch (error) {
      // Uno may replace semantic nodes while rebuilding the native visual tree.
      // Re-resolve only that transient detachment; real actionability/selector
      // errors still fail, and every retry shares the original deadline.
      if (!/element is not attached to the DOM/i.test(error.message)) throw error;
      state = { detached: true };
    }
    await page.waitForTimeout(Math.min(25, remaining() || 25));
  }
  assert.ok(state?.actionable, `Native control was not actionable on the Uno canvas: ${JSON.stringify(state)}`);
  if (options.trial) return;

  const modifiers = options.modifiers ?? [];
  try {
    for (const key of modifiers) await page.keyboard.down(key);
    await page.mouse.click(state.x, state.y, {
      button: options.button,
      clickCount: options.clickCount,
      delay: options.delay
    });
  } finally {
    for (const key of [...modifiers].reverse()) await page.keyboard.up(key);
  }
}

/** Replace native TextBox text through the same selection path as keyboard users. */
export async function fillControl(page, locator, value, options = {}) {
  await locator.waitFor({ state: 'attached', timeout: options.timeout });
  if (!await locator.evaluate(element => !!element.closest('#uno-semantics-root'))) {
    return locator.fill(value, options);
  }

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
}
