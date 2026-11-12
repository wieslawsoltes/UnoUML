import { clickControl, fillControl } from './native-controls.mjs';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';

// Run against the published .NET/Uno host. These paths must complete with no JVM endpoint.
export async function verifyPorts(page) {
  const click = (name, role = 'button') => clickControl(page, page.getByRole(role,{name,exact:true}));
  await page.setViewportSize({width:1390,height:860});
  await click('Split');
  const editor = page.locator('textarea:visible').filter({visible:true});
  const source = editor.nth(await editor.evaluateAll(xs => xs.findIndex(x=>x.value.startsWith('@start'))));
  const sequence = '@startuml\nparticipant Client\nparticipant Server\nautonumber\nClient -> Server : Open connection\nnewpage Delivery\nServer --> Client : Deliver receipt\n@enduml';
  await fillControl(page, source, sequence);
  const picker = page.getByRole('combobox',{name:'Sequence sheet'});
  await picker.waitFor();
  assert.equal(await picker.locator('option').count(),3);
  await picker.selectOption('2');
  const paper = page.locator('.paper').first();
  assert.equal(await paper.getAttribute('data-renderer'),'managed');
  assert.match(await paper.textContent(),/Deliver receipt/);
  assert.doesNotMatch(await paper.textContent(),/Open connection/);
  await click('Export');
  const dl = page.waitForEvent('download');
  await click('Vector image (.svg)', 'menuitem');
  const downloaded = await dl;
  assert.match(downloaded.suggestedFilename(),/-sheet-2\.svg$/);
  const exported = await readFile(await downloaded.path(),'utf8');
  assert.match(exported,/Deliver receipt/);
  assert.doesNotMatch(exported,/Open connection|sheet-picker/);
  await click('Export');
  const pngDownload = page.waitForEvent('download');
  await click('Raster image (.png)', 'menuitem');
  const png = await pngDownload;
  assert.match(png.suggestedFilename(),/-sheet-2\.png$/);
  assert.equal((await readFile(await png.path())).subarray(1,4).toString(),'PNG');
  await picker.selectOption('0');
  assert.equal(await paper.locator('svg[data-sequence-page]').count(),2);
  await clickControl(page, paper.locator('svg[data-sequence-page="1"] .participant-head[data-qualified-name="Client"]'));
  await page.waitForFunction(() => {
    const host = [...document.querySelectorAll('[id]')].find(e=>e.shadowRoot?.querySelector('.paper'));
    const root = host.shadowRoot;
    const head = root.querySelector('svg[data-sequence-page="1"] .participant-head[data-qualified-name="Client"]');
    const selection = root.querySelector('.selection');
    return !selection.hidden && Math.abs(selection.getBoundingClientRect().top - head.getBoundingClientRect().top + 3) < 1;
  });
  const printed = await page.evaluate(async () => {
    const ui = await import(new URL('uml/viewport.js',document.baseURI));
    const host = [...document.querySelectorAll('[id]')].find(e=>e.shadowRoot?.querySelector('.paper'));
    const descriptor = Object.getOwnPropertyDescriptor(HTMLIFrameElement.prototype,'contentWindow');
    let sheets;
    // Capture the document handed to the browser print dialog, then close it.
    Object.defineProperty(HTMLIFrameElement.prototype,'contentWindow',{
      configurable:true,
      get() {
        const child = descriptor.get.call(this);
        if (child) child.print = () => {
          sheets = [...child.document.querySelectorAll('section')].map(section => ({
            text:section.textContent, pageBreak:section.style.breakBefore,
            positioned:section.querySelector('svg').hasAttribute('y')
          }));
          child.dispatchEvent(new child.Event('afterprint'));
        };
        return child;
      }
    });
    try { await ui.exportDiagram(host.id,'print','sequence.print'); }
    finally { Object.defineProperty(HTMLIFrameElement.prototype,'contentWindow',descriptor); }
    return sheets;
  });
  assert.equal(printed.length,2);
  assert.match(printed[0].text,/Open connection/);
  assert.doesNotMatch(printed[0].text,/Deliver receipt/);
  assert.match(printed[1].text,/Deliver receipt/);
  assert.equal(printed[1].pageBreak,'page');
  assert.ok(printed.every(sheet=>!sheet.positioned));
  await fillControl(page, source, '@startuml\nbroken ???\n@enduml');
  await page.getByText(/Syntax Error/).first().waitFor();
  assert.equal(await picker.isDisabled(),true);
  await picker.dispatchEvent('change');
  await fillControl(page, source, sequence);
  await page.waitForFunction(() => [...document.querySelectorAll('[id]')].some(e=>{
    const sheet=e.shadowRoot?.querySelector('.sheet-picker');
    return sheet && !sheet.hidden && !sheet.disabled;
  }));

  await page.evaluate(async () => {
    const ui = await import(new URL('uml/viewport.js',document.baseURI));
    const host = [...document.querySelectorAll('[id]')].find(e=>e.shadowRoot?.querySelector('.paper'));
    const paper = host.shadowRoot.querySelector('.paper');
    for (const [source, text] of [
      ['@startchen\nentity CUSTOMER {\n Id <<key>>\n}\n@endchen','CUSTOMER'],
      ['@startditaa\n+-------+  +-------+\n| Begin |->| Ready |\n+-------+  +-------+\n@endditaa','Ready'],
      ['@startmath\nsqrt(x^2 + 1)\n@endmath','x'],
      ['@startlatex\n\\frac{a}{b}\n@endlatex','a']
    ]) {
      await ui.render(host.id,source);
      if (paper.dataset.renderer !== 'managed' || !paper.textContent.includes(text) || !paper.querySelector('svg [data-source-line]'))
        throw Error('Managed source-mapped output missing for ' + source.split('\n')[0]);
    }
  });

  const field = page.getByRole('textbox',{name:'Edit diagram label'});
  const surface = page.locator('.surface').first();
  const ditaa = '@startditaa\n+--------+     +-------+\n| Client +---->| Store |\n+--------+     +-------+\n@endditaa';
  await fillControl(page, source, ditaa);
  await paper.locator('[data-qualified-name="ditaa:2:17"] text').dblclick();
  await fillControl(page, field, 'Stores');
  await field.press('Enter');
  assert.equal(await source.inputValue(),ditaa.replace('| Store |','| Stores|'));
  await surface.press('Control+z');
  assert.equal(await source.inputValue(),ditaa);

  const chen = '@startchen\nentity CUSTOMER {\n Id <<key>>\n}\nentity ORDER {\n}\nrelationship PLACES {\n}\nPLACES -1- CUSTOMER\nPLACES -N- ORDER\n@endchen';
  await fillControl(page, source, chen);
  await paper.locator('[data-qualified-name="CUSTOMER"] text').dblclick();
  await fillControl(page, field, 'Customer account');
  await field.press('Enter');
  assert.equal(await source.inputValue(),chen.replace('entity CUSTOMER','entity "Customer account" as CUSTOMER'));
  await paper.locator('[data-qualified-name="CUSTOMER/Id"] text').dblclick();
  await fillControl(page, field, 'Account ID');
  await field.press('Enter');
  assert.ok((await source.inputValue()).includes('"Account ID" as Id <<key>>'));
  await paper.locator('[data-source-line="8"] text').dblclick();
  await fillControl(page, field, '(0,N)');
  await field.press('Enter');
  assert.ok((await source.inputValue()).includes('PLACES -(0,N)- CUSTOMER'));

  const styled = '@startuml\n<style>\nclassDiagram {\n class { BackgroundColor #abcdef; FontColor #123456 }\n arrow { LineColor #550077 }\n}\n</style>\nclass Account\nclass Order\nAccount --> Order : owns\n@enduml';
  await fillControl(page, source, styled);
  const account = paper.locator('[data-qualified-name="Account"]').first();
  await account.waitFor();
  assert.equal(await paper.getAttribute('data-renderer'),'managed');
  assert.equal(await account.locator('rect').first().getAttribute('fill'),'#abcdef');
  await account.locator('text').filter({hasText:/^Account$/}).dblclick();
  await fillControl(page, field, 'Customer account');
  await field.press('Enter');
  assert.equal(await source.inputValue(),styled.replace('class Account','class "Customer account" as Account'));

  const c4 = '@startuml\n!include <C4/C4_Component>\nPerson(user, "Customer")\nSystem_Boundary(site, "Booking") {\n  Container_Boundary(api, "API") {\n    Component(ctrl, "Controller", "C#", "Handles bookings")\n    ComponentDb(data, "Storage", "SQL")\n  }\n}\nRel(user, ctrl, "Calls", "HTTPS")\nBiRel_R(ctrl, data, "Reads", "SQL")\n@enduml';
  await fillControl(page, source, c4);
  await paper.locator('[data-c4-alias="ctrl"] text').filter({hasText:/^Controller$/}).dblclick();
  await fillControl(page, field, 'Request handler');
  await field.press('Enter');
  let updatedC4 = c4.replace('"Controller"','"Request handler"');
  assert.equal(await source.inputValue(),updatedC4);
  await paper.locator('[data-source-line="9"] text').filter({hasText:/^Calls$/}).dblclick();
  await fillControl(page, field, 'Invokes');
  await field.press('Enter');
  updatedC4 = updatedC4.replace('"Calls"','"Invokes"');
  assert.equal(await source.inputValue(),updatedC4);
  await clickControl(page, paper.locator('[data-source-line="9"] text').filter({hasText:/^Invokes$/}));
  const target = await page.getByRole('button',{name:'Reattach target endpoint'}).boundingBox();
  const storage = await paper.locator('[data-c4-alias="data"] text').filter({hasText:/^Storage$/}).boundingBox();
  assert.ok(target && storage);
  await page.mouse.move(target.x+target.width/2,target.y+target.height/2);
  await page.mouse.down();
  await page.mouse.move(storage.x+storage.width/2,storage.y+storage.height/2,{steps:8});
  await page.mouse.up();
  updatedC4 = updatedC4.replace('Rel(user, ctrl,','Rel(user, data,');
  assert.equal(await source.inputValue(),updatedC4);
  const advancedC4 = updatedC4.replace('"Handles bookings")','"Handles bookings", $tags="external")');
  await fillControl(page, source, advancedC4);
  await page.waitForFunction(() => [...document.querySelectorAll('[id]')].some(e=>
    e.shadowRoot?.querySelector('.paper')?.dataset.renderer === 'javascript'));
  const referenceHandler = paper.locator('[data-qualified-name="site.api.ctrl"] text').filter({hasText:/^Request$/}).first();
  await referenceHandler.dblclick();
  await fillControl(page, field, 'Dispatch handler');
  await field.press('Enter');
  assert.equal(await source.inputValue(),advancedC4.replace('"Request handler"','"Dispatch handler"'));

  const lifecycle = '@startuml\nparticipant Client\ncreate participant "Worker" as W\nClient -> W ++ : Start worker\nreturn Ready\nref over Client, W : Creation\ndestroy W\n@enduml';
  await fillControl(page, source, lifecycle);
  await paper.locator('.participant-head[data-qualified-name="W"] text').filter({hasText:/^Worker$/}).dblclick();
  await fillControl(page, field, 'Background worker');
  await field.press('Enter');
  let updatedLifecycle = lifecycle.replace('"Worker"','"Background worker"');
  assert.equal(await source.inputValue(),updatedLifecycle);
  await paper.locator('.message[data-source-line="3"] text').filter({hasText:/Start worker/}).dblclick();
  await fillControl(page, field, 'Dispatch');
  await field.press('Enter');
  updatedLifecycle = updatedLifecycle.replace('Start worker','Dispatch');
  assert.equal(await source.inputValue(),updatedLifecycle);
  await paper.locator('.message[data-source-line="4"] text').filter({hasText:/Ready/}).dblclick();
  await fillControl(page, field, 'Completed');
  await field.press('Enter');
  updatedLifecycle = updatedLifecycle.replace('return Ready','return Completed');
  assert.equal(await source.inputValue(),updatedLifecycle);
  await paper.locator('[data-source-line="5"] text').filter({hasText:/^Creation$/}).dblclick();
  await fillControl(page, field, 'Worker lifecycle');
  await field.press('Enter');
  assert.equal(await source.inputValue(),updatedLifecycle.replace(' : Creation',' : Worker lifecycle'));

  const chooser = page.waitForEvent('filechooser');
  const importing = page.evaluate(async () => (await import(new URL('uml/viewport.js',document.baseURI))).importIncludes(false));
  const originalInclude = "' Imported model\r\nclass IncludedInvoice\r\n";
  const changedInclude = originalInclude.replace('class IncludedInvoice','class "Invoice record" as IncludedInvoice');
  await (await chooser).setFiles({name:'model.iuml',mimeType:'text/plain',buffer:Buffer.from(originalInclude)});
  assert.equal(await importing,1);
  const input = '@startuml\n!include model.iuml\nclass Order\nOrder --> IncludedInvoice\n@enduml';
  await fillControl(page, source, input);
  const invoice = paper.locator('[data-qualified-name="IncludedInvoice"]').first();
  await invoice.waitFor();
  assert.equal(await paper.getAttribute('data-renderer'),'managed');
  assert.equal(await invoice.getAttribute('data-source-file'),'model.iuml');
  await invoice.locator('text').filter({hasText:'IncludedInvoice'}).first().dblclick();
  await fillControl(page, field, 'Invoice record');
  await field.press('Enter');
  await paper.locator('[data-qualified-name="IncludedInvoice"] text').filter({hasText:/^Invoice record$/}).first().waitFor();
  assert.equal(await source.inputValue(),input);
  assert.equal(await page.evaluate(async () => (await import(new URL('uml/viewport.js',document.baseURI))).readInclude('model.iuml')), changedInclude);
  await surface.press('Control+z');
  await paper.locator('[data-qualified-name="IncludedInvoice"] text').filter({hasText:/^IncludedInvoice$/}).first().waitFor();
  await clickControl(page, invoice.locator('text').filter({hasText:'IncludedInvoice'}).first());
  await clickControl(page, page.getByRole('button', {name:'Edit included source…',exact:true}));
  assert.equal(await page.getByRole('textbox',{name:'Edit diagram label'}).count(),0);
  assert.equal(await source.inputValue(),input);
  await page.getByRole('dialog',{name:'Edit imported include',exact:true}).waitFor();
  const includeEditor = page.getByRole('textbox',{name:'Imported include source'});
  await includeEditor.waitFor({state:'visible'});
  assert.equal(await includeEditor.inputValue(),originalInclude.replaceAll('\r\n','\n'));
  assert.equal(await includeEditor.evaluate(n => n.selectionStart),"' Imported model\n".length);
  await fillControl(page, includeEditor, changedInclude.replaceAll('\r\n','\n'));
  await click('Apply');
  await paper.locator('[data-qualified-name="IncludedInvoice"] text').filter({hasText:/^Invoice record$/}).first().waitFor();
  assert.equal(await source.inputValue(),input);
  await clickControl(page, paper.locator('[data-qualified-name="IncludedInvoice"] text').filter({hasText:/^Invoice record$/}).first());
  await clickControl(page, page.getByRole('button', {name:'Edit included source…',exact:true}));
  await includeEditor.waitFor({state:'visible'});
  assert.equal(await includeEditor.inputValue(),changedInclude.replaceAll('\r\n','\n'));
  const [includedFile] = await Promise.all([
    page.waitForEvent('download'),
    clickControl(page, page.getByRole('button', {name:'Download include',exact:true}))
  ]);
  assert.equal(includedFile.suggestedFilename(),'model.iuml');
  assert.equal(await readFile(await includedFile.path(),'utf8'),changedInclude);
  await includeEditor.press('Control+z');
  assert.equal(await includeEditor.inputValue(),originalInclude.replaceAll('\r\n','\n'));
  await click('Cancel');
  assert.equal(await page.evaluate(async () => (await import(new URL('uml/viewport.js',document.baseURI))).readInclude('model.iuml')), changedInclude);
  console.log('Verified Chen/Ditaa label editing, cardinalities, native/reference C4 macro editing, connector reattachment, sequence lifecycle labels, modern styles, AsciiMath, TeX, include provenance, repeated sequence selection, SVG/PNG sheet exports and print pagination.');
}
