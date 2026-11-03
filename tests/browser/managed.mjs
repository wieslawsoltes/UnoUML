import assert from 'node:assert/strict';

// Use the published Uno runtime and the same viewport for both engines. Time through SVG
// sanitization, hit-index construction and DOM installation. No artificial JS-to-C# shim.
export async function compareRenderers(page) {
  const reports = await page.evaluate(async () => {
    const ui = await import(new URL('uml/viewport.js', document.baseURI));
    const host = [...document.querySelectorAll('[id]')].find(el => el.shadowRoot?.querySelector('.paper'));
    const paper = host.shadowRoot.querySelector('.paper');
    const graph = iteration => '@startuml\n' +
      Array.from({length:20}, (_, i) => 'class Service' + i).join('\n') + '\n' +
      Array.from({length:19}, (_, i) => 'Service' + i + ' --> Service' + (i+1) + (i === 4 ? ' : transfer' + iteration : '')).join('\n') + '\n@enduml';
    const sequence = iteration => '@startuml\n' +
      Array.from({length:6}, (_, i) => 'participant P' + i).join('\n') + '\n' +
      Array.from({length:25}, (_, i) => 'P' + (i%6) + ' -> P' + ((i+1)%6) + ' : ' + (i === 8 ? 'transfer' + iteration : 'Message ' + i)).join('\n') + '\n@enduml';
    const c4 = iteration => '@startuml\n!include <C4/C4_Context>\nPerson(customer,"Customer","Places an order")\nSystem(store,"Storefront","Accepts orders")\nSystem_Ext(bank,"Bank","Handles transfers")\nRel(customer,store,"Checkout")\nRel(store,bank,"transfer' + iteration + '","HTTPS")\n@enduml';
    const reports = [];
    for (const [family, sample] of [['20 classes / 19 edges',graph], ['6 lifelines / 25 messages',sequence], ['3 C4 systems and people',c4]]) {
      const measurements = { managed: [], javascript: [] };
      for (let i = -2; i < 9; i++) {
        const input = sample(i);
        for (const native of i % 2 ? [true, false] : [false, true]) {
          ui.setLocalMode(host.id, native);
          const t = performance.now();
          await ui.render(host.id, input);
          const elapsed = performance.now() - t;
          const backend = native ? 'managed' : 'javascript';
          if (paper.dataset.renderer !== backend || !paper.textContent.includes('transfer' + i))
            throw new Error('Unexpected output from ' + backend + ' for ' + family);
          if (i >= 0) measurements[backend].push(elapsed);
        }
      }
      const median = samples => samples.sort((a,b)=>a-b)[samples.length>>1];
      reports.push({family, managed:median(measurements.managed), javascript:median(measurements.javascript)});
    }
    ui.setLocalMode(host.id, true);
    await ui.render(host.id, '@startuml\n!include <C4/C4_Context>\n!$personName = "Reviewer"\nPerson(user,$personName)\n@enduml');
    if (paper.dataset.renderer !== 'managed' || !paper.textContent.includes('Reviewer')) throw Error('C# preprocessor and C4 dispatch failed');
    await ui.render(host.id, '@startuml\n!pragma teoz true\nAlice -> Bob : Compatibility\n@enduml');
    if (paper.dataset.renderer !== 'javascript' || !paper.textContent.includes('Compatibility')) throw Error('Official fallback failed');
    for (const [tag, body] of [
      ['files','/src/Editor.cs\n/tests/EditorTests.cs'],
      ['mindmap','* Project\n** Library\n** Editor'],
      ['wbs','* Release\n** Compile\n** Publish'],
      ['json','{"name":"Studio","active":true}']
    ]) {
      await ui.render(host.id,'@start'+tag+'\n'+body+'\n@end'+tag);
      if (paper.dataset.renderer !== 'managed') throw Error('Expected native renderer for '+tag);
    }
    return reports;
  });
  for (const report of reports) {
    console.log(`Uncached browser render median (${report.family}, 9 variants): C# ${report.managed.toFixed(1)} ms, JS ${report.javascript.toFixed(1)} ms.`);
    assert.ok(report.managed > 0 && report.javascript > 0);
  }
}
