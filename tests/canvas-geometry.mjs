import test from 'node:test';
import {CanvasIds} from '../src/UnoUML.App/wwwroot/uml/canvas-scene.js';
import assert from 'node:assert/strict';
import {bounds,center,descendants,pointOnRoute,projectToRoute,resize,rotate,routePath,snapMove,visibleOwner} from '../src/UnoUML.App/wwwroot/uml/canvas-geometry.js';

const near=(a,b)=>assert.ok(Math.abs(a-b)<.002,`${a} != ${b}`);
test('smart guides choose the closest edge or center before grid snapping',()=>{
  const result=snapMove({x:10,y:20,width:40,height:40},43,20,[{x:100,y:40,width:40,height:40}],{snapToObjects:true,snapToGrid:true,gridSize:16},8);
  assert.equal(result.dx,50);assert.equal(result.dy,20);assert.deepEqual(result.guides,[{axis:'x',value:100},{axis:'y',value:40}]);
});
test('Alt bypasses guides and grid; Shift constrains the dominant axis',()=>{
  assert.deepEqual(snapMove({x:3,y:7,width:20,height:20},17.5,4,[],{gridSize:16},6,true,true),{dx:17.5,dy:0,guides:[]});
  assert.deepEqual(snapMove({x:3,y:7,width:20,height:20},17.5,4,[],{gridSize:16,snapToObjects:false},6),{dx:13,dy:9,guides:[]});
});
test('rotated bounds include every corner and preserve the center',()=>{
  const b=bounds([{x:10,y:20,width:80,height:40,rotation:90}]);
  near(b.x,30);near(b.y,0);near(b.width,40);near(b.height,80);
});
test('rotated resize keeps its opposite handle fixed in world coordinates',()=>{
  const original={x:80,y:20,width:120,height:60,rotation:37};
  const fixed=rotate({x:80,y:20},center(original),37),nextPoint=rotate({x:240,y:110},center(original),37);
  const changed=resize(original,'se',nextPoint,{free:true});
  const nextFixed=rotate({x:changed.x,y:changed.y},center(changed),37);
  near(fixed.x,nextFixed.x);near(fixed.y,nextFixed.y);near(changed.width,160);near(changed.height,90);
});
test('centered aspect resize preserves center and ratio',()=>{
  const original={x:10,y:20,width:100,height:50};
  const changed=resize(original,'se',{x:150,y:100},{centered:true,aspect:true,free:true});
  near(center(changed).x,60);near(center(changed).y,45);near(changed.width/changed.height,2);
});
test('hierarchies close transitively and outer collapse owns hidden descendants',()=>{
  const nodes=[{id:'root',collapsed:true},{id:'a',parent:'root',collapsed:true},{id:'b',parent:'a'},{id:'c'}];
  assert.deepEqual([...descendants(nodes,['root'])],['root','a','b']);
  assert.equal(visibleOwner(nodes[2],new Map(nodes.map(n=>[n.id,n]))).id,'root');
});
test('route placement measures arc length and projects to the closest segment',()=>{
  const path=[{x:0,y:0},{x:40,y:0},{x:40,y:60}];
  assert.deepEqual(pointOnRoute(path),{x:40,y:10});
  assert.deepEqual(projectToRoute({x:47,y:32},path),{index:1,point:{x:40,y:32},distance:7});
  assert.equal(routePath(path,'orthogonal'),'M0 0L40 0L40 60');
  assert.equal(routePath(path,'curved'),'M0 0L30 0Q40 0 40 10L40 60');
});
test('degenerate segments remain finite and geometry does not mutate source snapshots',()=>{
  const node={x:1,y:2,width:40,height:50};resize(node,'nw',{x:100,y:100},{free:true});assert.deepEqual(node,{x:1,y:2,width:40,height:50});
  assert.deepEqual(pointOnRoute([{x:4,y:5},{x:4,y:5}]),{x:4,y:5});
  assert.equal(routePath([],'orthogonal'),'');
});

test('compatibility SVG element IDs resolve to semantic canvas nodes',()=>{
  const ids=new CanvasIds();ids.addNode('A','ent0001',['A']);ids.addNode('B','ent0002',['B']);
  assert.equal(ids.resolve('ent0001',true),'A');assert.equal(ids.resolve('ent0002',true),'B');
  assert.equal(ids.resolve('A'),'A');assert.equal(ids.resolve('B'),'B');
  assert.equal(ids.resolve('missing',true),null);
});
test('renderer IDs cannot shadow same-named user nodes across identifier spaces',()=>{
  const ids=new CanvasIds();ids.addNode('A','ent0001',['A']);ids.addNode('ent0001','ent0002',['ent0001']);
  assert.equal(ids.resolve('ent0001',true),'A','Compatibility endpoints identify the SVG element');
  assert.equal(ids.resolve('ent0001'),'ent0001','Managed endpoints identify the semantic node');
  assert.equal(ids.resolve('ent0002',true),'ent0001');
});
test('namespaced, C4, DOT and escaped identifiers resolve exactly without suffix guessing',()=>{
  const ids=new CanvasIds();
  ids.addNode('One.Item','ent0001',['One.Item','Item']);ids.addNode('Two.Item','ent0002',['Two.Item','Item']);
  ids.addNode('service','ent0003',['boundary.service']);ids.addNode('node:α::A -> B','node1',['α::A -> B']);
  assert.equal(ids.resolve('One.Item'),'One.Item');assert.equal(ids.resolve('Two.Item'),'Two.Item');
  assert.equal(ids.resolve('Item'),null,'An ambiguous short alias is not rebound to the last node');
  assert.equal(ids.resolve('boundary.service'),'service');assert.equal(ids.resolve('node1',true),'node:α::A -> B');
  assert.equal(ids.resolve('node:α::A -> B'),'node:α::A -> B');assert.equal(ids.resolve('α::A -> B'),'node:α::A -> B');
});
test('duplicate SVG IDs remain unresolved instead of binding a connector to the wrong node',()=>{
  const ids=new CanvasIds();ids.addNode('A','duplicate',['shared']);ids.addNode('B','duplicate',['shared']);
  ids.addNode('duplicate','other',['duplicate']);
  assert.equal(ids.resolve('duplicate',true),null);assert.equal(ids.resolve('shared'),null);
  assert.equal(ids.resolve('duplicate'),'duplicate');
});
test('parallel, reversed and self links receive distinct deterministic IDs',()=>{
  const capture=()=>{const ids=new CanvasIds();return [['A','B'],['A','B'],['B','A'],['A','A'],['A','A']].map(pair=>ids.edgeId(...pair));};
  assert.deepEqual(capture(),['A->B:0','A->B:1','B->A:0','A->A:0','A->A:1']);
  assert.deepEqual(capture(),capture());
});
test('explicit and fallback edge IDs cannot overwrite each other or alias delimiter-containing pairs',()=>{
  const ids=new CanvasIds(['A->B:0','edge:0']);
  const edges=[ids.edgeId('A','B'),ids.edgeId('C','D','A->B:0'),ids.edgeId('C','D','A->B:0'),
    ids.edgeId('A','B','edge:0'),ids.edgeId('A','B','edge:0'),ids.edgeId('A->B','C'),ids.edgeId('A','B->C')];
  assert.equal(edges[0],'A->B:1');assert.equal(edges[1],'A->B:0');assert.equal(edges[3],'edge:0');
  assert.equal(new Set(edges).size,edges.length);
});
