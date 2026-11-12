import test from 'node:test';
import assert from 'node:assert/strict';
import {flattenPath,fillTriangles,strokeTriangles,dashContours,parseTransform,multiply,inverse,transformPoint,maxScale,boundsOf,ellipseContours,rectContours,GeometryError} from '../src/UnoUML.App/wwwroot/uml/gpu/geometry.js';
const near=(a,b,e=1e-7)=>assert.ok(Math.abs(a-b)<=e,a+' differs from '+b);
const contains=(mesh,p)=>{
 for(let i=0;i<mesh.length;i+=6){
  const a=[mesh[i],mesh[i+1]],b=[mesh[i+2],mesh[i+3]],c=[mesh[i+4],mesh[i+5]];
  const edge=(v,w)=>(w[0]-v[0])*(p[1]-v[1])-(w[1]-v[1])*(p[0]-v[0]);
  const s=[edge(a,b),edge(b,c),edge(c,a)];if(s.every(n=>n>=-1e-8)||s.every(n=>n<=1e-8))return true;
 }return false;
};
const winding=(mesh,p)=>{
 let result=0;
 for(let i=0;i<mesh.length;i+=6){
  const triangle=mesh.slice(i,i+6);
  if(contains(triangle,p)){const area=(triangle[2]-triangle[0])*(triangle[5]-triangle[1])-(triangle[3]-triangle[1])*(triangle[4]-triangle[0]);if(Math.abs(area)>1e-9)result+=Math.sign(area);}
 }return result;
};
test('all path coordinate modes preserve command endpoints and closure',()=>{
 const c=flattenPath('M1 2 l9 0 v8 h-9 z M30 40 50 60');
 assert.equal(c.length,2);assert.equal(c[0].closed,true);
 assert.deepEqual(c[0].points,[[1,2],[10,2],[10,10],[1,10]]);
 assert.deepEqual(c[1].points,[[30,40],[50,60]]);
});
test('smooth cubic and quadratic commands reflect only the matching control type',()=>{
 const c=flattenPath('M0 0C0 10 10 10 10 0S20 -10 20 0Q25 10 30 0T40 0',.05)[0];
 assert.deepEqual(c.points.at(-1),[40,0]);assert.ok(c.points.some(p=>p[1]>7));assert.ok(c.points.some(p=>p[1]<-7));
 const independent=flattenPath('M0 0L10 0S20 10 30 0',.05)[0];
 assert.deepEqual(independent.points.at(-1),[30,0]);
});
test('collinear backtracking cubic curves retain their extrema',()=>{
 const c=flattenPath('M0 0C100 0 -100 0 10 0',.01)[0];
 assert.ok(c.points.some(p=>p[0]>25));assert.ok(c.points.some(p=>p[0]<-20));
});
test('elliptical arc normalization, rotation and sweep keep finite endpoints',()=>{
 for(const flags of ['0 0','0 1','1 0','1 1']){
  const p=flattenPath('M1 2A4 2 30 '+flags+' 20 30',.02)[0].points;
  assert.deepEqual(p.at(-1),[20,30]);assert.ok(p.length>8);assert.ok(p.flat().every(Number.isFinite));
 }
 assert.deepEqual(flattenPath('M1 2A0 2 0 0 1 20 30')[0].points,[[1,2],[20,30]]);
 assert.equal(flattenPath('M1 2A4 2 0 0 1 1 2')[0].points.length,1);
});
test('tiny circles and rounded rectangles retain nondegenerate geometry',()=>{
 assert.ok(ellipseContours(0,0,.01,.01,1)[0].points.length>=4);
 const c=rectContours(2,3,20,10,500,500,.02);const b=boundsOf(c);
 near(b[0],2,.03);near(b[1],3,.03);near(b[2],20,.06);near(b[3],10,.06);
});
test('winding triangle fans preserve a reversed inner hole',()=>{
 const c=flattenPath('M0 0H20V20H0Z M5 5V15H15V5Z');
 const mesh=fillTriangles(c);assert.notEqual(winding(mesh,[2,3]),0);assert.equal(winding(mesh,[8,9]),0);
});
test('self-intersecting even-odd fills retain signed triangles without ear clipping',()=>{
 const mesh=fillTriangles(flattenPath('M0 0L20 20L0 20L20 0Z'),'evenodd');
 assert.equal(mesh.length,12);assert.notEqual(Math.abs(winding(mesh,[10,3]))%2,0);assert.equal(Math.abs(winding(mesh,[2,10]))%2,0);
});
test('nonzero winding budget prevents stencil8 wraparound',()=>{
 const points=Array.from({length:386},(_,i)=>[Math.cos(i*.1),Math.sin(i*.1)]);
 assert.throws(()=>fillTriangles([{points,closed:true}]),/winding-budget/);
 assert.ok(fillTriangles([{points,closed:true}],'evenodd').length>0);
});
test('stroke cap geometry distinguishes butt, square and round',()=>{
 const c=flattenPath('M0 0H10'),butt=strokeTriangles(c,4),square=strokeTriangles(c,4,{cap:'square'}),round=strokeTriangles(c,4,{cap:'round',tolerance:.01});
 assert.equal(contains(butt,[-1,0]),false);assert.equal(contains(square,[-1.9,1.9]),true);
 assert.equal(contains(round,[-1,0]),true);assert.equal(contains(round,[-1.9,1.9]),false);
});
test('stroke joins produce bounded miters and fallback bevels',()=>{
 const c=flattenPath('M0 0H10V10');
 const miter=strokeTriangles(c,4,{join:'miter',miterLimit:4}),bevel=strokeTriangles(c,4,{join:'bevel'}),clipped=strokeTriangles(c,4,{join:'miter',miterLimit:1});
 assert.equal(contains(miter,[11.8,-1.8]),true);assert.equal(contains(bevel,[11.8,-1.8]),false);
 assert.deepEqual(clipped,bevel);
});
test('closed paths join the closing seam and omit end caps',()=>{
 const c=flattenPath('M0 0H10V10H0Z');
 assert.deepEqual(strokeTriangles(c,2,{cap:'square'}),strokeTriangles(c,2,{cap:'butt'}));
});
test('stroke union meshes preserve a zero-length round or square subpath',()=>{
 const c=flattenPath('M3 4');
 assert.equal(strokeTriangles(c,2).length,0);
 assert.ok(contains(strokeTriangles(c,2,{cap:'round'}),[3,4]));
 assert.ok(contains(strokeTriangles(c,2,{cap:'square'}),[3.9,4.9]));
});
test('dash phase, odd arrays and vertex-crossing segments are retained',()=>{
 const c=flattenPath('M0 0H8V12');
 const d=dashContours(c,[5,5],2);
 assert.deepEqual(d[0].points,[[0,0],[3,0]]);
 assert.ok(d.some(v=>v.points.some(p=>p[0]===8&&p[1]===0)));
 const odd=dashContours(flattenPath('M0 0H30'),[3,2,1]);
 assert.ok(odd.length>=5);
});
test('negative dash offsets and zero-gap patterns terminate deterministically',()=>{
 const c=flattenPath('M0 0H20');
 assert.deepEqual(dashContours(c,[5,0]),c);
 assert.equal(dashContours(c,[5,5],-5).length,2);
 assert.throws(()=>strokeTriangles(c,2,{cap:'round',dash:[0,5]}),/zero-length-dash/);
});
test('affine composition and inversion round-trip sheared reflected coordinates',()=>{
 const m=parseTransform('translate(10,20) rotate(37,2,3) skewX(12) scale(-2,3)'),back=inverse(m);
 for(const p of [[0,0],[100,-30],[-7.25,800]]){const q=transformPoint(back,transformPoint(m,p));near(q[0],p[0]);near(q[1],p[1]);}
 near(maxScale([2,0,0,3,0,0]),3);
 assert.deepEqual(multiply([1,0,0,1,10,20],[2,0,0,2,0,0]),[2,0,0,2,10,20]);
});
test('malformed or excessive geometry is rejected for faithful raster fallback',()=>{
 for(const d of ['L1 2','M0','M0 0LNaN 2','M0 0A1 1 0 2 0 3 4','M0 0R2 3'])assert.throws(()=>flattenPath(d),GeometryError);
 assert.throws(()=>inverse([0,0,0,0,0,0]),/singular-transform/);
 assert.throws(()=>parseTransform('translate(1) garbage'),/invalid-transform/);
 assert.throws(()=>flattenPath('M0 0'+('L1 1'.repeat(32769))),/geometry-budget/);
});
