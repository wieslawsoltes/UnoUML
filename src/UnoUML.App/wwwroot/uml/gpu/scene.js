// Retained SVG -> shared Uno WebGPU scene compiler. The original SVG remains
// the semantic/edit/export document; this module never requests a GPU device.
import {GeometryError,identity,multiply,inverse,transformPoint,transformBounds,maxScale,numbers,parseTransform,flattenPath,ellipseContours,rectContours,boundsOf,boundsOfPoints,fillTriangles,strokeTriangles,intersectBounds} from './geometry.js';
import {resolvePaint,localReference} from './paint.js';
import {RasterCache,rasterCommands,qualityBucket} from './resources.js';
const GROUPS=new Set(['svg','g','a']);
const SKIP=new Set(['defs','title','desc','metadata','style','script','linearGradient','radialGradient','stop','pattern','marker','symbol','clipPath','mask','filter']);
const SHAPES=new Set(['path','rect','circle','ellipse','line','polyline','polygon']);
const STROKE_PROPERTIES=['strokeWidth','strokeLinecap','strokeLinejoin','strokeMiterlimit','strokeDasharray','strokeDashoffset','vectorEffect'];
let ownerSequence=0,transactionSequence=0;
const ownerIds=new WeakMap();
const ownerId=element=>{let id=ownerIds.get(element);if(!id){id='svg-owner-'+(++ownerSequence);ownerIds.set(element,id);}return id;};
const arrayMatrix=m=>{if(!m)throw new GeometryError('detached-svg-transform');const result=[m.a,m.b,m.c,m.d,m.e,m.f];if(!result.every(Number.isFinite))throw new GeometryError('invalid-svg-transform');return result;};
const opacity=value=>{const result=Number(value===''||value===undefined?1:value);if(!Number.isFinite(result))throw new GeometryError('invalid-opacity');return Math.max(0,Math.min(1,result));};
const axisAligned=m=>(Math.abs(m[1])<1e-8&&Math.abs(m[2])<1e-8)||(Math.abs(m[0])<1e-8&&Math.abs(m[3])<1e-8);
function viewportBounds(svg){
 const b=svg.viewBox?.baseVal;
 if(b&&b.width>0&&b.height>0)return[b.x,b.y,b.width,b.height];
 const width=svg.width?.baseVal?.value,height=svg.height?.baseVal?.value;
 if(!(width>0&&height>0))throw new GeometryError('unresolved-svg-viewport');return[0,0,width,height];
}
function lengthValue(value,base=1){
 const text=String(value??0).trim();
 if(!/^[+-]?(?:\d+\.?\d*|\.\d+)(?:e[+-]?\d+)?(?:px|%)?$/i.test(text))throw new GeometryError('unsupported-svg-length');
 const result=parseFloat(text)*(text.endsWith('%')?base/100:1);
 if(!Number.isFinite(result)||Math.abs(result)>1e9)throw new GeometryError('invalid-svg-length');return result;
}
function attrLength(element,name,fallback=0){
 const animated=element[name]?.baseVal;
 if(animated&&typeof animated.value==='number')return animated.value;
 return lengthValue(element.getAttribute(name)??fallback);
}
function primitiveContours(element,tolerance){
 const n=element.localName;
 if(n==='path')return flattenPath(element.getAttribute('d')||'',tolerance);
 if(n==='rect'){
  const rx=element.hasAttribute('rx')?attrLength(element,'rx'):element.hasAttribute('ry')?attrLength(element,'ry'):0;
  const ry=element.hasAttribute('ry')?attrLength(element,'ry'):rx;
  return rectContours(attrLength(element,'x'),attrLength(element,'y'),attrLength(element,'width'),attrLength(element,'height'),rx,ry,tolerance);
 }
 if(n==='circle')return ellipseContours(attrLength(element,'cx'),attrLength(element,'cy'),attrLength(element,'r'),attrLength(element,'r'),tolerance);
 if(n==='ellipse')return ellipseContours(attrLength(element,'cx'),attrLength(element,'cy'),attrLength(element,'rx'),attrLength(element,'ry'),tolerance);
 if(n==='line')return[{points:[[attrLength(element,'x1'),attrLength(element,'y1')],[attrLength(element,'x2'),attrLength(element,'y2')]],closed:false}];
 const coordinates=numbers(element.getAttribute('points')||'');
 if(coordinates.length%2)throw new GeometryError('invalid-point-list');
 const points=[];for(let i=0;i<coordinates.length;i+=2)points.push([coordinates[i],coordinates[i+1]]);
 return points.length?[{points,closed:n==='polygon'}]:[];
}
function bbox(element){const b=element.getBBox();return[b.x,b.y,b.width,b.height];}
function clipRectangle(ctx,element,matrix,inherited){
 const value=ctx.style(element).clipPath;if(!value||value==='none')return inherited;
 const reference=localReference(value),clip=ctx.byId.get(reference);
 if(!clip||clip.localName!=='clipPath')throw new GeometryError('complex-clip');
 const children=Array.from(clip.children).filter(c=>!SKIP.has(c.localName));
 if(children.length!==1||children[0].localName!=='rect')throw new GeometryError('complex-clip');
 const rect=children[0],units=clip.getAttribute('clipPathUnits')||'userSpaceOnUse',object=units==='objectBoundingBox';
 if(!object&&units!=='userSpaceOnUse')throw new GeometryError('complex-clip');
 if(lengthValue(rect.getAttribute('rx')||0)!==0||lengthValue(rect.getAttribute('ry')||0)!==0)throw new GeometryError('complex-clip');
 const box=object?bbox(element):ctx.viewport;
 const width=object?1:box[2],height=object?1:box[3];
 let transform=parseTransform(clip.getAttribute('transform')||'');
 if(object)transform=multiply([box[2],0,0,box[3],box[0],box[1]],transform);
 transform=multiply(matrix,multiply(transform,parseTransform(rect.getAttribute('transform')||'')));
 if(!axisAligned(transform))throw new GeometryError('complex-clip');
 const b=[lengthValue(rect.getAttribute('x')||0,width),lengthValue(rect.getAttribute('y')||0,height),lengthValue(rect.getAttribute('width')||0,width),lengthValue(rect.getAttribute('height')||0,height)];
 if(b[2]<0||b[3]<0)throw new GeometryError('invalid-clip');
 const result=transformBounds(b,transform);return inherited?intersectBounds(inherited,result):result;
}
function effectReason(style,element){
 if(style.filter&&style.filter!=='none')return'filter';
 if(style.mask&&style.mask!=='none'&&!/^none(?:\s|$)/.test(style.mask))return'mask';
 const maskImage=style.getPropertyValue('mask-image');if(maskImage&&maskImage!=='none')return'mask';
 for(const property of ['markerStart','markerMid','markerEnd'])if(style[property]&&style[property]!=='none')return'marker';
 if(style.colorInterpolation==='linearRGB')return'linear-rgb';
 if(style.getPropertyValue('mix-blend-mode')&&style.getPropertyValue('mix-blend-mode')!=='normal')return'blend-mode';
 if(element.localName==='svg'&&element!==element.ownerSVGElement&&element.ownerSVGElement)return'nested-svg';
 return null;
}
function geometryFor(ctx,element,style,matrix){
 const scale=qualityBucket(ctx.scale*maxScale(matrix)),tolerance=.2/Math.max(.001,scale);
 const attributes=Array.from(element.attributes).filter(a=>!a.name.startsWith('data-')&&!['id','class','transform','fill','stroke','opacity','fill-opacity','stroke-opacity'].includes(a.name)).map(a=>a.name+'='+a.value).join('|');
 const nonScaling=style.vectorEffect==='non-scaling-stroke';
 if(style.vectorEffect&&style.vectorEffect!=='none'&&!nonScaling)throw new GeometryError('unsupported-vector-effect');
 const host=multiply(ctx.rootMatrix,matrix),hostLinear=[host[0],host[1],host[2],host[3],0,0];
 const signature=element.localName+'|'+attributes+'|'+scale+'|'+STROKE_PROPERTIES.map(p=>style[p]).join('|')+'|'+style.fillRule+'|'+(style.fill==='none')+'|'+(style.stroke==='none')+(nonScaling?'|'+hostLinear.join(','):'');
 let item=ctx.geometryCache.get(element);
 if(item?.signature===signature){ctx.stats.geometryCacheHits++;return{...item,strokeMatrix:nonScaling?multiply(ctx.rootInverse,[1,0,0,1,host[4],host[5]]):matrix};}
 if(element.style.getPropertyValue('d')||element.style.getPropertyValue('r')||element.style.getPropertyValue('rx')||element.style.getPropertyValue('width'))throw new GeometryError('css-geometry');
 if(element.hasAttribute('pathLength'))throw new GeometryError('path-length-normalization');
 const contours=primitiveContours(element,tolerance),objectBounds=bbox(element);
 const rule=style.fillRule==='evenodd'?'evenodd':'nonzero';
 const fill=element.localName==='line'||style.fill==='none'?[]:fillTriangles(contours,rule);
 const diagonal=Math.hypot(ctx.viewport[2],ctx.viewport[3])/Math.SQRT2;
 const width=lengthValue(style.strokeWidth||1,diagonal);
 const dashText=String(style.strokeDasharray||'none'),dash=dashText==='none'?[]:dashText.split(/[\s,]+/).filter(Boolean).map(v=>lengthValue(v,diagonal));
 const strokeOptions={cap:style.strokeLinecap||'butt',join:style.strokeLinejoin||'miter',miterLimit:Number(style.strokeMiterlimit)||4,tolerance,dash,dashOffset:lengthValue(style.strokeDashoffset||0,diagonal)};
 let strokeContours=contours,strokeMatrix=matrix,paintMatrix=null;
 if(nonScaling){
  strokeContours=contours.map(c=>({closed:c.closed,points:c.points.map(p=>transformPoint(hostLinear,p))}));
  strokeMatrix=multiply(ctx.rootInverse,[1,0,0,1,host[4],host[5]]);
  paintMatrix=inverse(hostLinear);strokeOptions.tolerance=.2/Math.max(.001,ctx.scale*maxScale(strokeMatrix));
 }
 const stroke=style.stroke==='none'||!(width>0)?[]:strokeTriangles(strokeContours,width,strokeOptions);
 item={signature,objectBounds,rule,fill,fillBounds:boundsOf(contours),stroke,strokeBounds:stroke.length?boundsOfPoints(Array.from({length:stroke.length/2},(_,i)=>[stroke[i*2],stroke[i*2+1]])):[0,0,0,0],paintMatrix};
 ctx.geometryCache.set(element,item);ctx.stats.geometryBuilds++;
 return{...item,strokeMatrix};
}
function shapeCommands(ctx,element,style,matrix,clip){
 const geometry=geometryFor(ctx,element,style,matrix),alpha=opacity(style.opacity),id=ownerId(element);
 const fillPaint=resolvePaint(ctx,element,style.fill||'black',geometry.objectBounds,opacity(style.fillOpacity));
 let strokePaint=resolvePaint(ctx,element,style.stroke||'none',geometry.objectBounds,opacity(style.strokeOpacity));
 if(alpha<1&&fillPaint&&strokePaint&&geometry.fill.length&&geometry.stroke.length)throw new GeometryError('object-opacity');
 if(fillPaint)fillPaint.opacity*=alpha;
 if(strokePaint){strokePaint.opacity*=alpha;if(geometry.paintMatrix&&strokePaint.kind!=='solid')strokePaint={...strokePaint,transform:multiply(strokePaint.transform,geometry.paintMatrix)};}
 const commands={};
 if(fillPaint&&geometry.fill.length)commands.fill={id:id+':fill',kind:'fill',matrix,bounds:geometry.fillBounds,rule:geometry.rule,vertices:geometry.fill,paint:fillPaint,clip};
 if(strokePaint&&geometry.stroke.length)commands.stroke={id:id+':stroke',kind:'mesh',matrix:geometry.strokeMatrix,bounds:geometry.strokeBounds,vertices:geometry.stroke,paint:strokePaint,clip};
 const order=(style.paintOrder&&style.paintOrder!=='normal'?style.paintOrder.split(/\s+/):[]).filter(v=>v==='fill'||v==='stroke');
 for(const name of ['fill','stroke'])if(!order.includes(name))order.push(name);
 return order.map(name=>commands[name]).filter(Boolean);
}
export async function compileScene(svg,{scale=1,rasterCache=new RasterCache(),geometryCache=new WeakMap(),rasterLimits}={}){
 const started=performance.now(),styles=new WeakMap(),style=el=>{let value=styles.get(el);if(!value){value=getComputedStyle(el);styles.set(el,value);}return value;};
 const rootMatrix=arrayMatrix(svg.getCTM()),rootInverse=inverse(rootMatrix),viewport=viewportBounds(svg),byId=new Map();
 for(const el of svg.querySelectorAll('[id]'))byId.set(el.id,el);if(svg.id)byId.set(svg.id,svg);
 const stats={vectorCommands:0,rasterCommands:0,vertices:0,geometryBuilds:0,geometryCacheHits:0,rasterReasons:{},textureBytes:0,resolutionBucket:scale};
 const rootStyle=style(svg),overflow=rootStyle.overflow||'hidden';
 const width=svg.width?.baseVal?.value||viewport[2],height=svg.height?.baseVal?.value||viewport[3];
 const rootClip=overflow==='visible'?null:axisAligned(rootInverse)?transformBounds([0,0,width,height],rootInverse):null;
 const ctx={svg,style,byId,scale,rootMatrix,rootInverse,viewport,rootClip,rasterCache,geometryCache,rasterLimits,stats};
 const commands=[],textures=new Map();rasterCache.beginFrame();
 async function raster(element,reason,matrix,clip){
  if(!rootClip&&reason!=='text'&&reason!=='image')throw new GeometryError('unbounded-raster-'+reason);
  const result=await rasterCommands(ctx,element,ownerId(element),reason,matrix);
  for(const command of result.commands){if(clip)command.clip=clip;commands.push(command);}
  for(const texture of result.textures)textures.set(texture.id,texture);
  stats.rasterReasons[reason]=(stats.rasterReasons[reason]||0)+1;stats.rasterCommands+=result.commands.length;
 }
 async function visit(element,inheritedClip){
  if(SKIP.has(element.localName))return;
  const currentStyle=style(element);
  if(currentStyle.display==='none'||opacity(currentStyle.opacity)===0)return;
  const matrix=element===svg?identity():multiply(rootInverse,arrayMatrix(element.getCTM()));
  let clip=inheritedClip,reason=effectReason(currentStyle,element);
  try{clip=clipRectangle(ctx,element,matrix,inheritedClip);}catch(error){if(!(error instanceof GeometryError))throw error;reason=reason||error.message;}
  if(clip&&(!(clip[2]>0)||!(clip[3]>0)))return;
  if(reason){await raster(element,reason,matrix,inheritedClip);return;}
  if(GROUPS.has(element.localName)){
   if(opacity(currentStyle.opacity)<1){await raster(element,'group-opacity',matrix,clip);return;}
   for(const child of element.children)await visit(child,clip);
   return;
  }
  if(currentStyle.visibility==='hidden'||currentStyle.visibility==='collapse')return;
  if(element.localName==='text'){await raster(element,'text',matrix,clip);return;}
  if(element.localName==='image'){await raster(element,'image',matrix,clip);return;}
  if(element.localName==='use'){await raster(element,'use',matrix,clip);return;}
  if(SHAPES.has(element.localName)){
   try{const shapes=shapeCommands(ctx,element,currentStyle,matrix,clip);commands.push(...shapes);stats.vectorCommands+=shapes.length;for(const command of shapes)stats.vertices+=command.vertices.length/2;}
   catch(error){if(!(error instanceof GeometryError))throw error;await raster(element,error.message||'unsupported-geometry',matrix,clip);}
   return;
  }
  // An unknown visible element must never disappear from the composed diagram.
  await raster(element,'element-'+element.localName,matrix,clip);
 }
 // Blend modes depend on sibling backdrop pixels. Preserve their entire
 // compositing context instead of rasterizing an isolated child incorrectly.
 const hasBlend=[svg,...svg.querySelectorAll('*')].some(el=>{const blend=style(el).getPropertyValue('mix-blend-mode');return blend&&blend!=='normal';});
 if(hasBlend)await raster(svg,'blend-mode',identity(),rootClip);else await visit(svg,rootClip);
 if(commands.length>32768||stats.vertices>2097152)throw new GeometryError('scene-geometry-budget');
 stats.textureBytes=Array.from(textures.values()).reduce((sum,t)=>sum+t.width*t.height*4,0);
 stats.cacheBytes=rasterCache.bytes;stats.rasterCacheHits=rasterCache.hits;stats.rasterCacheMisses=rasterCache.misses;
 stats.compileMs=performance.now()-started;
 return{commands,textures:Array.from(textures.values()),stats};
}
function sameArray(a,b){return a===b||Array.isArray(a)&&Array.isArray(b)&&a.length===b.length&&a.every((value,index)=>value===b[index]);}
function sameCommandBody(a,b){
 return a.kind===b.kind&&a.rule===b.rule&&a.image===b.image&&sameArray(a.vertices,b.vertices)&&sameArray(a.bounds,b.bounds)&&sameArray(a.clip,b.clip)&&JSON.stringify(a.paint)===JSON.stringify(b.paint);
}
export function diffCommands(previous,next,{reset=false}={}){
 const oldMap=reset?new Map():new Map(previous.map(command=>[command.id,command])),changes=[];
 for(const command of next){
  const old=oldMap.get(command.id);
  if(!old){changes.push(command);continue;}
  const body=sameCommandBody(old,command);
  if(body&&sameArray(old.matrix,command.matrix))continue;
  changes.push(body?{id:command.id,matrix:command.matrix}:command);
 }
 const nextIds=new Set(next.map(command=>command.id));
 return{commands:changes,remove:reset?[]:previous.filter(command=>!nextIds.has(command.id)).map(command=>command.id),order:next.map(command=>command.id)};
}
export class ScenePresentation{
 constructor(view,emitUpdate,{onStats=()=>{},onError=()=>{},onPending=()=>{},rasterOptions,rasterLimits}={}){
  this.view=view;this.emitUpdate=emitUpdate;this.onStats=onStats;this.onError=onError;this.onPending=onPending;
  this.rasterCache=new RasterCache(rasterOptions);this.rasterLimits=rasterLimits;this.geometryCache=new WeakMap();
  this.svg=null;this.sourceVersion=0;this.generation=0;this.commands=[];this.uploadedTextures=new Set();this._lastRevision=0;
  this.pendingScene=false;this.disposed=false;this.reset=true;this.frame=0;this.running=false;this.dirty=false;this.camera=null;this.lastCamera=null;this.bucket=1;
  this.observer=new MutationObserver(records=>{if(records.some(record=>record.type!=='attributes'||!String(record.attributeName).startsWith('data-')))this.invalidate();});
  this.fontChanged=()=>{if(this.svg){this.rasterCache.clear();this.invalidate();}};
  document.fonts?.addEventListener('loadingdone',this.fontChanged);
 }
 get lastRevision(){return this._lastRevision;}
 setScene(svg,sourceVersion=this.view.version){
  if(this.disposed)return;
  this.observer.disconnect();this.svg=svg;this.sourceVersion=sourceVersion;this.generation++;
  this.commands=[];this.uploadedTextures.clear();this.geometryCache=new WeakMap();this.rasterCache.clear();this.reset=true;this.lastCamera=null;
  if(!svg)return;
  this.observer.observe(svg,{attributes:true,childList:true,subtree:true,characterData:true});
  this.readCamera();this.invalidate();
 }
 readCamera(){
  if(!this.svg||!this.svg.isConnected)return false;
  const surface=this.view.surface||this.view.host,rect=surface.getBoundingClientRect(),m=this.svg.getScreenCTM();
  if(!m||!(rect.width>0&&rect.height>0))return false;
  const camera=arrayMatrix(m);camera[4]-=rect.left;camera[5]-=rect.top;
  this.camera={camera,width:rect.width,height:rect.height};return true;
 }
 setCamera(){
  if(this.disposed||!this.readCamera())return;
  const bucket=qualityBucket(maxScale(this.camera.camera)*(globalThis.devicePixelRatio||1));
  if(bucket!==this.bucket){this.bucket=bucket;this.invalidate();return;}
  if(this.pendingScene||this.dirty||this.reset)return;
  const key=JSON.stringify(this.camera);if(key===this.lastCamera)return;
  this.emit({...this.camera});this.lastCamera=key;
 }
 invalidate(){
  if(this.disposed||!this.svg)return;
  this.generation++;this.dirty=true;this.pendingScene=true;this.onPending(true);
  if(this.frame||this.running)return;
  this.frame=requestAnimationFrame(()=>{this.frame=0;void this.flush();});
 }
 async flush(){
  if(this.disposed||!this.svg||this.running||!this.dirty)return;
  if(!this.readCamera())return;
  this.running=true;this.dirty=false;
  const generation=this.generation,version=this.sourceVersion,svg=this.svg;
  this.bucket=qualityBucket(maxScale(this.camera.camera)*(globalThis.devicePixelRatio||1));
  try{
   const result=await compileScene(svg,{scale:this.bucket,rasterCache:this.rasterCache,geometryCache:this.geometryCache,rasterLimits:this.rasterLimits});
   if(this.disposed||generation!==this.generation||version!==this.sourceVersion||version!==this.view.version||svg!==this.svg)return;
   this.readCamera();
   const delta=diffCommands(this.commands,result.commands,{reset:this.reset});
   const textures=result.textures.filter(texture=>this.reset||!this.uploadedTextures.has(texture.id));
   const update={reset:this.reset,...delta,textures,...this.camera};
   this.commands=result.commands;this.uploadedTextures=new Set(result.textures.map(texture=>texture.id));
   this.reset=false;this.pendingScene=false;this.lastCamera=JSON.stringify(this.camera);
   this.emit(update);this.onStats({...result.stats,revision:this._lastRevision});
  }catch(error){
   if(!this.disposed&&generation===this.generation){this.pendingScene=true;this.onError(error instanceof Error?error.message:String(error));}
  }finally{
   this.running=false;
   if(!this.disposed&&this.dirty&&!this.frame)this.frame=requestAnimationFrame(()=>{this.frame=0;void this.flush();});
  }
 }
 emit(update){if(this.disposed)return;this._lastRevision=++transactionSequence;this.emitUpdate({...update,revision:this._lastRevision});}
 presented(revision){
  const current=!this.disposed&&!this.pendingScene&&!this.dirty&&revision===this._lastRevision&&revision>0;
  if(current)this.onPending(false);return current;
 }
 dispose(){
  if(this.disposed)return;this.disposed=true;this.generation++;
  if(this.frame)cancelAnimationFrame(this.frame);this.frame=0;this.observer.disconnect();
  document.fonts?.removeEventListener('loadingdone',this.fontChanged);this.rasterCache.dispose();this.commands=[];this.uploadedTextures.clear();this.svg=null;
 }
}
