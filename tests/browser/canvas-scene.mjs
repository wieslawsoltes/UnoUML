import assert from 'node:assert/strict';

export async function verifyCanvasSceneIdentity(page,render) {
  const fixtures=[
    ['parallel and self links','@startuml\nclass A\nclass B\nA --> B : first\nA --> B : second\nB --> A : reverse\nA --> A : self\n@enduml',
      [['A','B'],['A','B'],['B','A'],['A','A']]],
    ['qualified namespace identities','@startuml\nnamespace One {\n class Item\n}\nnamespace Two {\n class Item\n}\nOne.Item --> Two.Item : cross\nTwo.Item --> One.Item : back\nOne.Item --> One.Item : self\n@enduml',
      [['One.Item','Two.Item'],['Two.Item','One.Item'],['One.Item','One.Item']]],
    ['user names resembling SVG IDs','@startuml\nclass A\nclass ent0001\nA --> ent0001 : outward\nent0001 --> A : inward\n@enduml',
      [['A','ent0001'],['ent0001','A']]]
  ];
  const pairs=edges=>edges.map(edge=>JSON.stringify([edge.source,edge.target])).sort();
  for(const [name,input,expected] of fixtures) {
    await render(input);
    assert.equal(await page.locator('.paper').getAttribute('data-renderer'),'javascript');
    const state=await page.evaluate(()=>JSON.parse(ui.getCanvasState('diagram')).state);
    assert.deepEqual(pairs(state.edges),expected.map(pair=>JSON.stringify(pair)).sort(),`Compatibility endpoints retain ${name}`);
    assert.equal(new Set(state.edges.map(edge=>edge.id)).size,state.edges.length,'Each parallel, reversed and self connector has its own canvas identity');
    assert.ok(state.edges.every(edge=>state.nodes.some(node=>node.id===edge.source)&&state.nodes.some(node=>node.id===edge.target)&&edge.waypoints.length>=2));
    assert.equal(await page.locator('g.link[data-canvas-edge]').evaluateAll(groups=>groups.filter(group=>
      [...group.querySelectorAll('path')].some(path=>path.getTotalLength()>0)).length),state.edges.length,
      'A standalone render retains visible routes before a native layout snapshot arrives');
    const moved=structuredClone(state);
    for(const node of moved.nodes){node.x+=43;node.y-=17;}
    for(const edge of moved.edges)for(const point of edge.waypoints){point.x+=43;point.y-=17;}
    await page.evaluate(state=>ui.setCanvasState('diagram',JSON.stringify(state),1),moved);
    const rendered=await page.evaluate(()=>{
      const svg=document.querySelector('#diagram').shadowRoot.querySelector('.paper svg');
      return [...svg.querySelectorAll('g.link[data-canvas-edge]')].map(group=>{
        const path=[...group.querySelectorAll('path')].filter(path=>!/[zZ]\s*$/.test(path.getAttribute('d')||''))
          .sort((a,b)=>b.getTotalLength()-a.getTotalLength())[0];
        const matrix=svg.getScreenCTM().inverse().multiply(path.getScreenCTM());
        const read=distance=>{const point=path.getPointAtLength(distance).matrixTransform(matrix);return {x:point.x,y:point.y};};
        return {id:group.dataset.canvasEdge,first:read(0),last:read(path.getTotalLength())};
      });
    });
    assert.equal(rendered.length,moved.edges.length,'Every compatibility connector remains independently projected');
    const distance=(a,b)=>Math.hypot(a.x-b.x,a.y-b.y);
    for(const route of rendered) {
      const edge=moved.edges.find(edge=>edge.id===route.id);assert.ok(edge);
      const first=edge.waypoints[0],last=edge.waypoints.at(-1);
      assert.ok(Math.min(distance(route.first,first)+distance(route.last,last),distance(route.first,last)+distance(route.last,first))<.1,
        `Projected connector ${route.id} follows its captured semantic endpoints`);
    }
    const after=await page.evaluate(()=>JSON.parse(ui.getCanvasState('diagram')).state);
    assert.deepEqual(after.edges.map(edge=>[edge.id,edge.source,edge.target]),state.edges.map(edge=>[edge.id,edge.source,edge.target]),
      'Applying saved geometry keeps the captured endpoint identities');
  }
  console.log('Verified compatibility SVG-to-semantic canvas identities, namespaces, synthetic-looking user names, distinct parallel/reversed/self edges and connector projection after movement.');
}
