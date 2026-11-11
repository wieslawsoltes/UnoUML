import assert from 'node:assert/strict';
import test from 'node:test';
import {transformPoint} from '../src/UnoUML.App/wwwroot/uml/gpu/geometry.js';
import {localReference,parseColor,resolvePaint} from '../src/UnoUML.App/wwwroot/uml/gpu/paint.js';
import {qualityBucket,rasterTiles,bytesToBase64,RasterCache} from '../src/UnoUML.App/wwwroot/uml/gpu/resources.js';

const close=(actual,expected,epsilon=1e-10)=>{
  assert.equal(actual.length,expected.length);
  actual.forEach((value,index)=>assert.ok(Math.abs(value-expected[index])<=epsilon,value+' != '+expected[index]+' at '+index));
};
function element(localName,attributes={},children=[],computed={}) {
  return {localName,children,computed,
    hasAttribute:name=>Object.hasOwn(attributes,name),
    getAttribute:name=>Object.hasOwn(attributes,name)?String(attributes[name]):null,
    getAttributeNS:(_namespace,name)=>Object.hasOwn(attributes,'xlink:'+name)?String(attributes['xlink:'+name]):null};
}
const stop=(offset,color,alpha=1)=>element('stop',{offset,'stop-color':color,'stop-opacity':alpha});
const defaultStops=()=>[stop(0,'#f00'),stop(1,'#00f')];
function context(definitions=[],width=200,height=100) {
  return {svg:{viewBox:{baseVal:{width,height}}},byId:new Map(definitions),style:el=>el.computed||{}};
}
const shape=element('rect');
const bounds=[10,20,80,40];
const paint=(gradient,ctx=context([['g',gradient]]),box=bounds)=>resolvePaint(ctx,shape,'url(#g)',box,.75);
const error=(fn,message)=>assert.throws(fn,{name:'Error',message});
const rejected=(promise,message)=>assert.rejects(promise,{name:'Error',message});
const snapshot=label=>({xml:'<svg><text>'+label+'</text></svg>'});
const tile={width:2,height:2,bounds:[.25,-.5,2,2]};

test('color parsing preserves straight alpha and clamps CSS numeric channels',()=>{
  close(parseColor('#369'),[.2,.4,.6,1]);
  close(parseColor('#369c'),[.2,.4,.6,.8]);
  close(parseColor('#33669980'),[.2,.4,.6,128/255]);
  close(parseColor('rgb(100% 50% 0% / 25%)'),[1,.5,0,.25]);
  close(parseColor('rgba(300,-20,127.5,1.5)'),[1,0,.5,1]);
  close(parseColor('transparent'),[0,0,0,0]);
  assert.equal(parseColor('none'),null);
  error(()=>parseColor('#12345'),'invalid-color');
  error(()=>parseColor('rgb(1 2 nope)'),'invalid-color');
  error(()=>parseColor('currentColor'),'unresolved-current-color');
  error(()=>parseColor('definitely-not-a-color'),'unsupported-color');
});

test('URL references preserve fragment identity after browser URL resolution',()=>{
  assert.equal(localReference('url(#paint)'), 'paint');
  assert.equal(localReference(' url("https://example.test/app.svg#paint") '),'paint');
  assert.equal(localReference("url('https://example.test/#old#final')"),'final');
  assert.equal(localReference('url(https://example.test/image.svg)'),null);
  assert.equal(localReference('#paint'),null);
});

test('solid paint separates object opacity from channel alpha and elides transparent fills',()=>{
  const ctx=context();
  assert.deepEqual(resolvePaint(ctx,shape,'#ff000080',bounds,.25),{kind:'solid',color:[1,0,0,128/255],opacity:.25});
  assert.equal(resolvePaint(ctx,shape,'transparent',bounds),null);
  assert.equal(resolvePaint(ctx,shape,'none',bounds),null);
  assert.equal(resolvePaint(ctx,shape,'#f00',bounds,0),null);
  assert.equal(resolvePaint(ctx,shape,'#f00',bounds,-1),null);
  assert.equal(resolvePaint(ctx,shape,'#f00',bounds,2).opacity,1);
  error(()=>resolvePaint(ctx,shape,'#f00',bounds,NaN),'invalid-opacity');
});

