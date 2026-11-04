import test from 'node:test';
import {CanvasIds} from '../src/UnoUML.App/wwwroot/uml/canvas-scene.js';
import assert from 'node:assert/strict';
import {bounds,center,descendants,endpointLabelTranslation,endpointTangent,moveWaypoint,pointOnRoute,projectToRoute,resize,rotate,routePath,snapMove,visibleOwner} from '../src/UnoUML.App/wwwroot/uml/canvas-geometry.js';

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
test('endpoint tangents skip coincident points and follow the actual two-point cubic',()=>{
  const route=[{x:0,y:0},{x:0,y:0},{x:0,y:50},{x:40,y:50},{x:40,y:50}];
  assert.deepEqual(endpointTangent(route,0),{x:0,y:1});
  assert.deepEqual(endpointTangent(route,1),{x:-1,y:0});
  assert.deepEqual(endpointTangent([{x:0,y:0},{x:40,y:50}],0,'curved'),{x:1,y:0});
  assert.deepEqual(endpointTangent([{x:0,y:0},{x:40,y:50}],1,'curved'),{x:-1,y:0});
  assert.equal(endpointTangent([{x:0,y:0},{x:0,y:0}],0),null);
});
test('wide source and target captions keep upright extent clearance after a quarter turn',()=>{
  const before=[{x:0,y:0},{x:0,y:100}],after=[{x:100,y:50},{x:200,y:50}];
  assert.deepEqual(endpointLabelTranslation({x:8,y:12,width:60,height:14},before,after,0,
    {x:80,y:0,width:20,height:100}),{x:104,y:16});
  assert.deepEqual(endpointLabelTranslation({x:8,y:70,width:60,height:14},before,after,1,
    {x:200,y:0,width:80,height:100}),{x:116,y:-42});
});
test('endpoint reversal puts the caption outside the new terminal without rotating its bounds',()=>{
  const before=[{x:0,y:0},{x:0,y:100}],after=[{x:100,y:100},{x:100,y:0}];
  const label={x:8,y:12,width:10,height:12},delta=endpointLabelTranslation(label,before,after,0,
    {x:60,y:100,width:80,height:50});
  assert.deepEqual(delta,{x:74,y:64});
  assert.ok(label.y+delta.y+label.height<100);
  assert.deepEqual(label,{x:8,y:12,width:10,height:12});
});
test('a rotated terminal has a separating plane from its endpoint caption',()=>{
  const label={x:8,y:12,width:60,height:14},before=[{x:0,y:0},{x:0,y:100}],after=[{x:100,y:100},{x:150,y:150}];
  const delta=endpointLabelTranslation(label,before,after,0,{x:20,y:20,width:160,height:160,rotation:45});
  const c=center(label),u=Math.SQRT1_2,nearest=(c.x+delta.x-100)*u+(c.y+delta.y-100)*u-(label.width+label.height)*u/2;
  assert.ok(nearest>=82-1e-7,'The label clears the rotated terminal support plane by two units');
});
test('unchanged endpoint direction preserves exact caption placement and repeated projection is stable',()=>{
  const label={x:8,y:12,width:60,height:14},before=[{x:0,y:0},{x:0,y:100}],after=[{x:20,y:30},{x:20,y:130}];
  assert.deepEqual(endpointLabelTranslation(label,before,before,0),{x:0,y:0});
  assert.deepEqual(endpointLabelTranslation(label,before,after,0),{x:20,y:30});
  const turn=[{x:100,y:100},{x:150,y:150}];
  const first=endpointLabelTranslation(label,before,turn,0,null,'curved');
  assert.deepEqual(endpointLabelTranslation(label,before,turn,0,null,'curved'),first);
  assert.deepEqual(first,{x:104,y:66},'A diagonal cubic uses its horizontal terminal tangent');
});
test('an endpoint caption slides past adjacent nodes while retaining its terminal clearance',()=>{
  const label={x:8,y:70,width:60,height:14},before=[{x:0,y:0},{x:0,y:100}],after=[{x:100,y:50},{x:200,y:50}];
  const terminal={id:'target',x:200,y:0,width:80,height:100};
  const obstacle={id:'neighbor',x:110,y:10,width:70,height:40};
  const delta=endpointLabelTranslation(label,before,after,1,terminal,'orthogonal',[terminal,obstacle]);
  near(delta.x,116);assert.ok(label.y+delta.y>=obstacle.y+obstacle.height+2);
  assert.ok(label.x+delta.x+label.width<terminal.x-2);
});
test('overlapping forbidden label intervals are combined before choosing the nearest clearance',()=>{
  const label={x:8,y:70,width:60,height:14},before=[{x:0,y:0},{x:0,y:100}],after=[{x:100,y:50},{x:200,y:50}];
  const terminal={id:'target',x:200,y:0,width:80,height:100};
  const obstacles=[{id:'a',x:110,y:10,width:70,height:40},{id:'b',x:110,y:45,width:70,height:30}];
  const delta=endpointLabelTranslation(label,before,after,1,terminal,'orthogonal',obstacles);
  assert.ok(label.y+delta.y+label.height<=8,'The merged downward obstruction makes the upper clear interval nearer');
  near(delta.x,116);
});
test('orthogonal corner movement preserves terminals and perpendicular neighboring segments',()=>{
  const path=[{x:0,y:0},{x:0,y:40},{x:60,y:40},{x:60,y:100},{x:120,y:100}],before=structuredClone(path);
  const moved=moveWaypoint(path,2,{x:80,y:55},true);
  assert.deepEqual(path,before);assert.equal(moved.points.length,path.length);
  assert.deepEqual(moved.points[0],path[0]);assert.deepEqual(moved.points.at(-1),path.at(-1));
  assert.deepEqual(moved.points[moved.index],{x:80,y:55});
  assert.ok(moved.points.every((p,i)=>i===0||p.x===moved.points[i-1].x||p.y===moved.points[i-1].y));
});
test('moving an inserted bend retains orthogonality without shifting attached endpoints',()=>{
  const path=[{x:0,y:0},{x:40,y:0},{x:100,y:0}];
  const moved=moveWaypoint(path,1,{x:45,y:35},true);
  assert.deepEqual(moved.points[0],path[0]);assert.deepEqual(moved.points.at(-1),path.at(-1));
  assert.deepEqual(moved.points[moved.index],{x:45,y:35});
  assert.ok(moved.points.every((p,i)=>i===0||p.x===moved.points[i-1].x||p.y===moved.points[i-1].y));
  const freely=moveWaypoint(path,1,{x:45,y:35},false);
  assert.equal(freely.points.length,3);assert.deepEqual(freely.points[1],{x:45,y:35});
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
