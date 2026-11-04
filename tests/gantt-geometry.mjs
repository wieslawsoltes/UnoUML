import test from 'node:test';
import assert from 'node:assert/strict';
import {ganttRange} from '../src/UnoUML.App/wwwroot/uml/gantt-geometry.js';

test('timeline movement preserves inclusive duration',()=>{
  assert.deepEqual(ganttRange(4,5,3),{start:7,duration:5});
  assert.deepEqual(ganttRange(4,5,-20),{start:0,duration:5});
  assert.deepEqual(ganttRange(4,5,2000),{start:1091,duration:5});
});
test('start resize keeps the finish date and cannot invert',()=>{
  assert.deepEqual(ganttRange(4,5,2,'start'),{start:6,duration:3});
  assert.deepEqual(ganttRange(4,5,-10,'start'),{start:0,duration:9});
  assert.deepEqual(ganttRange(4,5,100,'start'),{start:8,duration:1});
});
test('end resize keeps the start and enforces schedule horizon',()=>{
  assert.deepEqual(ganttRange(4,5,2,'end'),{start:4,duration:7});
  assert.deepEqual(ganttRange(4,5,-100,'end'),{start:4,duration:1});
  assert.deepEqual(ganttRange(4,5,2000,'end'),{start:4,duration:1092});
});
test('milestones stay a single calendar day',()=>{
  assert.deepEqual(ganttRange(4,1,2,'move',true),{start:6,duration:1});
  assert.deepEqual(ganttRange(4,1,-10,'start',true),{start:0,duration:1});
});
test('invalid interval arithmetic is rejected before preview',()=>{
  for(const values of [[-1,2,1],[0,0,1],[1095,2,0],[0,1,NaN],[0,1,.5],[Infinity,2,1]])assert.throws(()=>ganttRange(...values),RangeError);
});
