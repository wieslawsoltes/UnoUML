import assert from 'node:assert/strict';
import { mkdir, writeFile } from 'node:fs/promises';
import { resolve } from 'node:path';

// Exercise the production projector in its own document. Native ownership is a
// synchronous public-state callback; DOM focus and replacement are real browser
// operations, and no retry or input-helper repair is used by these fixtures.
export async function verifyNativeTextBoxFocus(page, { artifactDir } = {}) {
  const moduleUrl = await page.evaluate(() => new URL('uml/native-textboxes.js', document.baseURI).href);
  const id = 'unouml-native-textbox-focus-fixture';
  await page.evaluate(async id => {
    const iframe = document.createElement('iframe');
    iframe.id = id;
    iframe.title = 'Native TextBox replacement focus regression';
    iframe.style.cssText = 'position:fixed;left:0;top:0;width:650px;height:420px;background:white;z-index:2147483647';
    const loaded = new Promise(resolve => iframe.onload = resolve);
    iframe.srcdoc = '<!doctype html><style>body{margin:8px}textarea{width:240px;height:90px}button{display:block;margin:12px}</style>'
      + '<body><div id="uno-semantics-root"></div><button id="foreign">Foreign command</button>'
      + '<div id="background" style="height:100px;background:#eee">Background</div></body>';
    document.body.append(iframe);
    await loaded;
  }, id);
  const handle = await page.locator('#' + id).elementHandle();
  const frame = await handle.contentFrame();
  await handle.dispose();
  const report = { assertions:0, cases:[] };
  const failures = [];
  const equal = (actual, expected, message) => { report.assertions++; assert.deepEqual(actual, expected, message); };
  const settle = () => frame.evaluate(() => new Promise(resolve =>
    requestAnimationFrame(() => requestAnimationFrame(resolve))));
  const active = () => frame.evaluate(() => document.activeElement?.id || document.activeElement?.localName);
  const run = async (name, body) => {
    try { await body(); report.cases.push({name,pass:true}); }
    catch (error) {
      failures.push(error);
      const focus = await frame.evaluate(() => ({active:document.activeElement?.id || document.activeElement?.localName,
        documentFocused:document.hasFocus(),root:document.getElementById('uno-semantics-root')?.innerHTML})).catch(() => null);
      report.cases.push({name,pass:false,error:error.message,stack:error.stack,focus});
    }
  };
  try {
    await frame.evaluate(async url => {
      globalThis.textBoxFocusModule = await import(url);
      globalThis.focusFixture = { generation:0, scope:null };
    }, moduleUrl);
    const prepare = async ({ reader = true, owner = true, focus = true } = {}) => {
      await frame.evaluate(({reader,owner}) => {
        const fixture = focusFixture;
        if (fixture.scope) textBoxFocusModule.endTextBoxScope(fixture.scope.id,fixture.scope.generation);
        const root = document.getElementById('uno-semantics-root');
        root.replaceChildren();
        const scope = fixture.scope = {id:'focus-editor',generation:++fixture.generation,owner,checks:0,focusEvents:0};
        fixture.create = nodeId => {
          const input = document.createElement('textarea');
          input.id = nodeId;
          input.setAttribute('xamlautomationid',scope.id);
          input.setAttribute('aria-label','Focus editor');
          input.value = 'Native🙂 editor\n' + 'long line '.repeat(50) + '\n' + 'additional row\n'.repeat(15);
          input.addEventListener('focusin',() => { scope.focusEvents++; });
          return input;
        };
        root.append(fixture.create('old-editor'));
        const args = [scope.id,scope.generation,true];
        if (reader) args.push(() => { scope.checks++; return scope.owner; });
        textBoxFocusModule.beginTextBoxScope(...args);
      }, {reader,owner});
      if (focus) {
        await frame.locator('#old-editor').focus();
        await frame.waitForFunction(() => document.activeElement === document.getElementById('old-editor'));
      }
      await settle();
    };
    const replace = options => frame.evaluate(options => {
      const fixture = focusFixture, old = document.getElementById('old-editor');
      const current = fixture.create('new-editor');
      if (options?.selection) current.setSelectionRange(...options.selection);
      if (old) old.replaceWith(current);
      else document.getElementById('uno-semantics-root').append(current);
      current.scrollTop = 41;
      current.scrollLeft = 17;
      return {value:current.value,start:current.selectionStart,end:current.selectionEnd,
        direction:current.selectionDirection,top:current.scrollTop,left:current.scrollLeft};
    }, options ?? {});

    await run('native-owned focused replacement preserves the new projection and receives subsequent real input', async () => {
      await prepare();
      await frame.evaluate(() => document.getElementById('old-editor').setSelectionRange(0,3,'forward'));
      const expected = await replace({selection:[8,13,'backward']});
      await settle();
      equal(await active(),'new-editor','The replacement must receive DOM focus while its actual native owner still agrees');
      equal(await frame.evaluate(() => {
        const input = document.getElementById('new-editor');
        return {value:input.value,start:input.selectionStart,end:input.selectionEnd,
          direction:input.selectionDirection,top:input.scrollTop,left:input.scrollLeft};
      }),expected,'Restoration must preserve the replacement node value, UTF-16 selection and scroll rather than replaying the old node');
      equal(await frame.evaluate(() => focusFixture.scope.focusEvents),2,'Focus is transferred once without a reentrant focus loop');
      equal(await frame.evaluate(() => focusFixture.scope.checks > 0),true,'Native focus is checked at the replacement boundary');
      await page.keyboard.insertText('X');
      equal(await frame.locator('#new-editor').inputValue(),
        expected.value.slice(0,8) + 'X' + expected.value.slice(13),'Real input after replacement edits the focused projection at its current selection');
    });

    await run('delayed replacement retains only a witnessed native-owned focus intent', async () => {
      await prepare();
      await frame.evaluate(() => document.getElementById('old-editor').remove());
      await settle();
      equal(await active(),'body','Removal temporarily leaves browser focus on the document');
      await replace();
      await settle();
      equal(await active(),'new-editor','A later matching semantic insertion repairs the witnessed focus gap');
    });

    await run('metadata-only and unwitnessed registrations never acquire focus', async () => {
      await prepare({reader:false});
      await replace();
      await settle();
      equal(await active(),'body','The existing three-argument disabled-state API never restores focus');
      await prepare({focus:false});
      await replace();
      await settle();
      equal(await active(),'body','Native ownership alone is insufficient without the previous DOM focus witness');
    });

    await run('current foreign native ownership cancels replacement focus', async () => {
      await prepare();
      await frame.evaluate(() => { focusFixture.scope.owner = false; });
      await replace();
      await settle();
      equal(await active(),'body','A stale DOM witness cannot override the current native focus owner');
      await frame.evaluate(() => {
        focusFixture.scope.owner = true;
        textBoxFocusModule.updateTextBoxScope(focusFixture.scope.id,focusFixture.scope.generation,true);
      });
      await settle();
      equal(await active(),'body','A previously rejected transfer must not be replayed by an enabled-state update');
    });

    await run('explicit external focus and pointer intent prevent focus theft', async () => {
      await prepare();
      await frame.locator('#foreign').focus();
      await replace();
      await settle();
      equal(await active(),'foreign','A later semantic replacement cannot steal explicit DOM focus');
      await prepare();
      await frame.locator('#background').click();
      await replace();
      await settle();
      equal(await active(),'body','A real background pointer action cancels the old editing focus intent');
    });

    await run('Tab navigation and explicit connected blur cancel replacement recovery', async () => {
      await prepare();
      await page.keyboard.press('Tab');
      equal(await active(),'foreign','Real Tab navigation reaches the next ordinary control');
      await replace();
      await settle();
      equal(await active(),'foreign','Replacement does not reverse keyboard navigation');
      await prepare();
      await frame.evaluate(() => document.getElementById('old-editor').blur());
      await settle();
      await replace();
      await settle();
      equal(await active(),'body','A deliberately blurred connected editor must remain unfocused');
    });

    await run('window blur and composition leave focus under browser and IME ownership', async () => {
      await prepare();
      await frame.evaluate(() => window.dispatchEvent(new Event('blur')));
      await replace();
      await settle();
      equal(await active(),'body','A window blur cancels pending focus restoration');
      await frame.evaluate(() => window.dispatchEvent(new Event('focus')));
      await prepare();
      await frame.evaluate(() => document.getElementById('old-editor').dispatchEvent(new CompositionEvent('compositionstart',{bubbles:true})));
      await replace();
      await settle();
      equal(await active(),'body','Replacing an editor during composition must not transfer its active IME session');
    });

    await run('completed composition re-arms retention only for the current native-owned editor', async () => {
      await prepare();
      const before = await frame.evaluate(() => focusFixture.scope.checks);
      await frame.evaluate(() => {
        const input = document.getElementById('old-editor');
        input.dispatchEvent(new CompositionEvent('compositionstart',{bubbles:true}));
        input.dispatchEvent(new CompositionEvent('compositionend',{bubbles:true}));
      });
      equal(await active(),'old-editor','Composition completion itself must not change focus');
      equal(await frame.evaluate(before => focusFixture.scope.checks > before,before),true,
        'A completed reading re-arms retention only after a fresh native focus query');
      await replace();
      await settle();
      equal(await active(),'new-editor','A later replacement is repaired after a valid completed composition');

      await prepare();
      await frame.evaluate(() => {
        const input = document.getElementById('old-editor');
        input.dispatchEvent(new CompositionEvent('compositionstart',{bubbles:true}));
        focusFixture.scope.owner = false;
        input.dispatchEvent(new CompositionEvent('compositionend',{bubbles:true}));
        focusFixture.scope.owner = true;
      });
      await replace();
      await settle();
      equal(await active(),'body','A rejected composition-end ownership reading cannot authorize a later transfer');
    });

    await run('unload and newer registration generations reject stale replacement work', async () => {
      await prepare();
      await frame.evaluate(() => {
        const scope = focusFixture.scope;
        document.getElementById('old-editor').remove();
        textBoxFocusModule.endTextBoxScope(scope.id,scope.generation);
      });
      await replace();
      await settle();
      equal(await active(),'body','Unloading before mutation delivery cancels the focused lifetime');
      await prepare();
      await frame.evaluate(() => {
        const old = focusFixture.scope;
        document.getElementById('old-editor').remove();
        const scope = focusFixture.scope = {...old,generation:++focusFixture.generation};
        textBoxFocusModule.beginTextBoxScope(scope.id,scope.generation,true,() => scope.owner);
        textBoxFocusModule.endTextBoxScope(old.id,old.generation);
      });
      await replace();
      await settle();
      equal(await active(),'body','A new load with the same public ID cannot inherit an old focus witness');
    });

    await run('modal isolation and disabled scopes reject replacement focus', async () => {
      for (const attribute of ['hidden','inert','aria-hidden']) {
        await prepare();
        await frame.evaluate(attribute => {
          const wrapper = document.createElement('div');
          wrapper.setAttribute(attribute,attribute === 'aria-hidden' ? 'true' : '');
          wrapper.append(focusFixture.create('new-editor'));
          document.getElementById('old-editor').replaceWith(wrapper);
        },attribute);
        await settle();
        equal(await active(),'body','A native owner cannot restore focus through modal ' + attribute + ' isolation');
      }
      await prepare();
      await frame.evaluate(() => {
        const scope = focusFixture.scope;
        textBoxFocusModule.updateTextBoxScope(scope.id,scope.generation,false);
      });
      await replace();
      await settle();
      equal(await active(),'body','Disabling the effective scope cancels its pending replacement focus');
      await frame.evaluate(() => {
        const scope = focusFixture.scope;
        textBoxFocusModule.updateTextBoxScope(scope.id,scope.generation,true);
      });
      await settle();
      equal(await active(),'body','Re-enabling the scope must not revive a cancelled focus witness');
    });

    await run('ambiguous duplicate identities do not choose a focus recipient', async () => {
      await prepare();
      await frame.evaluate(() => {
        const fixture = focusFixture;
        document.getElementById('old-editor').replaceWith(fixture.create('new-editor'),fixture.create('duplicate-editor'));
      });
      await settle();
      equal(await active(),'body','Restoration requires one current matching semantic editor');
    });
  } catch (error) {
    failures.push(error);
    report.cases.push({name:'fixture setup',pass:false,error:error.message,stack:error.stack});
  } finally {
    report.pass = failures.length === 0;
    try {
      await frame.evaluate(() => {
        const scope = globalThis.focusFixture?.scope;
        if (scope) globalThis.textBoxFocusModule?.endTextBoxScope(scope.id,scope.generation);
      });
    } catch {}
    await page.evaluate(id => document.getElementById(id)?.remove(),id);
    if (artifactDir) {
      await mkdir(artifactDir,{recursive:true});
      await writeFile(resolve(artifactDir,'native-textbox-focus.json'),JSON.stringify(report,null,2));
    }
  }
  console.log('Native TextBox focus replacement:',JSON.stringify(report));
  if (failures.length) throw new AggregateError(failures,'Native TextBox replacement focus regressions failed');
  return report;
}
