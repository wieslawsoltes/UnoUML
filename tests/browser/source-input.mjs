import assert from 'node:assert/strict';
import { mkdir, writeFile } from 'node:fs/promises';
import { resolve } from 'node:path';

// Exercise trusted Chromium text/IME events in a separate realm. The target
// listener models Uno's observed synchronous native string echo: TextDecoder
// consumes one leading BOM after managed code has read the correct input.
export async function verifySourceInput(page, { artifactDir } = {}) {
  const moduleUrl = await page.evaluate(() => new URL('uml/source-text.js', document.baseURI).href);
  const id = 'unouml-source-input-fixture';
  await page.evaluate(async id => {
    const iframe = document.createElement('iframe');
    iframe.id = id;
    iframe.title = 'Source text native echo regression';
    iframe.style.cssText = 'position:fixed;left:0;top:0;width:620px;height:420px;background:white;z-index:2147483647';
    const loaded = new Promise(resolve => iframe.onload = resolve);
    iframe.srcdoc = '<!doctype html><body><div id="uno-semantics-root"></div><button id="outside-focus">Outside editor</button></body>';
    document.body.append(iframe);
    await loaded;
  }, id);
  const frame = await (await page.locator('#' + id).elementHandle()).contentFrame();
  const report = { assertions: 0, cases: [], events: [] };
  const equal = (actual, expected, message) => { report.assertions++; assert.deepEqual(actual, expected, message); };
  const check = (value, message) => { report.assertions++; assert.ok(value, message); };
  const settle = () => frame.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
  let ime;
  try {
    await frame.evaluate(async url => {
      globalThis.sourceModule = await import(url);
      const state = globalThis.sourceInputFixture = {
        owner: 'source-input-owner', mode: 'echo', events: [], oldInput: null,
        decode: value => {
          const bytes = new Uint8Array(value.length * 2);
          for (let index = 0; index < value.length; index++) {
            const code = value.charCodeAt(index);
            bytes[index * 2] = code & 255;
            bytes[index * 2 + 1] = code >>> 8;
          }
          return new TextDecoder('utf-16le').decode(bytes);
        }
      };
      state.attach = input => {
        const nativeEcho = event => {
          const record = {
            type: event.type, trusted: event.isTrusted, composing: !!event.isComposing,
            target: input.id, mode: state.mode, before: input.value,
            start: input.selectionStart, end: input.selectionEnd
          };
          state.events.push(record);
          if (state.events.length > 128) state.events.shift();
          // The tagged semantic TextBox ignores intermediate IME input and
          // publishes the committed value at compositionend.
          if (event.type === 'input' && event.isComposing) {
            record.ignored = true;
            record.after = input.value;
            return;
          }
          input.value = state.mode === 'different' ? 'Native replacement' : state.decode(input.value);
          record.after = input.value;
          if (state.mode === 'unload') sourceModule.removeSourceText(state.owner);
          if (state.mode === 'renew') {
            sourceModule.removeSourceText(state.owner);
            sourceModule.syncSourceText(state.owner, '""', true);
          }
          if (state.mode === 'replace') {
            state.oldInput = input;
            const replacement = state.create('source-editor', state.owner);
            replacement.value = input.value;
            input.replaceWith(replacement);
          }
        };
        input.addEventListener('input', nativeEcho);
        input.addEventListener('compositionend', nativeEcho);
        return input;
      };
      state.create = (id, owner) => {
        const input = document.createElement('textarea');
        input.id = id;
        input.setAttribute('aria-label', id);
        if (owner) input.setAttribute('xamlautomationid', owner);
        input.style.cssText = 'display:block;width:500px;height:100px;font:14px monospace';
        return state.attach(input);
      };
      state.reset = (registered = true, mode = 'echo', value = '') => {
        sourceModule.removeSourceText(state.owner);
        state.mode = mode;
        state.events = [];
        state.oldInput = null;
        const input = state.create('source-editor', state.owner);
        input.value = value;
        document.getElementById('uno-semantics-root').replaceChildren(input);
        if (registered) sourceModule.syncSourceText(state.owner, '""', true);
      };
    }, moduleUrl);
    const editor = frame.locator('#source-editor');
    const reset = (registered = true, mode = 'echo', value = '') =>
      frame.evaluate(({ registered, mode, value }) => sourceInputFixture.reset(registered, mode, value), { registered, mode, value });
    const trace = () => frame.evaluate(() => sourceInputFixture.events);
    const recordCase = async name => {
      report.cases.push(name);
      report.events.push({ name, trace: await trace() });
    };
    const insert = async text => {
      await editor.focus();
      await page.keyboard.press('ControlOrMeta+A');
      await page.keyboard.insertText(text);
    };
    const source = '\uFEFF@startuml\nclass A {\n  label : λ🙂\n}\n@enduml\n';

    await reset(false);
    await insert(source);
    equal(await editor.inputValue(), source.slice(1), 'The unregistered native echo reproduces the observed leading-BOM loss');
    const negative = await trace();
    check(negative.length > 0 && negative.every(event => event.trusted), 'The regression control uses real trusted browser input');
    check(negative[0].before.startsWith('\uFEFF'), 'Native code initially receives the correct BOM-bearing input');
    check(!negative[0].after.startsWith('\uFEFF'), 'The simulated outgoing TextDecoder echo removes the first BOM');
    await recordCase('unregistered trusted-input regression control');

    await reset();
    await insert(source);
    equal(await editor.inputValue(), source, 'A first multiline insertion preserves its BOM without a prior managed BOM snapshot');
    const positive = await trace();
    check(positive.length > 0 && positive.every(event => event.trusted && event.before.startsWith('\uFEFF')),
      'Every native import in the multiline operation sees the retained BOM');
    check(positive.every(event => !event.after.startsWith('\uFEFF')), 'The native echo defect was exercised on every import');
    equal(await editor.evaluate(input => [input.selectionStart, input.selectionEnd]), [source.length, source.length],
      'The repaired native echo preserves the actual committed UTF-16 caret');
    const at = source.indexOf('label');
    await editor.evaluate((input, at) => input.setSelectionRange(at, at), at);
    await page.keyboard.insertText('new_');
    equal(await editor.inputValue(), source.slice(0, at) + 'new_' + source.slice(at), 'A later edit preserves the BOM and its insertion position');
    equal(await editor.evaluate(input => [input.selectionStart, input.selectionEnd]), [at + 4, at + 4],
      'Echo repair does not move a middle-of-document caret to the end');
    await recordCase('trusted multiline insert and subsequent caret-preserving edit');

    await reset(true, 'echo', source);
    await frame.evaluate(source => sourceModule.syncSourceText(sourceInputFixture.owner, JSON.stringify(source), true, 0, 1), source);
    await editor.focus();
    await editor.evaluate(input => input.setSelectionRange(0, 1));
    await page.keyboard.press('Delete');
    equal(await editor.inputValue(), source.slice(1), 'Deleting the selected BOM is an intentional native edit');
    await frame.locator('#outside-focus').focus();
    await editor.focus();
    await settle();
    equal(await editor.inputValue(), source.slice(1), 'Refocus and queued managed projection never undo deliberate BOM deletion');
    check((await trace()).every(event => !event.before.startsWith('\uFEFF')), 'The deletion commit is observed without a BOM');
    await recordCase('intentional BOM removal invalidates prior managed expectation');

    await reset(true, 'different');
    await insert('\uFEFFone line');
    equal(await editor.inputValue(), 'Native replacement', 'A nonmatching native value is not overwritten by a captured input snapshot');
    await reset();
    await frame.evaluate(() => {
      const input = document.getElementById('source-editor');
      input.value = '\uFEFFuntrusted';
      input.dispatchEvent(new InputEvent('input', { bubbles: true, inputType: 'insertText', data: '\uFEFFuntrusted' }));
    });
    equal(await editor.inputValue(), 'untrusted', 'Untrusted synthetic input cannot create an authoritative repair snapshot');
    equal((await trace()).map(event => event.trusted), [false], 'The synthetic negative case is explicitly distinguishable from real input');
    await recordCase('exact-echo match and trusted-event boundaries');

    for (const mode of ['unload', 'renew', 'replace']) {
      await reset(true, mode);
      await insert('\uFEFFlifetime');
      equal(await editor.inputValue(), 'lifetime', `A ${mode} during native input invalidates the captured editor/registration`);
      if (mode === 'replace') {
        equal(await frame.evaluate(() => sourceInputFixture.oldInput.isConnected), false, 'The event target was actually detached');
        equal(await frame.evaluate(() => sourceInputFixture.oldInput.value), 'lifetime', 'Detached input is not repaired after replacement');
      }
      await recordCase(`${mode} before document-bubble repair`);
    }
    await reset();
    await frame.evaluate(source => {
      const input = document.getElementById('source-editor');
      input.value = source.slice(1);
      sourceModule.syncSourceText(sourceInputFixture.owner, JSON.stringify(source), true, source.length, 0);
      sourceModule.removeSourceText(sourceInputFixture.owner);
      input.value = source.slice(1);
      sourceModule.syncSourceText(sourceInputFixture.owner, '""', true);
    }, source);
    await settle();
    equal(await editor.inputValue(), source.slice(1), 'An old queued microtask/frame cannot restore a BOM into a renewed plain-source registration');
    await recordCase('queued managed projection lifetime');

    await reset();
    await frame.evaluate(() => {
      const input = sourceInputFixture.create('foreign-editor', 'another-source');
      document.getElementById('uno-semantics-root').append(input);
    });
    await frame.locator('#foreign-editor').focus();
    await page.keyboard.insertText('\uFEFFforeign');
    equal(await frame.locator('#foreign-editor').inputValue(), 'foreign', 'An explicitly identified different source editor is never claimed as shared IME input');
    equal(await editor.inputValue(), '', 'Foreign editing leaves the registered semantic source unchanged');
    await frame.evaluate(() => {
      const input = sourceInputFixture.create('shared-ime', null);
      document.getElementById('uno-semantics-root').append(input);
    });
    await frame.locator('#shared-ime').focus();
    await page.keyboard.insertText('\uFEFFshared\nIME');
    equal(await frame.locator('#shared-ime').inputValue(), '\uFEFFshared\nIME', 'Public native focus ownership permits repair of the active shared IME textarea');
    equal(await editor.inputValue(), '', 'Shared IME repair changes only the actual event target');
    await recordCase('semantic editor and shared IME ownership');

    const imeBase = '\uFEFF@startuml\n';
    await reset(true, 'echo', imeBase);
    await editor.focus();
    await editor.evaluate(input => input.setSelectionRange(input.value.length, input.value.length));
    ime = await page.context().newCDPSession(page);
    await ime.send('Input.imeSetComposition', { text: '日', selectionStart: 1, selectionEnd: 1 });
    const composingValue = await editor.inputValue();
    equal(composingValue, imeBase + '日', 'Chromium created a real in-progress IME reading');
    check((await trace()).some(event => event.trusted && event.composing && event.ignored),
      'The fixture exercises trusted intermediate composition input');
    await frame.evaluate(value => sourceModule.syncSourceText(sourceInputFixture.owner, JSON.stringify('\uFEFF' + value), true, value.length, 0), composingValue);
    await settle();
    equal(await editor.inputValue(), composingValue, 'A managed projection whose stripped value matches cannot overwrite active composition');
    await page.keyboard.insertText('日本');
    equal(await editor.inputValue(), imeBase + '日本', 'A committed IME edit preserves one BOM through the native echo');
    check((await trace()).some(event => event.type === 'compositionend' && event.trusted), 'The commit traversed a real browser compositionend event');
    equal(await editor.evaluate(input => [input.selectionStart, input.selectionEnd]), [imeBase.length + 2, imeBase.length + 2],
      'Committed IME selection remains at its native UTF-16 endpoint');
    await recordCase('trusted IME reading and committed echo');
    report.pass = true;
  } catch (error) {
    report.pass = false;
    report.error = error.message;
    report.stack = error.stack;
    try { report.failureEvents = await frame.evaluate(() => sourceInputFixture?.events); } catch { }
    throw error;
  } finally {
    if (ime) await ime.detach().catch(() => {});
    if (artifactDir) {
      await mkdir(artifactDir, { recursive: true });
      await writeFile(resolve(artifactDir, 'source-input.json'), JSON.stringify(report, null, 2));
    }
    await page.locator('#' + id).evaluate(element => element.remove());
  }
  console.log('Verified source input projection:', JSON.stringify({ assertions: report.assertions, cases: report.cases, pass: report.pass }));
  return report;
}
