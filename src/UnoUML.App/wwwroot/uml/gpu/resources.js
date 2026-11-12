// Browser text shaping and bounded compatibility tiles for SVG features that
// require an isolated compositing layer. Pixels are straight-alpha RGBA; the
// shared Uno WebGPU backend owns GPU resources and premultiplied sampling.
import {GeometryError,identity,multiply,inverse,transformBounds,maxScale,intersectBounds} from './geometry.js';
import {embedSvgFonts} from './fonts.js';
const NS='http://www.w3.org/2000/svg';
const VISUAL_PROPERTIES=[
 'color','color-interpolation','fill','fill-opacity','fill-rule','stroke','stroke-opacity','stroke-width',
 'stroke-linecap','stroke-linejoin','stroke-miterlimit','stroke-dasharray','stroke-dashoffset','paint-order',
 'opacity','visibility','display','overflow','overflow-x','overflow-y','filter','clip-path','clip-rule','mask','marker-start','marker-mid','marker-end',
 'font-family','font-size','font-style','font-weight','font-stretch','font-variant','font-kerning','font-feature-settings',
 'font-variation-settings','font-optical-sizing','letter-spacing','word-spacing','text-anchor','text-decoration',
 'dominant-baseline','alignment-baseline','baseline-shift','writing-mode','direction','unicode-bidi','white-space',
 'text-rendering','shape-rendering','image-rendering','vector-effect','stop-color','stop-opacity','flood-color','flood-opacity'
];
const GEOMETRY_STYLE={path:['d'],rect:['x','y','width','height','rx','ry'],circle:['cx','cy','r'],ellipse:['cx','cy','rx','ry'],image:['x','y','width','height'],use:['x','y','width','height'],svg:['x','y','width','height']};
let resourceSequence=0;
function hash(value){
 let a=2166136261,b=5381;for(let i=0;i<value.length;i++){const c=value.charCodeAt(i);a=Math.imul(a^c,16777619);b=Math.imul(b,33)^c;}
 return (a>>>0).toString(36)+(b>>>0).toString(36);
}
export function qualityBucket(scale){
 if(!Number.isFinite(scale)||scale<=0)return 1;
 const power=Math.log2(scale)*2,nearest=Math.round(power);
 const stable=Math.abs(power-nearest)<=Number.EPSILON*Math.max(1,Math.abs(power))*8?nearest:Math.ceil(power);
 return Math.pow(2,stable/2);
}
export function rasterTiles(bounds,scale,{tileSize=2048,maxPixels=16777216,maxTiles=64}={}){
 if(bounds.length!==4||!bounds.every(Number.isFinite)||!(bounds[2]>0&&bounds[3]>0)||!Number.isFinite(scale)||!(scale>0))throw new GeometryError('invalid-raster-bounds');
 const width=Math.max(1,Math.ceil(bounds[2]*scale)),height=Math.max(1,Math.ceil(bounds[3]*scale));
 if(width*height>maxPixels)throw new GeometryError('raster-pixel-budget');
 const columns=Math.ceil(width/tileSize),rows=Math.ceil(height/tileSize);
 if(columns*rows>maxTiles)throw new GeometryError('raster-tile-budget');
 // Derive every boundary from the same integer pixel grid: adjacent quads
 // share exactly the same edge, even when the scaled size was rounded up.
 const sx=width/bounds[2],sy=height/bounds[3],tiles=[];
 for(let y=0;y<height;y+=tileSize)for(let x=0;x<width;x+=tileSize){
  const w=Math.min(tileSize,width-x),h=Math.min(tileSize,height-y);
  tiles.push({width:w,height:h,bounds:[bounds[0]+x/sx,bounds[1]+y/sy,w/sx,h/sy]});
 }
 return tiles;
}
export function bytesToBase64(bytes){
 const alphabet='ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/';let result='',part='';
 for(let i=0;i<bytes.length;i+=3){
  const a=bytes[i],hasB=i+1<bytes.length,hasC=i+2<bytes.length,b=hasB?bytes[i+1]:0,c=hasC?bytes[i+2]:0;
  part+=alphabet[a>>2]+alphabet[((a&3)<<4)|(b>>4)]+(hasB?alphabet[((b&15)<<2)|(c>>6)]:'=')+(hasC?alphabet[c&63]:'=');
  if(part.length>=16384){result+=part;part='';}
 }return result+part;
}
function setStyle(source,clone,ctx){
 const style=ctx.style(source);
 if(ctx.fontFamilies&&['text','tspan','use'].includes(source.localName))ctx.fontFamilies.add(style.fontFamily);
 const properties=[...VISUAL_PROPERTIES,...(GEOMETRY_STYLE[source.localName]||[])];
 if(typeof source.getCTM==='function')properties.push('transform','transform-origin','transform-box');
 for(const property of properties){
  const value=style.getPropertyValue(property);
  if(value)clone.style.setProperty(property,value);
 }
 if(style.vectorEffect==='non-scaling-stroke'&&style.stroke&&style.stroke!=='none'){
  const m=source.getCTM(),sx=m?Math.hypot(m.a,m.b):0,sy=m?Math.hypot(m.c,m.d):0;
  if(!(sx>0)||Math.abs(sx-sy)>1e-7*Math.max(sx,sy)||Math.abs(m.a*m.c+m.b*m.d)>1e-7*sx*sy)throw new GeometryError('nonuniform-nonscaling-raster');
  if(['markerStart','markerMid','markerEnd'].some(p=>style[p]&&style[p]!=='none'))throw new GeometryError('nonscaling-marker-raster');
  const vb=ctx.viewport||viewport(ctx),base=Math.hypot(vb[2],vb[3])/Math.SQRT2;
  clone.style.setProperty('vector-effect','none');
  clone.style.setProperty('stroke-width',String(scalar(style.strokeWidth||1,base)/sx));
  if(style.strokeDasharray&&style.strokeDasharray!=='none')clone.style.setProperty('stroke-dasharray',style.strokeDasharray.split(/[\s,]+/).filter(Boolean).map(n=>scalar(n,base)/sx).join(' '));
  clone.style.setProperty('stroke-dashoffset',String(scalar(style.strokeDashoffset||0,base)/sx));
 }
 // A CSS URL resolved by the browser includes the document URL. Serialized
 // SVGs must reference their own copied definitions instead of that document.
 for(const attribute of Array.from(clone.attributes||[])){
  if(attribute.value.includes('url('))clone.setAttribute(attribute.name,attribute.value.replace(/url\(["']?[^"'()]*#([^"'()]+)["']?\)/g,'url(#$1)'));
 }
 if(ctx.shallow)return;
 const children=Array.from(source.children),copies=Array.from(clone.children);
 for(let i=0;i<children.length;i++)setStyle(children[i],copies[i],ctx);
}
function definitions(ctx,content){
 const defs=document.createElementNS(NS,'defs'),copied=new Set(),queue=[];
 const fragment=value=>{
  const index=value.lastIndexOf('#');if(index<0)return null;
  const id=value.slice(index+1);try{return decodeURIComponent(id);}catch{return id;}
 };
 function scan(root){
  // Inspect DOM values before XML escaping. CSSOM may canonicalize url(#id)
  // to url("#id"); scanning serialized XML would mistake &quot; for ID bytes.
  for(const node of [root,...root.querySelectorAll('*')]){
   for(const attribute of Array.from(node.attributes||[])){
    const value=attribute.value;
    if(attribute.localName==='href'&&value.startsWith('#'))queue.push(fragment(value));
    for(const match of value.matchAll(/url\(\s*(?:"([^"]*)"|'([^']*)'|([^)]*?))\s*\)/g)){
     const id=fragment(match[1]??match[2]??match[3]);if(id!==null)queue.push(id);
    }
   }
  }
 }
 scan(content);
 while(queue.length){
  const id=queue.pop();if(copied.has(id))continue;copied.add(id);
  if(copied.size>512)throw new GeometryError('raster-reference-budget');
  const source=ctx.byId.get(id);if(!source)throw new GeometryError('missing-raster-reference');
  const clone=source.cloneNode(true);
  // Referenced geometry retains <use> instance inheritance. Paint servers and
  // effects instead inherit at their definition site, so freeze their styles.
  if(['linearGradient','radialGradient','filter','mask','pattern','clipPath','marker'].includes(source.localName))setStyle(source,clone,ctx);
  scan(clone);defs.append(clone);
 }
 return defs;
}
function viewport(ctx){
 if(ctx.viewport)return ctx.viewport.slice();
 const vb=ctx.svg.viewBox?.baseVal;
 if(vb&&vb.width>0&&vb.height>0)return[vb.x,vb.y,vb.width,vb.height];
 const width=ctx.svg.width?.baseVal?.value,height=ctx.svg.height?.baseVal?.value;
 if(!(width>0&&height>0))throw new GeometryError('unresolved-svg-viewport');
 return[0,0,width,height];
}
export function makeRasterSnapshot(ctx,element,{world=false}={}){
 ctx={...ctx,fontFamilies:new Set()};
 const rootBounds=viewport(ctx),inner=document.createElementNS(NS,'svg');
 inner.setAttribute('x',rootBounds[0]);inner.setAttribute('y',rootBounds[1]);
 inner.setAttribute('width',rootBounds[2]);inner.setAttribute('height',rootBounds[3]);
 inner.setAttribute('viewBox',rootBounds.join(' '));inner.setAttribute('preserveAspectRatio','none');
 inner.setAttribute('overflow','visible');inner.setAttribute('xmlns',NS);
 const clone=element.cloneNode(true);setStyle(element,clone,ctx);
 if(!world){clone.removeAttribute('transform');clone.style.removeProperty('transform');}
 if(element===ctx.svg){clone.setAttribute('x',rootBounds[0]);clone.setAttribute('y',rootBounds[1]);clone.setAttribute('width',rootBounds[2]);clone.setAttribute('height',rootBounds[3]);clone.setAttribute('viewBox',rootBounds.join(' '));clone.style.removeProperty('width');clone.style.removeProperty('height');clone.style.removeProperty('x');clone.style.removeProperty('y');clone.removeAttribute('transform');clone.style.removeProperty('transform');}
 let content=clone;
 if(world){
  for(let ancestor=element.parentElement;ancestor&&ancestor!==ctx.svg;ancestor=ancestor.parentElement){
   const wrapper=ancestor.cloneNode(false);setStyle(ancestor,wrapper,{...ctx,style:ctx.style,shallow:true});
   // setStyle normally copies descendants too; an empty wrapper has no children.
   wrapper.replaceChildren(content);content=wrapper;
  }
 }
 inner.append(definitions(ctx,content),content);
 // Root styles are inherited, while root opacity/filter are compiled as one
 // isolated root owner when present and must not be applied a second time.
 for(const property of ['color','font-family','font-size','font-style','font-weight','font-stretch','letter-spacing','word-spacing','text-anchor','writing-mode','direction']){
  const value=ctx.style(ctx.svg).getPropertyValue(property);if(value)inner.style.setProperty(property,value);
 }
 return{xml:new XMLSerializer().serializeToString(inner),rootBounds,fontFamilies:Array.from(ctx.fontFamilies)};
}
export function serializeTile(snapshot,tile,includeFonts=true){
 const b=tile.bounds;
 const css=includeFonts&&snapshot.fontCss?'<style type="text/css">'+snapshot.fontCss.replaceAll('&','&amp;').replaceAll('<','&lt;')+'</style>':'';
 return '<svg xmlns="'+NS+'" width="'+tile.width+'" height="'+tile.height+'" viewBox="'+b.join(' ')+'" preserveAspectRatio="none">'+css+snapshot.xml+'</svg>';
}
async function rasterize(xml,width,height){
 const blob=new Blob([xml],{type:'image/svg+xml;charset=utf-8'}),url=URL.createObjectURL(blob),image=new Image();
 try{
  image.decoding='async';
  await new Promise((resolve,reject)=>{image.onload=resolve;image.onerror=()=>reject(new GeometryError('raster-image-decode'));image.src=url;});
  const canvas=document.createElement('canvas');canvas.width=width;canvas.height=height;
  const context=canvas.getContext('2d',{willReadFrequently:true});
  if(!context)throw new GeometryError('raster-context-unavailable');
  context.clearRect(0,0,width,height);context.drawImage(image,0,0,width,height);
  const rgba=context.getImageData(0,0,width,height).data;
  const result={id:'svg-raster-'+(++resourceSequence),width,height,rgba:bytesToBase64(rgba)};
  canvas.width=canvas.height=1;return result;
 }finally{image.onload=image.onerror=null;image.src='';URL.revokeObjectURL(url);}
}
export class RasterCache{
 constructor({maxBytes=67108864,maxEntries=8192,raster=rasterize}={}){
  this.maxBytes=maxBytes;this.maxEntries=maxEntries;this.raster=raster;this.entries=new Map();this.bytes=0;this.clock=0;this.disposed=false;this.hits=0;this.misses=0;
 }
 beginFrame(){for(const entry of this.entries.values())entry.pinned=false;}
 async get(snapshot,tile){
  if(this.disposed)throw new GeometryError('raster-cache-disposed');
  const signature=tile.width+'x'+tile.height+':'+serializeTile(snapshot,tile,false)+'|fonts:'+(snapshot.fontKey||''),key=hash(signature);
  if(signature.length>2097152)throw new GeometryError('raster-snapshot-budget');
  let entry=this.entries.get(key);
  // Keep the full signature, not only its hash, so a collision cannot substitute
  // a different glyph, gradient, or confidential document fragment.
  if(entry&&entry.signature===signature&&entry.fontCss===snapshot.fontCss){entry.pinned=true;entry.used=++this.clock;this.hits++;return entry.promise;}
  const bytes=tile.width*tile.height*4;
  if(bytes>this.maxBytes)throw new GeometryError('raster-cache-budget');
  while(this.bytes+bytes>this.maxBytes||this.entries.size>=this.maxEntries){
   let oldestKey=null,oldest=Infinity;
   for(const [candidateKey,candidate] of this.entries)if(!candidate.pinned&&candidate.used<oldest){oldestKey=candidateKey;oldest=candidate.used;}
   if(oldestKey===null)throw new GeometryError('raster-cache-budget');this.drop(oldestKey);
  }
  if(entry)this.drop(key);
  entry={signature,fontCss:snapshot.fontCss,bytes,pinned:true,used:++this.clock,promise:null};this.bytes+=bytes;this.misses++;
  entry.promise=Promise.resolve().then(()=>this.raster(serializeTile(snapshot,tile),tile.width,tile.height)).catch(error=>{if(this.entries.get(key)===entry)this.drop(key);throw error;});
  this.entries.set(key,entry);return entry.promise;
 }
 drop(key){const entry=this.entries.get(key);if(entry){this.bytes-=entry.bytes;this.entries.delete(key);}}
 clear(){this.entries.clear();this.bytes=0;}
 dispose(){this.disposed=true;this.clear();}
}
export function localRasterBounds(element,style,scale,ctx=null,matrix=null){
 if(ctx&&matrix&&element.localName==='text')return transformBounds(worldRasterBounds(ctx,element),inverse(matrix));
 const b=element.getBBox(),width=Number.parseFloat(style.strokeWidth)||0,miter=Math.max(1,Number.parseFloat(style.strokeMiterlimit)||4);
 const pad=Math.max(1/scale,style.stroke!=='none'?width*miter*.5:0);
 if(![b.x,b.y,b.width,b.height,pad].every(Number.isFinite))throw new GeometryError('invalid-raster-bounds');
 return[b.x-pad,b.y-pad,Math.max(b.width,0)+2*pad,Math.max(b.height,0)+2*pad];
}
function unionBounds(a,b){
 if(!a)return b;if(!b)return a;const x=Math.min(a[0],b[0]),y=Math.min(a[1],b[1]);
 return[x,y,Math.max(a[0]+a[2],b[0]+b[2])-x,Math.max(a[1]+a[3],b[1]+b[3])-y];
}
function scalar(value,base=1){
 const text=String(value??0).trim();
 if(!/^[+-]?(?:\d+\.?\d*|\.\d+)(?:e[+-]?\d+)?(?:%|px)?$/i.test(text))throw new GeometryError('unbounded-effect-length');
 const result=parseFloat(text)*(text.endsWith('%')?base/100:1);
 if(!Number.isFinite(result)||Math.abs(result)>1e9)throw new GeometryError('unbounded-effect-length');return result;
}
function localBox(element){const b=element.getBBox();return[b.x,b.y,b.width,b.height];}
function worldMatrix(ctx,element){
 if(element===ctx.svg)return identity();
 const m=element.getCTM();if(!m)throw new GeometryError('detached-raster-transform');
 return multiply(ctx.rootInverse,[m.a,m.b,m.c,m.d,m.e,m.f]);
}
function filterBounds(ctx,element,style,matrix){
 const filter=style.filter;if(!filter||filter==='none')return null;
 const reference=/^url\(\s*["']?[^"'()]*#([^"'()]+)["']?\s*\)$/.exec(filter);
 if(!reference)throw new GeometryError('unbounded-css-filter');
 const definition=ctx.byId.get(reference[1]);if(definition?.localName!=='filter')throw new GeometryError('missing-filter');
 const units=definition.getAttribute('filterUnits')||'objectBoundingBox';
 if(!['objectBoundingBox','userSpaceOnUse'].includes(units))throw new GeometryError('unbounded-filter-units');
 const object=units==='objectBoundingBox',box=localBox(element);
 const b=object?box:ctx.viewport;
 const x=scalar(definition.getAttribute('x')??'-10%',object?1:b[2]),y=scalar(definition.getAttribute('y')??'-10%',object?1:b[3]);
 const w=scalar(definition.getAttribute('width')??'120%',object?1:b[2]),h=scalar(definition.getAttribute('height')??'120%',object?1:b[3]);
 const region=object?[box[0]+x*box[2],box[1]+y*box[3],w*box[2],h*box[3]]:[x,y,w,h];
 if(!(region[2]>0&&region[3]>0))return[0,0,0,0];
 return transformBounds(region,matrix);
}
export function worldRasterBounds(ctx,element){
 const ignored=new Set(['defs','title','desc','metadata','style','linearGradient','radialGradient','stop','pattern','marker','symbol','clipPath','mask','filter']);
 function visit(node){
  if(ignored.has(node.localName))return null;
  const style=ctx.style(node);if(style.display==='none'||Number(style.opacity)===0)return null;
  const matrix=worldMatrix(ctx,node),filtered=filterBounds(ctx,node,style,matrix);
  // SVG filter regions are explicit clipping regions, including when their
  // outputs extend beyond the source geometry and the outer SVG viewport.
  if(filtered)return filtered;
  if(['g','a','svg'].includes(node.localName)){
   let result=null;for(const child of node.children)result=unionBounds(result,visit(child));return result;
  }
  if(style.visibility==='hidden'||style.visibility==='collapse')return null;
  if(typeof node.getBBox!=='function')throw new GeometryError('unbounded-raster-element');
  for(const property of ['markerStart','markerMid','markerEnd'])if(style[property]&&style[property]!=='none')throw new GeometryError('unbounded-marker');
  if(node.localName==='use'){
   const href=node.getAttribute('href')||node.getAttributeNS('http://www.w3.org/1999/xlink','href')||'';
   const target=href.startsWith('#')?ctx.byId.get(href.slice(1)):null;
   if(!target)throw new GeometryError('unbounded-use');
   // getBBox includes the instantiated geometry, but browser stroke-inclusive
   // getBBox support is inconsistent for use shadow trees. Never guess a bound
   // for a referenced stroked tree until its instance CTMs can be measured.
   if([target,...target.querySelectorAll('*')].some(el=>{const st=ctx.style(el);return el!==target&&el.localName==='use'||st.stroke&&st.stroke!=='none'||st.filter&&st.filter!=='none'||['markerStart','markerMid','markerEnd'].some(p=>st[p]&&st[p]!=='none');}))throw new GeometryError('unbounded-use-paint');
  }
  const box=localBox(node),stroke=style.stroke&&style.stroke!=='none';
  let pad=0;
  if(stroke){
   const width=scalar(style.strokeWidth||1,Math.hypot(ctx.viewport[2],ctx.viewport[3])/Math.SQRT2);
   const limit=Math.max(Math.SQRT2,Number(style.strokeMiterlimit)||4);pad=Math.abs(width)*limit*.5;
  }
  let result;
  if(style.vectorEffect==='non-scaling-stroke'){
   result=transformBounds(box,matrix);pad*=maxScale(ctx.rootInverse);result=[result[0]-pad,result[1]-pad,result[2]+2*pad,result[3]+2*pad];
  }else result=transformBounds([box[0]-pad,box[1]-pad,box[2]+2*pad,box[3]+2*pad],matrix);
  // Individual text spans can override stroke width while the <text> bbox
  // includes only glyph geometry. Union their own painted bounds as well.
  if(node.localName==='text'||node.localName==='tspan')for(const child of node.children)result=unionBounds(result,visit(child));
  return result;
 }
 const result=visit(element);
 if(!result)return[0,0,0,0];
 const pad=1/Math.max(.001,ctx.scale);
 return[result[0]-pad,result[1]-pad,result[2]+pad*2,result[3]+pad*2];
}
export async function rasterCommands(ctx,element,id,reason,matrix){
 ctx.checkpoint?.();
 const local=element!==ctx.svg&&element.localName!=='svg';
 ctx.fontEmbeddingCache??=new Map();
 const snapshot=await embedSvgFonts(ctx.svg,makeRasterSnapshot(ctx,element,{world:!local}),ctx.fontEmbeddingCache);
 ctx.checkpoint?.();
 const scale=qualityBucket(ctx.scale*(local?maxScale(matrix):1));
 let bounds;
 if(local&&(reason==='text'||reason==='image'))bounds=localRasterBounds(element,ctx.style(element),scale,ctx,matrix);
 else{
  try{bounds=worldRasterBounds(ctx,element);}catch(error){if(!ctx.rootClip)throw error;bounds=ctx.rootClip;}
  if(ctx.rootClip)bounds=intersectBounds(bounds,ctx.rootClip);
  if(local)bounds=transformBounds(bounds,inverse(matrix));
 }
 // Suppress CTM cancellation noise during translation, far below one
 // thousandth of a physical pixel, so unchanged layers retain their texture.
 const grid=1e-6/Math.max(.001,scale);bounds=bounds.map(v=>Math.round(v/grid)*grid);
 if(!(bounds[2]>0&&bounds[3]>0))return{commands:[],textures:[],reason};
 const tiles=rasterTiles(bounds,scale,ctx.rasterLimits),commands=[],textures=[];
 for(let i=0;i<tiles.length;i++){
  ctx.checkpoint?.();
  const tile=tiles[i],resource=await ctx.rasterCache.get(snapshot,tile);
  ctx.checkpoint?.();textures.push(resource);
  commands.push({id:id+':raster:'+i,kind:'image',matrix:local?matrix:identity(),bounds:tile.bounds,image:resource.id});
 }
 return{commands,textures,reason};
}