test('gradient references inherit attributes and stops but derived stops replace the base',()=>{
  const base=element('linearGradient',{x1:'25%',x2:'75%',spreadMethod:'reflect'},defaultStops());
  const derived=element('linearGradient',{href:'#base',y2:'100%'});
  const ctx=context([['g',derived],['base',base]]);
  const inherited=paint(derived,ctx);
  assert.equal(inherited.kind,'linear');assert.equal(inherited.spread,'reflect');
  close(inherited.start,[.25,0]);close(inherited.end,[.75,1]);
  assert.equal(inherited.stops.length,2);close(inherited.stops[1].color,[0,0,1,1]);
  derived.children.push(stop('.4','#0f0'));
  const replaced=paint(derived,ctx);
  assert.equal(replaced.kind,'solid');close(replaced.color,[0,1,0,1]);assert.equal(replaced.opacity,.75);
});

test('stop offsets are monotonic and stop alpha remains straight for the shader',()=>{
  const gradient=element('linearGradient',{},[
    stop('-20%','#f008',.5),stop('60%','#0f0'),stop('20%','#00f'),stop('120%','#fff',2)
  ]);
  const result=paint(gradient);
  assert.deepEqual(result.stops.map(s=>s.offset),[0,.6,.6,1]);
  close(result.stops[0].color,[1,0,0,4/15]);
  assert.equal(result.opacity,.75);
  const styled=element('stop',{offset:1,'stop-color':'#00f','stop-opacity':1},[],{stopColor:'rgb(255 128 0)',stopOpacity:'.25'});
  const stylePaint=paint(element('linearGradient',{},[stop(0,'#000'),styled]));
  close(stylePaint.stops[1].color,[1,128/255,0,.25]);
});

test('object bounding box transforms are inverted after gradient transforms',()=>{
  const result=paint(element('linearGradient',{gradientTransform:'translate(.25 .5) scale(2)'},defaultStops()));
  close(transformPoint(result.transform,[30,40]),[0,0]);
  close(transformPoint(result.transform,[190,120]),[1,1]);
  close(transformPoint(result.transform,[110,80]),[.5,.5]);
  error(()=>paint(element('linearGradient',{},defaultStops()),undefined,[0,0,0,2]),'zero-gradient-bounds');
  error(()=>paint(element('linearGradient',{gradientTransform:'scale(0)'},defaultStops())),'singular-transform');
});

test('user-space percentages use viewport axes and normalized diagonal radial units',()=>{
  const linear=paint(element('linearGradient',{gradientUnits:'userSpaceOnUse',x1:'10%',y1:'20%',x2:'90%',y2:'80%'},defaultStops()));
  close(linear.start,[20,20]);close(linear.end,[180,80]);
  close(linear.transform,[1,0,0,1,0,0]);
  const radial=paint(element('radialGradient',{gradientUnits:'userSpaceOnUse',cx:'50%',cy:'50%',r:'20%',fr:'5%'},defaultStops()));
  close(radial.center,[100,50]);close(radial.focal,[100,50]);
  assert.ok(Math.abs(radial.radius-Math.hypot(200,100)/Math.SQRT2*.2)<1e-10);
  assert.ok(Math.abs(radial.focalRadius-radial.radius*.25)<1e-10);
});

test('radial gradients retain focal circles and reject edge cases for faithful fallback',()=>{
  const radial=paint(element('radialGradient',{cx:'.5',cy:'.5',r:'.5',fx:'.6',fy:'.55',fr:'.1'},defaultStops()));
  close(radial.focal,[.6,.55]);assert.equal(radial.focalRadius,.1);assert.equal(radial.radius,.5);
  const constant=paint(element('radialGradient',{r:0},defaultStops()));
  assert.equal(constant.kind,'solid');close(constant.color,[0,0,1,1]);
  error(()=>paint(element('radialGradient',{r:-1},defaultStops())),'invalid-radial-gradient');
  error(()=>paint(element('radialGradient',{fr:'.5'},defaultStops())),'radial-gradient-edge');
  error(()=>paint(element('radialGradient',{fx:1,fy:'.5'},defaultStops())),'radial-gradient-edge');
  error(()=>paint(element('radialGradient',{fx:'.95',fr:'.1'},defaultStops())),'radial-gradient-edge');
});

