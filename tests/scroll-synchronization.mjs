import test from 'node:test';
import assert from 'node:assert/strict';
import {configureScrollSynchronization, removeScrollSynchronization} from '../src/UnoUML.App/wwwroot/uml/scroll-synchronization.js';

function fixture() {
  const elements = new Map(), listeners = new Map(), registrations = new Set();
  const body = {parent:null};
  const window = {
    addEventListener(type, handler, capture) {
      const entries = listeners.get(type) || [];
      if (!entries.some(entry => entry.handler === handler && entry.capture === capture)) entries.push({handler,capture});
      listeners.set(type,entries);
    },
    removeEventListener(type, handler, capture) {
      listeners.set(type,(listeners.get(type) || []).filter(entry => entry.handler !== handler || entry.capture !== capture));
    }
  };
  globalThis.window = window;
  globalThis.document = {getElementById:id => elements.get(id)};
  const node = (id,classes=[],parent=body) => {
    const element = {id,parent,classes:new Set(classes),children:[],isConnected:true,scrollLeft:0,scrollTop:0,
      closest(selector) {
        for (let current=this;current;current=current.parent) if (current.classes?.has(selector.slice(1))) return current;
        return null;
      },
      querySelectorAll(selector) {
        const result=[];
        const visit=parent => {for(const child of parent.children || []) {if(child.classes?.has(selector.slice(1))) result.push(child);visit(child);}};
        visit(this);return result;
      }};
    parent.children?.push(element);elements.set(id,element);return element;
  };
  const register = (id,callback) => { registrations.add(id);configureScrollSynchronization(id,callback); };
  const remove = id => { removeScrollSynchronization(id);registrations.delete(id); };
  const fire = (type,target,pointerId=1,onBody=()=>{}) => {
    const path=[];
    for(let current=target;current;current=current.parent) path.push(current);
    path.push(window);
    const event={type,target,pointerId,defaultPrevented:false,propagationStopped:false,composedPath:()=>path,
      preventDefault(){this.defaultPrevented=true;},stopPropagation(){this.propagationStopped=true;}};
    for(const entry of listeners.get(type) || []) if(entry.capture) entry.handler(event);
    onBody(event);
    for(const entry of listeners.get(type) || []) if(!entry.capture) entry.handler(event);
    return event;
  };
  return {body,node,register,remove,fire,listeners,elements,
    dispose() {for(const id of registrations) removeScrollSynchronization(id);delete globalThis.window;delete globalThis.document;}};
}

test('native offsets reach managed synchronization before the original pointer reaches body hit testing',()=>{
  const dom=fixture();
  try {
    const viewer=dom.node('inspector',['uno-scrollviewer']);
    const presenter=dom.node('presenter',['uno-scrollcontentpresenter'],viewer);
    const button=dom.node('insert-column',[],presenter);
    presenter.scrollLeft=12.5;presenter.scrollTop=0;
    let managed={horizontal:12.5,vertical:451};
    dom.register(viewer.id,(horizontal,vertical)=>{managed={horizontal,vertical};});
    const event=dom.fire('pointerdown',button,1,original=>{
      assert.deepEqual(managed,{horizontal:12.5,vertical:0});
      assert.equal(original.target,button);
    });
    assert.equal(event.defaultPrevented,false);
    assert.equal(event.propagationStopped,false);
    assert.deepEqual([...dom.listeners.keys()].sort(),['pointercancel','pointerdown','pointerup']);
    assert.ok([...dom.listeners.values()].every(entries=>entries.length===1&&entries[0].capture===true));
  } finally {dom.dispose();}
});

test('only the registered viewer synchronizes, using its own presenter rather than a nested TextBox',()=>{
  const dom=fixture();
  try {
    const viewer=dom.node('inspector',['uno-scrollviewer']);
    const nested=dom.node('textbox-scroll',['uno-scrollviewer'],viewer);
    const textPresenter=dom.node('textbox-presenter',['uno-scrollcontentpresenter'],nested);
    textPresenter.scrollTop=99;
    const calls=[];dom.register(viewer.id,(...offsets)=>calls.push(offsets));
    dom.fire('pointerdown',textPresenter);
    assert.deepEqual(calls,[],'A nested presenter cannot stand in for a missing owner template');
    const presenter=dom.node('inspector-presenter',['uno-scrollcontentpresenter'],viewer);
    presenter.scrollTop=42.25;
    dom.fire('pointerdown',textPresenter);
    assert.deepEqual(calls,[[0,42.25]]);
    dom.fire('pointerdown',dom.node('outside'));
    assert.equal(calls.length,1,'Other controls retain their ordinary dispatch');
  } finally {dom.dispose();}
});

