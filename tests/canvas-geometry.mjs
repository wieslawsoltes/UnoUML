import test from 'node:test';
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