test('unsupported paint semantics fail explicitly instead of approximating colors',()=>{
  const cycle=element('linearGradient',{href:'#g'},defaultStops());
  error(()=>paint(cycle),'gradient-cycle');
  error(()=>paint(element('linearGradient',{href:'#missing'},defaultStops())),'missing-gradient');
  error(()=>paint(element('linearGradient',{href:'https://example.test/#g'},defaultStops())),'external-gradient');
  error(()=>paint(element('linearGradient',{},defaultStops(),{colorInterpolation:'linearRGB'})),'linear-rgb-gradient');
  error(()=>paint(element('pattern')),'pattern');
  error(()=>resolvePaint(context(),shape,'url(#missing)',bounds),'unsupported-paint-reference');
  error(()=>paint(element('linearGradient',{gradientUnits:'invalid'},defaultStops())),'unsupported-gradient-units');
  error(()=>paint(element('linearGradient',{spreadMethod:'invalid'},defaultStops())),'unsupported-gradient-spread');
  error(()=>paint(element('linearGradient',{x2:'calc(1px + 2px)'},defaultStops())),'unsupported-gradient-length');
  error(()=>paint(element('linearGradient',{},Array.from({length:65},(_,i)=>stop(i/64,'#000')))),'gradient-stop-budget');
  assert.equal(paint(element('linearGradient')),null);
});

test('quality buckets never undersample and only change on half-octave boundaries',()=>{
  for(const scale of [.01,.2,.5,.71,1,1.001,1.414,2,7,20,100]){
    const bucket=qualityBucket(scale);
    assert.ok(bucket>=scale-1e-12);assert.ok(bucket<scale*Math.SQRT2+1e-12);
    assert.equal(qualityBucket(bucket),bucket);
  }
  assert.equal(qualityBucket(0),1);assert.equal(qualityBucket(NaN),1);assert.equal(qualityBucket(Infinity),1);
});

test('raster tiles cover fractional bounds with shared pixel edges and bounded allocation',()=>{
  const original=[-3.125,7.25,10.3,7.1],tiles=rasterTiles(original,1.3,{tileSize:4});
  assert.equal(tiles.length,12);
  assert.equal(tiles.reduce((sum,t)=>sum+t.width*t.height,0),14*10);
  for(const current of tiles){
    assert.ok(current.width>0&&current.width<=4&&current.height>0&&current.height<=4);
    assert.ok(current.bounds[0]>=original[0]-1e-12&&current.bounds[1]>=original[1]-1e-12);
    assert.ok(current.bounds[0]+current.bounds[2]<=original[0]+original[2]+1e-12);
    assert.ok(current.bounds[1]+current.bounds[3]<=original[1]+original[3]+1e-12);
  }
  for(let row=0;row<3;row++)for(let col=0;col<3;col++){
    const left=tiles[row*4+col],right=tiles[row*4+col+1];
    assert.ok(Math.abs(left.bounds[0]+left.bounds[2]-right.bounds[0])<1e-12);
  }
  assert.ok(Math.abs(tiles.at(-1).bounds[0]+tiles.at(-1).bounds[2]-(original[0]+original[2]))<1e-12);
  assert.ok(Math.abs(tiles.at(-1).bounds[1]+tiles.at(-1).bounds[3]-(original[1]+original[3]))<1e-12);
  error(()=>rasterTiles([0,0,0,2],1),'invalid-raster-bounds');
  error(()=>rasterTiles([0,0,2,2],NaN),'invalid-raster-bounds');
  error(()=>rasterTiles([0,0,10,10],1,{maxPixels:99}),'raster-pixel-budget');
  error(()=>rasterTiles([0,0,10,10],1,{tileSize:2,maxTiles:24}),'raster-tile-budget');
});

test('chunked RGBA base64 is exact across tail lengths and large payload boundaries',()=>{
  for(const length of [0,1,2,3,4,16383,16384,16385,65537]){
    const bytes=Uint8Array.from({length},(_,i)=>(i*71+13)&255);
    assert.equal(bytesToBase64(bytes),Buffer.from(bytes).toString('base64'));
  }
});