test('release through Uno body capture uses current offsets and cancellation forgets that pointer',()=>{
  const dom=fixture();
  try {
    const viewer=dom.node('inspector',['uno-scrollviewer']);
    const presenter=dom.node('presenter',['uno-scrollcontentpresenter'],viewer);
    const button=dom.node('command',[],presenter);
    const calls=[];dom.register(viewer.id,(...offsets)=>calls.push(offsets));
    dom.fire('pointerdown',button,7);
    presenter.scrollTop=36;
    dom.fire('pointerup',dom.body,7);
    assert.deepEqual(calls,[[0,0],[0,36]],'A captured release is synchronized before managed delivery');
    dom.fire('pointerup',dom.body,7);
    assert.equal(calls.length,2,'A completed pointer is not retained');
    dom.fire('pointerdown',button,8);
    dom.fire('pointercancel',dom.body,8);
    dom.fire('pointerup',dom.body,8);
    assert.equal(calls.length,3,'A cancelled pointer is not retained');
  } finally {dom.dispose();}
});

test('template replacement and disconnected controls never reuse stale native offsets',()=>{
  const dom=fixture();
  try {
    const calls=[];dom.register('inspector',(...offsets)=>calls.push(offsets));
    dom.fire('pointerdown',dom.body);
    const viewer=dom.node('inspector',['uno-scrollviewer']);
    const old=dom.node('old-presenter',['uno-scrollcontentpresenter'],viewer);
    old.scrollTop=400;
    dom.fire('pointerdown',old);
    viewer.children=[];
    const current=dom.node('new-presenter',['uno-scrollcontentpresenter'],viewer);
    current.scrollTop=18;
    dom.fire('pointerdown',current);
    assert.deepEqual(calls,[[0,400],[0,18]]);
    viewer.isConnected=false;
    dom.fire('pointerdown',current);
    assert.equal(calls.length,2);
  } finally {dom.dispose();}
});

test('unload and repeated registration release callbacks and pointer ownership without duplicate listeners',()=>{
  const dom=fixture();
  try {
    const viewer=dom.node('inspector',['uno-scrollviewer']);
    const presenter=dom.node('presenter',['uno-scrollcontentpresenter'],viewer);
    const old=[],current=[];
    dom.register(viewer.id,(...offsets)=>old.push(offsets));
    dom.fire('pointerdown',presenter,3);
    dom.register(viewer.id,(...offsets)=>current.push(offsets));
    dom.fire('pointerup',dom.body,3);
    dom.fire('pointerdown',presenter,4);
    assert.deepEqual(old,[[0,0]]);
    assert.deepEqual(current,[[0,0]],'Replacing a registration drops its previous callback and active pointers');
    assert.ok([...dom.listeners.values()].every(entries=>entries.length===1));
    dom.remove(viewer.id);
    assert.ok([...dom.listeners.values()].every(entries=>entries.length===0));
    dom.fire('pointerup',presenter,4);
    assert.equal(current.length,1);
    dom.register(viewer.id,(...offsets)=>current.push(offsets));
    dom.fire('pointerup',dom.body,4);
    dom.fire('pointerdown',presenter,5);
    assert.equal(current.length,2);
    assert.ok([...dom.listeners.values()].every(entries=>entries.length===1));
  } finally {dom.dispose();}
});

test('shared capture listeners remain until the last registered viewer unloads and invalid offsets are ignored',()=>{
  const dom=fixture();
  try {
    const one=dom.node('one',['uno-scrollviewer']),two=dom.node('two',['uno-scrollviewer']);
    const first=dom.node('first',['uno-scrollcontentpresenter'],one),second=dom.node('second',['uno-scrollcontentpresenter'],two);
    const calls=[];
    dom.register(one.id,(...offsets)=>calls.push(['one',...offsets]));
    dom.register(two.id,(...offsets)=>calls.push(['two',...offsets]));
    first.scrollTop=Number.NaN;dom.fire('pointerdown',first);
    assert.deepEqual(calls,[]);
    dom.remove(one.id);
    second.scrollTop=9;dom.fire('pointerdown',second);
    assert.deepEqual(calls,[['two',0,9]]);
    assert.ok([...dom.listeners.values()].every(entries=>entries.length===1));
    dom.remove(two.id);
    assert.ok([...dom.listeners.values()].every(entries=>entries.length===0));
  } finally {dom.dispose();}
});
