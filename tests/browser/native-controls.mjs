import assert from 'node:assert/strict';

/** Activate Uno's public, keyboard-accessible entry after the window is ready. */
export async function activateAccessibility(page) {
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
  await locator.waitFor({ state: 'attached', timeout: options.timeout });
  if (!await locator.evaluate(element => !!element.closest('#uno-semantics-root'))) {
    return locator.click(options);
  }

  assert.ok(!options.force, 'Native controls must pass visibility, enabled, stability and canvas hit checks');
  const timeout = options.timeout ?? 30_000;
  const deadline = timeout === 0 ? Infinity : Date.now() + timeout;
  const remaining = () => timeout === 0 ? 0 : Math.max(1, deadline - Date.now());
  await locator.waitFor({ state: 'visible', timeout: remaining() });
  await locator.scrollIntoViewIfNeeded({ timeout: remaining() });

  let state;
  while (Date.now() < deadline) {
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
      if (state.attached && state.stable && state.rect.width > 0 && state.rect.height > 0
          && state.inViewport && state.onCanvas) break;
    } else {
      state = { disabled: true };
    }
    await page.waitForTimeout(Math.min(25, remaining() || 25));
  }
  assert.ok(state?.attached && state.stable && state.rect.width > 0 && state.rect.height > 0
    && state.inViewport && state.onCanvas,
  `Native control was not actionable on the Uno canvas: ${JSON.stringify(state)}`);
  assert.equal(await locator.isEnabled(), true, 'Native control remains enabled before pointer input');
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
