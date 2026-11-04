import assert from 'node:assert/strict';

export async function verifyDotStructuralAuthoring(page, source) {
  await page.setViewportSize({width:1480,height:960});
  await page.getByText('Split',{exact:true}).last().click();
  await source.waitFor({state:'visible'});
  const paper = page.locator('.paper').first(), surface = page.locator('.surface').first();
  const node = id => paper.locator(`[data-dot-id=${JSON.stringify('node:' + id)}]`);
  const edge = (from,to) => paper.locator(`[data-dot-kind="edge"][data-dot-from-id=${JSON.stringify(from)}][data-dot-to-id=${JSON.stringify(to)}]`);
  const button = name => page.getByRole('button',{name,exact:true});
  const field = name => page.getByRole('textbox',{name,exact:true});
  // A canvas hit rebuilds the XAML inspector. Its lower table actions may
  // scroll while focus settles; keyboard activation targets the named button
  // through the normal Click handler without relying on that moving point.
  const tableAction = name => button(name).press('Enter');
  const selectedCell = async (row,column,text) => {
    await page.getByRole('combobox',{name:'Table cell',exact:true})
      .filter({hasText:`Row ${row}, column ${column}: ${text}`}).waitFor({state:'visible'});
    await page.waitForFunction(value => document.querySelector('[role="textbox"][aria-label="Table cell text"]')?.value === value,text);
    assert.equal(await field('Table cell text').inputValue(),text,'The inspector resolves the selected cell before applying structure');
  };
  const recordText = text => node('a').locator('[data-edit-kind="dot-record-text"]').filter({hasText:new RegExp('^' + text + '$')});
  const cellText = text => node('a').locator('[data-edit-kind="dot-html-text"]').filter({hasText:new RegExp('^' + text + '$')});
  // Source commits precede rendering. Wait for both so a repeated label cannot
  // resolve to an old SVG whose source spans or selection snapshot are stale.
  const change = async (action,expected,message) => {
    const previous = await paper.locator('svg').elementHandle();
    const input = await source.elementHandle();
    try {
      await action();
      await page.waitForFunction(({input,expected}) => input.value === expected,{input,expected});
      if (previous) await page.waitForFunction(svg => !svg.isConnected,previous);
      await paper.locator('svg').waitFor();
      assert.equal(await source.inputValue(),expected,message);
      assert.equal(await paper.getAttribute('data-renderer'),'managed');
    } catch (cause) {
      const [actual,selected] = await Promise.all([
        source.inputValue({timeout:1000}).catch(() => '(source unavailable)'),
        page.getByRole('combobox',{name:'Table cell',exact:true}).textContent({timeout:1000}).catch(() => '(no table cell)')
      ]);
      throw new Error(`${message}\nExpected source: ${expected}\nActual source: ${actual}\nSelected cell: ${selected}`,{cause});
    } finally {
      await input?.dispose();
      await previous?.dispose();
    }
  };
  const load = value => change(() => source.fill(value),value,'The fixture renders natively');
  const undo = (value,message) => change(() => surface.press('Control+z'),value,message);
  const choose = async (name,caption) => {
    await page.getByRole('combobox',{name,exact:true}).click();
    // Uno popups are hosted outside the inspector's element subtree.
    await page.locator('.uno-comboboxitem').filter({hasText:caption}).click();
  };

  const shared = '@startdot\nstrict digraph {\n  rankdir=LR;\n  node [shape=box,color=blue];\n  a[label=Alpha]; b[label=Beta]; c[label=Gamma]; d[label=Destination];\n  a /* keep chain */ -> b -> c [label=Shared,color=red];\n  a -> b [penwidth=2];\n}\n@enddot';
  await load(shared);
  await edge('a','b').locator('text').filter({hasText:/^Shared$/}).click();
  await button('Reattach target endpoint').waitFor({state:'visible'});
  const removed = shared.replace('a /* keep chain */ -> b -> c [label=Shared,color=red];',
    'a /* keep chain */ ; b ; c ;\n  b -> c [label=Shared, color=red];;').replace('a -> b [penwidth=2];','a ; b ;;');
  await change(() => button('Delete edge').click(),removed,'Deleting a shared strict edge removes all its declarations and preserves the other chain edge');
  assert.equal(await edge('a','b').count(),0);
  assert.equal(await paper.locator('[data-dot-kind="node"]').count(),4);
  assert.equal(await paper.locator('[data-dot-kind="edge"]').count(),1);
  await edge('b','c').locator('text').filter({hasText:/^Shared$/}).waitFor();
  await undo(shared,'One undo restores the shared chain, strict override, defaults and comments byte for byte');

  await edge('a','b').locator('text').filter({hasText:/^Shared$/}).click();
  await button('Reattach target endpoint').press('Enter');
  const retargeted = shared.replace('a /* keep chain */ -> b -> c [label=Shared,color=red];',
    'a /* keep chain */ ; b ; c ;\n  a -> d [label=Shared, color=red];\n  b -> c [label=Shared, color=red];;')
    .replace('a -> b [penwidth=2];','a ; b ;\n  a -> d [penwidth=2];;');
  await change(() => node('d').click(),retargeted,'Reattaching a shared strict edge updates every contribution without changing its sibling');
  assert.equal(await edge('a','b').count(),0);
  assert.equal(await edge('a','d').getAttribute('data-dot-merged'),'true');
  await edge('b','c').locator('text').filter({hasText:/^Shared$/}).waitFor();
  assert.equal(await paper.locator('[data-dot-kind="edge"]').count(),2);
  await undo(shared,'One undo restores every retargeted strict declaration');

  const record = '@startdot\ndigraph {\n  rankdir=LR;\n  a [shape=record,label="<p> First|<q> Second|Third"]; b[label=Target];\n  a:p:e -> b [tailport="p:e"]; // retained port comment\n}\n@enddot';
  await load(record);
  await recordText('First').click();
  assert.equal(await field('Record field port').inputValue(),'p');
  await button('Remove record field').click();
  await page.getByText('The source changed, or this change would remove a connected port or create an invalid field/table structure.',{exact:true}).waitFor();
  assert.equal(await source.inputValue(),record,'A live port blocks field deletion');
  await recordText('Second').click();
  assert.equal(await field('Record field text').inputValue(),'Second','Clicking a rendered field selects its own inspector row');
  const insertedField = record.replace('<p> First|<q> Second','<p> First|Field|<q> Second');
  await change(() => button('Insert field before').click(),insertedField,'Insert follows the selected record field');
  await recordText('Field').waitFor();
  await undo(record,'One undo removes the inserted field; the rejected port deletion added no edit');

  await recordText('First').click();
  const grouped = record.replace('<p> First|<q> Second','{<p> First|<q> Second}');
  await change(() => button('Group with next field').click(),grouped,'Grouping changes only the chosen sibling fields');
  await recordText('First').click();
  await choose('Record field',/^0: Field group$/);
  await change(() => button('Ungroup record fields').click(),record,'Ungroup restores the original record syntax');
  await undo(grouped,'One undo restores the removed group');
  await undo(record,'One undo restores the original grouping operation');

  await recordText('First').click();
  await field('Record field port').fill('renamed');
  const renamedPort = record.replace('<p> First','<renamed> First').replace('a:p:e -> b [tailport="p:e"];',
    'a:renamed:e ; b ;\n  a:renamed:e -> b [tailport="renamed:e"];;');
  await change(() => button('Apply field port').click(),renamedPort,'Port rename updates both endpoint and tailport syntax in one source transaction');
  await recordText('First').click();
  assert.equal(await field('Record field port').inputValue(),'renamed');
  assert.equal(await edge('a','b').count(),1);
  await undo(record,'One undo restores the field port and every edge reference exactly');

  const table = "@startdot\ndigraph {\n  a [shape=plain,label=<<TABLE BORDER='0' CELLBORDER='1'><TR><TD PORT='p'>Alpha</TD><TD>Beta</TD></TR><!-- keep table --><TR><TD>Gamma</TD><TD>Delta</TD></TR></TABLE>>];\n  b; a:p -> b;\n}\n@enddot";
  await load(table);
  await cellText('Gamma').click();
  await selectedCell(2,1,'Gamma');
  const insertedRow = table.replace('<TR><TD>Gamma','<TR><TD>Cell</TD><TD>Cell</TD></TR><TR><TD>Gamma');
  await change(() => button('Insert row above').click(),insertedRow,'A new row preserves unrelated markup, comments and attribute quoting');
  assert.equal(await cellText('Cell').count(),2);
  await cellText('Cell').first().click();
  await selectedCell(2,1,'Cell');
  await change(() => tableAction('Remove table row'),table,'Removing the inserted row restores the original label');
  await undo(insertedRow,'One undo restores the deleted table row');
  await undo(table,'One undo removes the inserted table row');

  await cellText('Alpha').click();
  await selectedCell(1,1,'Alpha');
  const insertedColumn = table.replace('<TD>Beta','<TD>Cell</TD><TD>Beta').replace('<TD>Delta','<TD>Cell</TD><TD>Delta');
  await change(() => button('Insert column after').click(),insertedColumn,'Column insertion updates each row atomically');
  assert.equal(await cellText('Cell').count(),2);
  await cellText('Cell').first().click();
  await selectedCell(1,2,'Cell');
  await change(() => tableAction('Remove table column'),table,'Removing the inserted column retains the live port');
  await undo(insertedColumn,'One undo restores the deleted column in every row');
  await undo(table,'One undo removes the inserted column from every row');

  await cellText('Alpha').click();
  await selectedCell(1,1,'Alpha');
  const merged = table.replace("<TD PORT='p'>Alpha</TD><TD>Beta</TD>","<TD PORT='p' COLSPAN=\"2\">Alpha<BR/>Beta</TD>");
  await change(() => tableAction('Merge with next cell'),merged,'Merge keeps the first port and both text values');
  await cellText('Alpha').click();
  await selectedCell(1,1,'AlphaBeta');
  const split = merged.replace("<TD PORT='p' COLSPAN=\"2\">Alpha<BR/>Beta</TD>","<TD PORT='p' >Alpha<BR/>Beta</TD><TD>Cell</TD>");
  await change(() => tableAction('Split spanning cell'),split,'Split keeps original content and port in the first cell');
  await cellText('Cell').waitFor();
  assert.equal(await edge('a','b').count(),1);
  await undo(merged,'One undo restores the spanning cell');
  await undo(table,'One undo restores the original separate cells and exact markup');

  const plainEdge = '@startdot\ndigraph { a->b->c[label=Shared]; }\n@enddot';
  await load(plainEdge);
  await edge('a','b').locator('text').filter({hasText:/^Shared$/}).click();
  const edgeTable = '<TABLE BORDER="0" CELLBORDER="1" CELLSPACING="0"><TR><TD>Cell</TD><TD>Cell</TD></TR><TR><TD>Cell</TD><TD>Cell</TD></TR></TABLE>';
  const createdTable = plainEdge.replace('a->b->c[label=Shared];',`a;b;c;\n  a -> b [label=<${edgeTable}>];\n  b -> c [label=Shared];;`);
  await change(() => button('Create table cells').click(),createdTable,'Creating an edge table isolates the selected edge from its shared label');
  assert.equal(await edge('a','b').locator('[data-edit-kind="dot-html-text"]').count(),4);
  await edge('b','c').locator('text').filter({hasText:/^Shared$/}).waitFor();
  assert.equal(await paper.locator('[data-dot-kind="edge"]').count(),2);
  await undo(plainEdge,'One undo restores the original plain shared edge label and chain');
  console.log('Verified shared DOT edge delete/retarget, record add/group/port integrity, HTML row/column/merge/split, edge table creation, exact source retention and atomic undo through the native inspector.');
}
