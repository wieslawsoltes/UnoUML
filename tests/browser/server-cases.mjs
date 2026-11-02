import assert from 'node:assert/strict';

export async function verifyServerRendering(page) {
  const endpoint = 'https://unouml-renderer.test/svg';
  const source = '@startchen\nentity Book {\n}\n@endchen';
  let body, count = 0;
  let response = { contentType: 'image/svg+xml', body: '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 25" width="100" height="25"><text>Book</text></svg>' };
  await page.route(endpoint, route => {
    count++;
    body = route.request().postData();
    assert.equal(route.request().method(), 'POST');
    return route.fulfill({ ...response, headers: { 'access-control-allow-origin': '*' } });
  });
  await page.evaluate(({endpoint,source}) => {
    ui.setServerEndpoint(endpoint);
    return ui.render('diagram', source);
  }, {endpoint,source});
  assert.equal(body, source);
  assert.equal(await page.locator('.paper text').textContent(), 'Book');
  response = { contentType: 'image/png', body: Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+X2ioAAAAASUVORK5CYII=', 'base64') };
  await page.evaluate(() => ui.render('diagram', '@startditaa\n+--+\n@endditaa'));
  assert.match(await page.locator('.paper image').getAttribute('href'), /^data:image\/png;base64,/);
  response = { status: 503, body: 'Unavailable' };
  await assert.rejects(() => page.evaluate(() => ui.render('diagram', '@startuml\nclass Problem\n@enduml')), /503/);
  await assert.rejects(() => page.evaluate(() => ui.exportDiagram('diagram', 'svg', 'stale.svg')), /Render a diagram/);
  await page.evaluate(() => { ui.setServerEndpoint(''); return ui.render('diagram', '@startuml\nAlice -> Bob\n@enduml'); });
  assert.equal(count, 3);
}
