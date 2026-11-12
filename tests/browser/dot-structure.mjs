import { activateControl, fillControl } from './native-controls.mjs';
import assert from 'node:assert/strict';

export async function verifyDotStructuralAuthoring(page, source) {
  await page.setViewportSize({width:1480,height:960});
  await activateControl(page, page.getByRole('button', {name:'Split',exact:true}));
  await source.waitFor({state:'visible'});
  const paper = page.locator('.paper').first(), surface = page.locator('.surface').first();
  const node = id => paper.locator(`[data-dot-id=${JSON.stringify('node:' + id)}]`);
  const edge = (from,to) => paper.locator(`[data-dot-kind="edge"][data-dot-from-id=${JSON.stringify(from)}][data-dot-to-id=${JSON.stringify(to)}]`);
  const button = name => page.getByRole('button',{name,exact:true});
  const field = name => page.getByRole('textbox',{name,exact:true});
  // Native inspector commands use real keyboard activation; graph hits and
  // endpoint gestures below retain ordinary SVG pointer actionability.
  const tableAction = name => button(name).press('Enter');
  const inspectorTableAction = name => activateControl(page, button(name));
  const selectedCell = async (row,column,text) => {
    const picker = page.getByRole('combobox',{name:'Table cell',exact:true});
    try {
      // A canvas hit rebuilds the inspector. Wait for its selected field and
      // the native head's actual layout before opening the logical popup.
      await page.waitForFunction(value => document.querySelector('input[aria-label="Table cell text"],textarea[aria-label="Table cell text"]')?.value === value,text);
      await activateControl(page, picker);
      const selected = page.getByRole('listbox',{name:'Table cell',exact:true})
        .getByRole('option',{name:`Row ${row}, column ${column}: ${text}`,exact:true,selected:true});
      await selected.waitFor({state:'attached'});
      assert.equal(await picker.getAttribute('aria-activedescendant'),await selected.getAttribute('id'));
      await picker.press('Escape');
      await selected.waitFor({state:'detached'});
      assert.equal(await field('Table cell text').inputValue(),text,'The inspector resolves the selected cell before applying structure');
    } catch (cause) {
      const snapshot = await page.evaluate(() => {
        const root = document.getElementById('uno-semantics-root');
        const picker = root?.querySelector('[role="combobox"][aria-label="Table cell"]');
        const list = document.getElementById(picker?.getAttribute('aria-controls'));
        const describe = element => {
          if (!element) return null;
          const rect = element.getBoundingClientRect();
          return {id:element.id,role:element.getAttribute('role'),name:element.getAttribute('aria-label'),
            selected:element.getAttribute('aria-selected'),expanded:element.getAttribute('aria-expanded'),
            controls:element.getAttribute('aria-controls'),activeDescendant:element.getAttribute('aria-activedescendant'),
            position:element.getAttribute('aria-posinset'),size:element.getAttribute('aria-setsize'),
            hidden:!!element.closest('[aria-hidden="true"],[hidden],[inert]'),
            value:typeof element.value === 'string' ? element.value.slice(0,256) : null,
            rect:{x:rect.x,y:rect.y,width:rect.width,height:rect.height}};
        };
        return {picker:describe(picker),list:describe(list),
          options:[...(list?.querySelectorAll('[role="option"]') ?? [])].slice(0,16).map(describe),
          text:describe(root?.querySelector('input[aria-label="Table cell text"],textarea[aria-label="Table cell text"]')),
          active:describe(document.activeElement)};
      }).catch(error => ({diagnosticError:error.message}));
      console.error('Selected DOT table cell failure: '+JSON.stringify({row,column,text,snapshot}));
      throw cause;
    }
  };
  const recordText = text => node('a').locator('[data-edit-kind="dot-record-text"]').filter({hasText:new RegExp('^' + text + '$')});
  const cellText = text => node('a').locator('[data-edit-kind="dot-html-text"]').filter({hasText:new RegExp('^' + text + '$')});
  // Source commits precede rendering. Wait for both so a repeated label cannot
  // resolve to an old SVG whose source spans or selection snapshot are stale.
  const change = async (action,expected,message) => {
    const previous = await paper.locator('svg').elementHandle();
    try {
      await action();
      // Native accessibility can replace the semantic TextBox after an edit.
      // Resolve its current public projection instead of polling a detached
      // element whose value can never reflect the committed source.
      await page.waitForFunction(expected =>
        document.querySelector('#uno-semantics-root textarea[aria-label="PlantUML source"]')?.value === expected, expected);
      if (previous) await page.waitForFunction(svg => !svg.isConnected,previous);
      await paper.locator('svg').waitFor();
      assert.equal(await source.inputValue(),expected,message);
      assert.equal(await paper.getAttribute('data-renderer'),'managed');
    } catch (cause) {
      const [actual,selected] = await Promise.all([
        source.inputValue({timeout:1000}).catch(() => '(source unavailable)'),
        page.getByRole('combobox',{name:'Table cell',exact:true}).getAttribute('aria-activedescendant',{timeout:1000}).catch(() => '(no table cell)')
      ]);
      throw new Error(`${message}\nExpected source: ${expected}\nActual source: ${actual}\nSelected cell: ${selected}`,{cause});
    } finally {
      await previous?.dispose();
    }
  };
  const load = value => change(() => fillControl(page, source, value),value,'The fixture renders natively');
  const undo = (value,message) => change(() => surface.press('Control+z'),value,message);
  const choose = async (name,caption) => {
    await activateControl(page, page.getByRole('combobox',{name,exact:true}));
    // Uno popups are hosted outside the inspector's element subtree.
    await activateControl(page, page.getByRole('listbox',{name,exact:true}).getByRole('option',{name:caption,exact:true}));
  };

  const shared = '@startdot\nstrict digraph {\n  rankdir=LR;\n  node [shape=box,color=blue];\n  a[label=Alpha]; b[label=Beta]; c[label=Gamma]; d[label=Destination];\n  a /* keep chain */ -> b -> c [label=Shared,color=red];\n  a -> b [penwidth=2];\n}\n@enddot';
  await load(shared);
  await activateControl(page, edge('a','b').locator('text').filter({hasText:/^Shared$/}));
  await button('Reattach target endpoint').waitFor({state:'visible'});
  const removed = shared.replace('a /* keep chain */ -> b -> c [label=Shared,color=red];',
    'a /* keep chain */ ; b ; c ;\n  b -> c [label=Shared, color=red];;').replace('a -> b [penwidth=2];','a ; b ;;');
  await change(() => activateControl(page, button('Delete edge')),removed,'Deleting a shared strict edge removes all its declarations and preserves the other chain edge');
  assert.equal(await edge('a','b').count(),0);
  assert.equal(await paper.locator('[data-dot-kind="node"]').count(),4);
  assert.equal(await paper.locator('[data-dot-kind="edge"]').count(),1);
  await edge('b','c').locator('text').filter({hasText:/^Shared$/}).waitFor();
  await undo(shared,'One undo restores the shared chain, strict override, defaults and comments byte for byte');

  await activateControl(page, edge('a','b').locator('text').filter({hasText:/^Shared$/}));
  await button('Reattach target endpoint').press('Enter');
  const retargeted = shared.replace('a /* keep chain */ -> b -> c [label=Shared,color=red];',
    'a /* keep chain */ ; b ; c ;\n  a -> d [label=Shared, color=red];\n  b -> c [label=Shared, color=red];;')
    .replace('a -> b [penwidth=2];','a ; b ;\n  a -> d [penwidth=2];;');
  await change(() => activateControl(page, node('d')),retargeted,'Reattaching a shared strict edge updates every contribution without changing its sibling');
  assert.equal(await edge('a','b').count(),0);
  assert.equal(await edge('a','d').getAttribute('data-dot-merged'),'true');
  await edge('b','c').locator('text').filter({hasText:/^Shared$/}).waitFor();
  assert.equal(await paper.locator('[data-dot-kind="edge"]').count(),2);
  await undo(shared,'One undo restores every retargeted strict declaration');

  const record = '@startdot\ndigraph {\n  rankdir=LR;\n  a [shape=record,label="<p> First|<q> Second|Third"]; b[label=Target];\n  a:p:e -> b [tailport="p:e"]; // retained port comment\n}\n@enddot';
  await load(record);
  await activateControl(page, recordText('First'));
  assert.equal(await field('Record field port').inputValue(),'p');
  await activateControl(page, button('Remove record field'));
  await page.getByText('The source changed, or this change would remove a connected port or create an invalid field/table structure.',{exact:true}).waitFor();
  assert.equal(await source.inputValue(),record,'A live port blocks field deletion');
  await activateControl(page, recordText('Second'));
  assert.equal(await field('Record field text').inputValue(),'Second','Clicking a rendered field selects its own inspector row');
  const insertedField = record.replace('<p> First|<q> Second','<p> First|Field|<q> Second');
  await change(() => activateControl(page, button('Insert field before')),insertedField,'Insert follows the selected record field');
  await recordText('Field').waitFor();
  await undo(record,'One undo removes the inserted field; the rejected port deletion added no edit');

  await activateControl(page, recordText('First'));
  const grouped = record.replace('<p> First|<q> Second','{<p> First|<q> Second}');
  await change(() => activateControl(page, button('Group with next field')),grouped,'Grouping changes only the chosen sibling fields');
  await activateControl(page, recordText('First'));
  await choose('Record field',/^0: Field group$/);
  await change(() => activateControl(page, button('Ungroup record fields')),record,'Ungroup restores the original record syntax');
  await undo(grouped,'One undo restores the removed group');
  await undo(record,'One undo restores the original grouping operation');

  await activateControl(page, recordText('First'));
  await fillControl(page, field('Record field port'), 'renamed');
  const renamedPort = record.replace('<p> First','<renamed> First').replace('a:p:e -> b [tailport="p:e"];',
    'a:renamed:e ; b ;\n  a:renamed:e -> b [tailport="renamed:e"];;');
  await change(() => activateControl(page, button('Apply field port')),renamedPort,'Port rename updates both endpoint and tailport syntax in one source transaction');
  await activateControl(page, recordText('First'));
  assert.equal(await field('Record field port').inputValue(),'renamed');
  assert.equal(await edge('a','b').count(),1);
  await undo(record,'One undo restores the field port and every edge reference exactly');

  const table = "@startdot\ndigraph {\n  a [shape=plain,label=<<TABLE BORDER='0' CELLBORDER='1'><TR><TD PORT='p'>Alpha</TD><TD>Beta</TD></TR><!-- keep table --><TR><TD>Gamma</TD><TD>Delta</TD></TR></TABLE>>];\n  b; a:p -> b;\n}\n@enddot";
  await load(table);
  await activateControl(page, cellText('Gamma'));
  await selectedCell(2,1,'Gamma');
  const insertedRow = table.replace('<TR><TD>Gamma','<TR><TD>Cell</TD><TD>Cell</TD></TR><TR><TD>Gamma');
  await change(() => inspectorTableAction('Insert row above'),insertedRow,'A new row preserves unrelated markup, comments and attribute quoting');
  assert.equal(await cellText('Cell').count(),2);
  await activateControl(page, cellText('Cell').first());
  await selectedCell(2,1,'Cell');
  await change(() => tableAction('Remove table row'),table,'Removing the inserted row restores the original label');
  await undo(insertedRow,'One undo restores the deleted table row');
  await undo(table,'One undo removes the inserted table row');

  await activateControl(page, cellText('Alpha'));
  await selectedCell(1,1,'Alpha');
  const insertedColumn = table.replace('<TD>Beta','<TD>Cell</TD><TD>Beta').replace('<TD>Delta','<TD>Cell</TD><TD>Delta');
  await change(() => inspectorTableAction('Insert column after'),insertedColumn,'Column insertion updates each row atomically');
  assert.equal(await cellText('Cell').count(),2);
  await activateControl(page, cellText('Cell').first());
  await selectedCell(1,2,'Cell');
  await change(() => tableAction('Remove table column'),table,'Removing the inserted column retains the live port');
  await undo(insertedColumn,'One undo restores the deleted column in every row');
  await undo(table,'One undo removes the inserted column from every row');

  await activateControl(page, cellText('Alpha'));
  await selectedCell(1,1,'Alpha');
  const merged = table.replace("<TD PORT='p'>Alpha</TD><TD>Beta</TD>","<TD PORT='p' COLSPAN=\"2\">Alpha<BR/>Beta</TD>");
  await change(() => tableAction('Merge with next cell'),merged,'Merge keeps the first port and both text values');
  await activateControl(page, cellText('Alpha'));
  await selectedCell(1,1,'AlphaBeta');
  const split = merged.replace("<TD PORT='p' COLSPAN=\"2\">Alpha<BR/>Beta</TD>","<TD PORT='p' >Alpha<BR/>Beta</TD><TD>Cell</TD>");
  await change(() => tableAction('Split spanning cell'),split,'Split keeps original content and port in the first cell');
  await cellText('Cell').waitFor();
  assert.equal(await edge('a','b').count(),1);
  await undo(merged,'One undo restores the spanning cell');
  await undo(table,'One undo restores the original separate cells and exact markup');

  const plainEdge = '@startdot\ndigraph { a->b->c[label=Shared]; }\n@enddot';
  await load(plainEdge);
  await activateControl(page, edge('a','b').locator('text').filter({hasText:/^Shared$/}));
  const edgeTable = '<TABLE BORDER="0" CELLBORDER="1" CELLSPACING="0"><TR><TD>Cell</TD><TD>Cell</TD></TR><TR><TD>Cell</TD><TD>Cell</TD></TR></TABLE>';
  const createdTable = plainEdge.replace('a->b->c[label=Shared];',`a;b;c;\n  a -> b [label=<${edgeTable}>];\n  b -> c [label=Shared];;`);
  await change(() => activateControl(page, button('Create table cells')),createdTable,'Creating an edge table isolates the selected edge from its shared label');
  assert.equal(await edge('a','b').locator('[data-edit-kind="dot-html-text"]').count(),4);
  await edge('b','c').locator('text').filter({hasText:/^Shared$/}).waitFor();
  assert.equal(await paper.locator('[data-dot-kind="edge"]').count(),2);
  await undo(plainEdge,'One undo restores the original plain shared edge label and chain');
  console.log('Verified shared DOT edge delete/retarget, record add/group/port integrity, HTML row/column/merge/split, edge table creation, exact source retention and atomic undo through the native inspector.');
}