test('raster cache deduplicates concurrent requests and returns retained resources',async()=>{
  let calls=0,finish;
  const cache=new RasterCache({raster:async(_xml,width,height)=>{
    calls++;await new Promise(resolve=>{finish=resolve;});return{id:'retained',width,height,rgba:'AA=='};
  }});
  const a=cache.get(snapshot('A'),tile),b=cache.get(snapshot('A'),tile);
  await Promise.resolve();assert.equal(calls,1);assert.equal(cache.misses,1);assert.equal(cache.hits,1);assert.equal(cache.bytes,16);
  finish();const [first,second]=await Promise.all([a,b]);assert.strictEqual(first,second);
  cache.beginFrame();assert.strictEqual(await cache.get(snapshot('A'),tile),first);
  assert.equal(cache.hits,2);cache.dispose();assert.equal(cache.bytes,0);assert.equal(cache.entries.size,0);
});

test('cache pins protect an in-flight frame and LRU eviction starts at the next frame',async()=>{
  let serial=0;
  const cache=new RasterCache({maxBytes:32,maxEntries:2,raster:async()=>({id:String(++serial)})});
  const a=await cache.get(snapshot('A'),tile),b=await cache.get(snapshot('B'),tile);
  await rejected(cache.get(snapshot('C'),tile),'raster-cache-budget');
  assert.equal(cache.bytes,32);assert.equal(cache.entries.size,2);
  cache.beginFrame();assert.strictEqual(await cache.get(snapshot('A'),tile),a);
  const c=await cache.get(snapshot('C'),tile);assert.notStrictEqual(c,b);
  cache.beginFrame();const bAgain=await cache.get(snapshot('B'),tile);
  assert.notStrictEqual(bAgain,b);assert.equal(cache.entries.size,2);assert.equal(cache.bytes,32);
  cache.dispose();
});

test('cache content changes, raster failures and disposal never leave stale ownership',async()=>{
  let serial=0;
  const cache=new RasterCache({maxBytes:32,raster:async(xml)=>{
    if(xml.includes('fail'))throw new Error('decode failed');
    return{id:String(++serial)};
  }});
  const a=await cache.get(snapshot('A'),tile);
  const changed=await cache.get(snapshot('changed'),tile);assert.notStrictEqual(changed,a);
  cache.beginFrame();
  await assert.rejects(cache.get(snapshot('fail'),tile),/decode failed/);
  assert.equal(cache.bytes,16);assert.equal(cache.entries.size,1);
  cache.clear();assert.equal(cache.bytes,0);assert.equal(cache.entries.size,0);
  cache.dispose();await rejected(cache.get(snapshot('A'),tile),'raster-cache-disposed');
});

test('in-flight raster completion cannot resurrect a cleared cache or evict a replacement',async()=>{
  const finishes=[];
  const cache=new RasterCache({raster:()=>new Promise((resolve,reject)=>finishes.push({resolve,reject}))});
  const first=cache.get(snapshot('A'),tile);await Promise.resolve();
  cache.clear();
  const second=cache.get(snapshot('A'),tile);await Promise.resolve();
  finishes[0].reject(new Error('old failed'));
  await assert.rejects(first,/old failed/);assert.equal(cache.entries.size,1);assert.equal(cache.bytes,16);
  finishes[1].resolve({id:'new'});assert.equal((await second).id,'new');
  const third=cache.get(snapshot('B'),tile);await Promise.resolve();
  cache.dispose();finishes[2].resolve({id:'disposed'});
  assert.equal((await third).id,'disposed');assert.equal(cache.entries.size,0);assert.equal(cache.bytes,0);
});

test('raster resource budgets reject excess snapshots and images before decode',async()=>{
  let calls=0;
  const cache=new RasterCache({maxBytes:15,raster:async()=>{calls++;return{};}});
  await rejected(cache.get(snapshot('A'),tile),'raster-cache-budget');
  await rejected(cache.get(snapshot('x'.repeat(2097152)),tile),'raster-snapshot-budget');
  assert.equal(calls,0);assert.equal(cache.bytes,0);assert.equal(cache.entries.size,0);
  cache.dispose();
});
