import assert from 'node:assert/strict';

export async function verifyStructuredEditing(page, source) {
  await page.setViewportSize({width:1480,height:960});
  await page.getByText('Split',{exact:true}).last().click();
  await source.waitFor({state:'visible'});
  const paper = page.locator('.paper').first(), surface = page.locator('.surface').first();
  const packet = '@startpacketdiag\n{\n  colwidth = 16\n  0-7: Header  \' retained\n  8-15: Payload\n}\n@endpacketdiag';
  await source.fill(packet);
  await paper.locator('.packet-field[data-packet-first="0"][data-packet-last="7"]').waitFor();
  assert.equal(await paper.getAttribute('data-renderer'),'managed');
  await paper.locator('.packet-field[data-packet-field-index="0"] rect').click();
  await page.getByText('Resize packet field…',{exact:true}).last().click();
  let dialog = page.getByRole('dialog',{name:'Resize packet field',exact:true});
  await dialog.getByRole('textbox',{name:'Bit count',exact:true}).fill('12');
  await dialog.getByRole('button',{name:'Apply',exact:true}).click();
  await dialog.waitFor({state:'hidden'});
  await paper.locator('.packet-field[data-packet-first="12"][data-packet-last="19"]').waitFor();
  assert.equal(await source.inputValue(),packet.replace('0-7: Header','0-11: Header').replace('8-15: Payload','12-19: Payload'));
  await surface.press('Control+z');
  await paper.locator('.packet-field[data-packet-first="8"][data-packet-last="15"]').waitFor();
  assert.equal(await source.inputValue(),packet,'One undo restores both resized and shifted ranges');

  await paper.locator('.packet-field[data-packet-field-index="0"] rect').click();
  await page.getByText('Split packet field…',{exact:true}).last().click();
  dialog = page.getByRole('dialog',{name:'Split packet field',exact:true});
  await dialog.getByRole('textbox',{name:'First part bit count',exact:true}).fill('3');
  await dialog.getByRole('textbox',{name:'Second part label',exact:true}).fill('Flags');
  // Keyboard acceptance follows the same modal path as clicking Split.
  await dialog.getByRole('button',{name:'Split',exact:true}).focus();
  await dialog.getByRole('button',{name:'Split',exact:true}).press('Enter');
  await dialog.waitFor({state:'hidden'});
  await paper.locator('.packet-field[data-packet-first="3"][data-packet-last="7"]').waitFor();
  assert.equal(await source.inputValue(),packet.replace("  0-7: Header  ' retained", "  0-2: Header  ' retained\n  3-7: Flags"),'Split retains the original inline comment exactly once');
  await surface.press('Control+z');
  await paper.locator('.packet-field[data-packet-first="0"][data-packet-last="7"]').waitFor();
  assert.equal(await source.inputValue(),packet);

  await paper.locator('.packet-field[data-packet-field-index="0"] rect').click();
  await page.getByText('Insert packet field…',{exact:true}).last().click();
  dialog = page.getByRole('dialog',{name:'Insert packet field',exact:true});
  await dialog.getByRole('textbox',{name:'Field label',exact:true}).fill('Type');
  await dialog.getByRole('textbox',{name:'Bit count',exact:true}).fill('4');
  await dialog.getByRole('button',{name:'Insert',exact:true}).click();
  await dialog.waitFor({state:'hidden'});
  await paper.locator('.packet-field[data-packet-first="8"][data-packet-last="11"]').waitFor();
  assert.equal(await source.inputValue(),packet.replace('  8-15: Payload','  8-11: Type\n  12-19: Payload'));
  await surface.press('Control+z');
  await paper.locator('.packet-field[data-packet-first="8"][data-packet-last="15"]').waitFor();
  assert.equal(await source.inputValue(),packet);

  const chart = '@startchart\nh-axis [A, B]\nv-axis 0 --> 20\nbar "One" [2, 4] #112233\nline "Two" [5, 6]\nlegend right\n@endchart';
  await source.fill(chart);
  await paper.locator('[data-edit-kind="chart-category"]').filter({hasText:/^A$/}).waitFor();
  await paper.locator('[data-edit-kind="chart-category"]').filter({hasText:/^A$/}).click();
  await page.getByText('Insert chart category…',{exact:true}).last().click();
  dialog = page.getByRole('dialog',{name:'Insert chart category',exact:true});
  await dialog.getByRole('textbox',{name:'Category label',exact:true}).fill('C');
  await dialog.getByRole('textbox',{name:'Value for One',exact:true}).fill('3');
  await dialog.getByRole('textbox',{name:'Value for Two',exact:true}).fill('7');
  await dialog.getByRole('button',{name:'Insert',exact:true}).click();
  await dialog.waitFor({state:'hidden'});
  await paper.locator('[data-edit-kind="chart-category"]').filter({hasText:/^C$/}).waitFor();
  assert.equal(await source.inputValue(),chart.replace('[A, B]','[A, B, C]').replace('[2, 4]','[2, 4, 3]').replace('[5, 6]','[5, 6, 7]'));
  assert.equal(await paper.locator('g.series.bar rect').count(),3,'All chart series receive the new category');
  await surface.press('Control+z');
  await paper.locator('[data-edit-kind="chart-category"]').filter({hasText:/^C$/}).waitFor({state:'detached'});
  assert.equal(await source.inputValue(),chart,'One undo restores categories and every series');

  const coordinates = '@startchart\nh-axis -10 --> 10\nv-axis 0 --> 100\nscatter "Observed" [(-5, 20), (5, 80)] #blue\nlegend right\n@endchart';
  await source.fill(coordinates);
  const firstPoint = paper.locator('g[data-point-x="-5"][data-point-y="20"] circle');
  await firstPoint.waitFor();
  await firstPoint.click();
  await page.getByText('Edit chart data point…',{exact:true}).last().click();
  dialog = page.getByRole('dialog',{name:'Edit chart data point',exact:true});
  await dialog.getByRole('textbox',{name:'X value',exact:true}).fill('-2');
  await dialog.getByRole('textbox',{name:'Y value',exact:true}).fill('30');
  await dialog.getByRole('button',{name:'Apply',exact:true}).click();
  await dialog.waitFor({state:'hidden'});
  await paper.locator('g[data-point-x="-2"][data-point-y="30"] circle').waitFor();
  assert.equal(await source.inputValue(),coordinates.replace('(-5, 20)','(-2, 30)'));
  await surface.press('Control+z');
  await firstPoint.waitFor();
  assert.equal(await source.inputValue(),coordinates,'Both coordinate values undo together');
  assert.equal(await paper.getAttribute('data-renderer'),'managed');
  console.log('Verified native packet insert/resize/split and categorical/coordinate chart authoring, exact source retention, keyboard dialogs and atomic undo.');
}
