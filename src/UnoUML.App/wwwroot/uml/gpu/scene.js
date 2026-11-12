// Retained SVG -> shared Uno WebGPU scene compiler. The original SVG remains
// the semantic/edit/export document; this module never requests a GPU device.
import {GeometryError,identity,multiply,inverse,transformPoint,transformBounds,maxScale,numbers,parseTransform,flattenPath,ellipseContours,rectContours,boundsOf,boundsOfPoints,fillTriangles,strokeTriangles,intersectBounds} from './geometry.js';
import {resolvePaint,localReference} from './paint.js';
import {RasterCache,rasterCommands,qualityBucket} from './resources.js';
import {CanvasOverlayScene} from './overlays.js';
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
function cssViewportLength(svg,style,property){
 const text=style?.getPropertyValue(property)||'';
 if(/^[+]?(?:\d+\.?\d*|\.\d+)(?:px)?$/.test(text)){const value=parseFloat(text);if(value>0)return value;}
 return(property==='width'?svg.clientWidth:svg.clientHeight)||svg[property]?.baseVal?.value||0;
}
function viewportBounds(svg,style){
 const b=svg.viewBox?.baseVal;
 if(b&&b.width>0&&b.height>0)return[b.x,b.y,b.width,b.height];
 const width=cssViewportLength(svg,style,'width'),height=cssViewportLength(svg,style,'height');
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
const GEOMETRY_PROPERTIES={path:['d'],rect:['x','y','width','height','rx','ry'],circle:['cx','cy','r'],ellipse:['cx','cy','rx','ry'],line:['x1','y1','x2','y2']};
function verifyCssGeometry(ctx,element,style,contours,tolerance){
 for(const property of GEOMETRY_PROPERTIES[element.localName]||[]){
  const value=style.getPropertyValue(property);if(!value)continue;
  if(property==='d'){
   if(value==='none'){if((element.getAttribute('d')||'').trim())throw new GeometryError('css-geometry');continue;}
   const path=/^path\(\s*(["'])([^"']*)\1\s*\)$/.exec(value);if(!path)throw new GeometryError('css-geometry');
   if(JSON.stringify(flattenPath(path[2],tolerance))!==JSON.stringify(contours))throw new GeometryError('css-geometry');continue;
  }
  if(value==='auto'){if(element.hasAttribute(property))throw new GeometryError('css-geometry');continue;}
  const horizontal=['x','x1','x2','cx','width','rx'].includes(property);
  const vertical=['y','y1','y2','cy','height','ry'].includes(property);
  const base=horizontal?ctx.viewport[2]:vertical?ctx.viewport[3]:Math.hypot(ctx.viewport[2],ctx.viewport[3])/Math.SQRT2;
  const actual=lengthValue(value,base),attribute=attrLength(element,property);
  if(Math.abs(actual-attribute)>1e-8*Math.max(1,Math.abs(actual),Math.abs(attribute)))throw new GeometryError('css-geometry');
 }
}
function geometryFor(ctx,element,style,matrix){
 const scale=qualityBucket(ctx.scale*maxScale(matrix)),tolerance=.2/Math.max(.001,scale);
 const attributes=Array.from(element.attributes).filter(a=>!a.name.startsWith('data-')&&!['id','class','transform','fill','stroke','opacity','fill-opacity','stroke-opacity'].includes(a.name)).map(a=>a.name+'='+a.value).join('|');
 const nonScaling=style.vectorEffect==='non-scaling-stroke';
 if(style.vectorEffect&&style.vectorEffect!=='none'&&!nonScaling)throw new GeometryError('unsupported-vector-effect');
 const host=multiply(ctx.rootMatrix,matrix),hostLinear=[host[0],host[1],host[2],host[3],0,0];
 const signature=element.localName+'|'+attributes+'|'+scale+'|'+STROKE_PROPERTIES.map(p=>style[p]).join('|')+'|'+(GEOMETRY_PROPERTIES[element.localName]||[]).map(p=>style.getPropertyValue(p)).join('|')+'|'+style.fillRule+'|'+(style.fill==='none')+'|'+(style.stroke==='none')+(nonScaling?'|'+hostLinear.join(','):'');
 let item=ctx.geometryCache.get(element);
 if(item?.signature===signature){ctx.stats.geometryCacheHits++;return{...item,strokeMatrix:nonScaling?multiply(ctx.rootInverse,[1,0,0,1,host[4],host[5]]):matrix};}
 if(element.style.getPropertyValue('d')||element.style.getPropertyValue('r')||element.style.getPropertyValue('rx')||element.style.getPropertyValue('width'))throw new GeometryError('css-geometry');
 if(element.hasAttribute('pathLength'))throw new GeometryError('path-length-normalization');
 const contours=primitiveContours(element,tolerance),objectBounds=bbox(element);
 verifyCssGeometry(ctx,element,style,contours,tolerance);
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
export async function compileScene(svg,{scale=1,rasterCache=new RasterCache(),geometryCache=new WeakMap(),rasterLimits,isCurrent=()=>true}={}){
 // Source replacement can happen while fonts or decoded image pixels are
 // pending. Stop before the next owner/tile without cancelling shared cache work.
 const checkpoint=()=>{if(!isCurrent())throw new Error('scene-compilation-cancelled');};
 checkpoint();
 if(globalThis.document?.fonts?.status==='loading')await document.fonts.ready;
 checkpoint();
 const started=performance.now(),styles=new WeakMap(),style=el=>{let value=styles.get(el);if(!value){value=getComputedStyle(el);styles.set(el,value);}return value;};
 const rootMatrix=arrayMatrix(svg.getCTM()),rootInverse=inverse(rootMatrix),viewport=viewportBounds(svg,style(svg)),byId=new Map();
 for(const el of svg.querySelectorAll('[id]'))byId.set(el.id,el);if(svg.id)byId.set(svg.id,svg);
 const stats={vectorCommands:0,rasterCommands:0,vertices:0,geometryBuilds:0,geometryCacheHits:0,rasterReasons:{},textureBytes:0,resolutionBucket:scale};
 const rootStyle=style(svg),overflow=rootStyle.overflow||'hidden';
 const width=cssViewportLength(svg,rootStyle,'width')||viewport[2],height=cssViewportLength(svg,rootStyle,'height')||viewport[3];
 const rootClip=overflow==='visible'?null:axisAligned(rootInverse)?transformBounds([0,0,width,height],rootInverse):null;
 const ctx={svg,style,byId,scale,rootMatrix,rootInverse,viewport,rootClip,rasterCache,geometryCache,rasterLimits,stats,checkpoint};
 const commands=[],textures=new Map();rasterCache.beginFrame();
 async function raster(element,reason,matrix,clip){
  checkpoint();
  const result=await rasterCommands(ctx,element,ownerId(element),reason,matrix);
  checkpoint();
  for(const command of result.commands){if(clip)command.clip=clip;commands.push(command);}
  for(const texture of result.textures)textures.set(texture.id,texture);
  stats.rasterReasons[reason]=(stats.rasterReasons[reason]||0)+1;stats.rasterCommands+=result.commands.length;
 }
 async function visit(element,inheritedClip){
  checkpoint();
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
 checkpoint();
 if(commands.length>32768||stats.vertices>2097152)throw new GeometryError('scene-geometry-budget');
 stats.textureBytes=Array.from(textures.values()).reduce((sum,t)=>sum+t.width*t.height*4,0);
 stats.cacheBytes=rasterCache.bytes;stats.rasterCacheHits=rasterCache.hits;stats.rasterCacheMisses=rasterCache.misses;
 stats.compileMs=performance.now()-started;
 return{commands,textures:Array.from(textures.values()),stats};
}
function sameArray(a,b){return a===b||Array.isArray(a)&&Array.isArray(b)&&a.length===b.length&&a.every((value,index)=>value===b[index]);}
function sameCommandBody(a,b){
 return (a.space??'world')===(b.space??'world')&&a.kind===b.kind&&a.rule===b.rule&&a.image===b.image&&sameArray(a.vertices,b.vertices)&&sameArray(a.bounds,b.bounds)&&sameArray(a.clip,b.clip)&&JSON.stringify(a.paint)===JSON.stringify(b.paint);
}
export function diffCommands(previous,next,{reset=false}={}){
 const oldMap=reset?new Map():new Map(previous.map(command=>[command.id,command])),changes=[];
 for(const command of next){
  const old=oldMap.get(command.id);
  if(!old){changes.push(command);continue;}
  if(old===command)continue;
  const body=sameCommandBody(old,command);
  if(body&&sameArray(old.matrix,command.matrix))continue;
  changes.push(body?{id:command.id,matrix:command.matrix}:command);
 }
 const nextIds=new Set(next.map(command=>command.id));
 return{commands:changes,remove:reset?[]:previous.filter(command=>!nextIds.has(command.id)).map(command=>command.id),order:next.map(command=>command.id)};
}
// Only these classes are owned by selection presentation. Author classes,
// theme changes and all other SVG attributes still invalidate compilation.
const semanticClasses=value=>String(value||'').split(/\s+/).filter(name=>name&&name!=='multi-mark').sort().join(' ');
function sceneMutation(record){
 if(record.type!=='attributes')return true;
 if(String(record.attributeName).startsWith('data-'))return false;
 return record.attributeName!=='class'||semanticClasses(record.oldValue)!==semanticClasses(record.target.getAttribute('class'));
}
export class ScenePresentation{
 constructor(view,emitUpdate,{onStats=()=>{},onError=()=>{},onPending=()=>{},rasterOptions,rasterLimits}={}){
  this.view=view;this.emitUpdate=emitUpdate;this.onStats=onStats;this.onError=onError;this.onPending=onPending;
  this.rasterCache=new RasterCache(rasterOptions);this.rasterLimits=rasterLimits;this.geometryCache=new WeakMap();
  this.svg=null;this.sourceVersion=0;this.generation=0;this.commands=[];this.diagramCommands=[];this.overlayCommands=[];
  this.observedOverlays=[];this.uploadedTextures=new Set();this._lastRevision=0;this.lastStats=null;
  this.pendingScene=false;this.disposed=false;this.reset=true;this.frame=0;this.running=false;this.dirty=false;this.overlayDirty=true;
  this.camera=null;this.lastCamera=null;this.bucket=1;this.waitingForViewport=false;
  this.observer=new MutationObserver(records=>{if(records.some(sceneMutation))this.invalidate();});
  this.overlays=new CanvasOverlayScene(view,()=>this.invalidateOverlays());
  this.fontChanged=()=>{if(this.svg){this.rasterCache.clear();this.invalidate();}};
  document.fonts?.addEventListener('loadingdone',this.fontChanged);
 }
 get lastRevision(){return this._lastRevision;}
 setScene(svg,sourceVersion=this.view.version){
  if(this.disposed)return;
  this.observer.disconnect();this.svg=svg;this.sourceVersion=sourceVersion;this.generation++;
  this.commands=[];this.diagramCommands=[];this.overlayCommands=[];this.observedOverlays=[];this.uploadedTextures.clear();
  this.geometryCache=new WeakMap();this.rasterCache.clear();this.reset=true;this.lastCamera=null;this.lastStats=null;
  this.dirty=false;this.overlayDirty=true;this.pendingScene=!!svg;
  if(!svg)return;
  this.observer.observe(svg,{attributes:true,attributeOldValue:true,childList:true,subtree:true,characterData:true});
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
  this.waitingForViewport=false;
  const bucket=qualityBucket(maxScale(this.camera.camera)*(globalThis.devicePixelRatio||1));
  if(bucket!==this.bucket){this.bucket=bucket;this.invalidate();return;}
  if(this.pendingScene||this.dirty||this.reset){if(this.dirty)this.schedule();return;}
  try{this.publishDynamic(this.readOverlays().commands);}
  catch(error){this.sceneError(error);}
 }
 schedule(){
  if(this.disposed||this.frame||this.running||!this.svg)return;
  this.frame=requestAnimationFrame(()=>{this.frame=0;void this.flush();});
 }
 invalidate(){
  if(this.disposed||!this.svg)return;
  this.generation++;this.dirty=true;this.pendingScene=true;this.onPending(true);this.schedule();
 }
 invalidateOverlays(){
  if(this.disposed||!this.svg)return;
  // Keep the last complete GPU frame during normal overlay latency. Overlay
  // mutations neither cancel an asynchronous SVG compile nor expose DOM paint.
  this.overlayDirty=true;this.schedule();
 }
 readOverlays(){
  const commands=this.overlays.snapshot();
  const delta=diffCommands(this.observedOverlays,commands);
  const changed=delta.commands.length>0||delta.remove.length>0||
    !sameArray(this.observedOverlays.map(command=>command.id),delta.order);
  this.observedOverlays=commands;this.overlayDirty=false;
  return{commands,changed};
 }
 publishDynamic(commands){
  if(this.disposed||this.pendingScene||this.dirty||this.reset)return;
  const delta=diffCommands(this.overlayCommands,commands);
  const orderChanged=!sameArray(this.overlayCommands.map(command=>command.id),delta.order);
  const geometryChanged=delta.commands.length>0||delta.remove.length>0||orderChanged;
  const cameraKey=JSON.stringify(this.camera),cameraChanged=cameraKey!==this.lastCamera;
  if(!geometryChanged&&!cameraChanged)return;
  const update={...this.camera};
  if(delta.commands.length)update.commands=delta.commands;
  if(delta.remove.length)update.remove=delta.remove;
  if(orderChanged)update.order=[...this.diagramCommands.map(command=>command.id),...delta.order];
  if(geometryChanged){this.overlayCommands=commands;this.commands=[...this.diagramCommands,...commands];}
  this.lastCamera=cameraKey;
  // onStats describes actual SVG compilation, not retained camera/overlay work.
  this.emit(update);
 }
 reportStats(){
  if(this.lastStats)this.onStats({...this.lastStats,overlayCommands:this.overlayCommands.length,
    overlayVertices:this.overlayCommands.reduce((sum,command)=>sum+(command.vertices?.length||0)/2,0),revision:this._lastRevision});
 }
 sceneError(error){
  if(this.disposed)return;
  this.pendingScene=true;this.onError(error instanceof Error?error.message:String(error));
 }
 async flush(){
  if(this.disposed||!this.svg||this.running||(!this.dirty&&!this.overlayDirty))return;
  if(!this.readCamera()){this.waitingForViewport=true;return;}
  this.waitingForViewport=false;
  // Resolve observer records before compiling. This also remembers the last
  // attempted decoration state after an error, preventing unchanged paint
  // restoration mutations from retrying an invalid transaction indefinitely.
  let overlays;
  try{overlays=this.readOverlays();}
  catch(error){this.dirty=false;this.overlayDirty=false;this.sceneError(error);return;}
  if(!this.dirty){
   if(this.pendingScene||this.reset){if(overlays.changed)this.invalidate();return;}
   try{this.publishDynamic(overlays.commands);}catch(error){this.sceneError(error);}
   return;
  }
  this.running=true;this.dirty=false;
  const generation=this.generation,version=this.sourceVersion,svg=this.svg;
  const isCurrent=()=>!this.disposed&&generation===this.generation&&version===this.sourceVersion&&version===this.view.version&&svg===this.svg;
  this.bucket=qualityBucket(maxScale(this.camera.camera)*(globalThis.devicePixelRatio||1));
  try{
   const result=await compileScene(svg,{scale:this.bucket,rasterCache:this.rasterCache,geometryCache:this.geometryCache,rasterLimits:this.rasterLimits,isCurrent});
   if(!isCurrent())return;
   if(!this.readCamera()){this.waitingForViewport=true;this.dirty=true;return;}
   // Snapshot after awaited text/image work, so camera and all editor paint
   // share one transaction even if selection changed during compilation.
   const overlayCommands=this.readOverlays().commands,commands=[...result.commands,...overlayCommands];
   const delta=diffCommands(this.commands,commands,{reset:this.reset});
   const textures=result.textures.filter(texture=>this.reset||!this.uploadedTextures.has(texture.id));
   const update={reset:this.reset,...delta,textures,...this.camera};
   this.diagramCommands=result.commands;this.overlayCommands=overlayCommands;this.commands=commands;
   this.uploadedTextures=new Set(result.textures.map(texture=>texture.id));
   this.reset=false;this.pendingScene=false;this.lastCamera=JSON.stringify(this.camera);this.lastStats=result.stats;
   this.emit(update);this.reportStats();
  }catch(error){if(isCurrent())this.sceneError(error);}
  finally{
   this.running=false;
   if(!this.disposed&&!this.waitingForViewport&&(this.dirty||this.overlayDirty))this.schedule();
  }
 }
 resetAfterRejection(){
  if(this.disposed)return;
  // Backend atomic rejection leaves the previous GPU state untouched. Retain
  // the observed overlay snapshot only to distinguish real recovery edits from
  // unchanged DOM fallback paint; the next scene transaction resets its baseline.
  this.reset=true;this.commands=[];this.uploadedTextures.clear();this.pendingScene=true;this.onPending(true);
 }
 emit(update){
  if(this.disposed)return;this._lastRevision=++transactionSequence;
  try{this.emitUpdate({...update,version:2,revision:this._lastRevision});}
  catch(error){this.resetAfterRejection();this.onError(error instanceof Error?error.message:String(error));}
 }
 presented(revision){
  if(this.disposed)return false;
  if(this.observer.takeRecords().some(sceneMutation))this.invalidate();
  if(!this.pendingScene&&!this.dirty&&!this.reset){
   // Native layout may finish before its queued resize/camera notification.
   // Measure camera and overlays from the same current DOM coordinate frame.
   // An unavailable viewport invalidates the camera key so a later resize emits
   // a fresh acknowledgement even if its dimensions return unchanged.
   if(!this.readCamera()){this.waitingForViewport=true;this.lastCamera=null;return false;}
   this.waitingForViewport=false;
   const bucket=qualityBucket(maxScale(this.camera.camera)*(globalThis.devicePixelRatio||1));
   if(bucket!==this.bucket){this.bucket=bucket;this.invalidate();return false;}
   // An unchanged hidden/style assignment must not reject the only submitted
   // frame and then emit no successor. A real pending overlay change instead
   // publishes its next combined revision before evaluating this acknowledgement.
   try{this.publishDynamic(this.readOverlays().commands);}
   catch(error){this.sceneError(error);}
  }
  const current=!this.pendingScene&&!this.dirty&&!this.reset&&!this.overlayDirty&&revision===this._lastRevision&&revision>0;
  if(current)this.onPending(false);return current;
 }
 dispose(){
  if(this.disposed)return;this.disposed=true;this.generation++;
  if(this.frame)cancelAnimationFrame(this.frame);this.frame=0;this.observer.disconnect();this.overlays.dispose();
  document.fonts?.removeEventListener('loadingdone',this.fontChanged);this.rasterCache.dispose();
  this.commands=[];this.diagramCommands=[];this.overlayCommands=[];this.observedOverlays=[];this.uploadedTextures.clear();this.svg=null;
 }
}
