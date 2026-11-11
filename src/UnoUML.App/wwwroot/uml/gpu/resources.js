// Browser text shaping and bounded compatibility tiles for SVG features that
// require an isolated compositing layer. Pixels are straight-alpha RGBA; the
// shared Uno WebGPU backend owns GPU resources and premultiplied sampling.
import {GeometryError,identity,transformBounds,maxScale} from './geometry.js';
const NS='http://www.w3.org/2000/svg';
const VISUAL_PROPERTIES=[
 'color','color-interpolation','fill','fill-opacity','fill-rule','stroke','stroke-opacity','stroke-width',
 'stroke-linecap','stroke-linejoin','stroke-miterlimit','stroke-dasharray','stroke-dashoffset','paint-order',
 'opacity','visibility','display','filter','clip-path','clip-rule','mask','marker-start','marker-mid','marker-end',
 'font-family','font-size','font-style','font-weight','font-stretch','font-variant','font-kerning','font-feature-settings',
 'font-variation-settings','font-optical-sizing','letter-spacing','word-spacing','text-anchor','text-decoration',
 'dominant-baseline','alignment-baseline','baseline-shift','writing-mode','direction','unicode-bidi','white-space',
 'text-rendering','shape-rendering','image-rendering','vector-effect','stop-color','stop-opacity','flood-color','flood-opacity'
];
let resourceSequence=0;
function hash(value){
 let a=2166136261,b=5381;for(let i=0;i<value.length;i++){const c=value.charCodeAt(i);a=Math.imul(a^c,16777619);b=Math.imul(b,33)^c;}
 return (a>>>0).toString(36)+(b>>>0).toString(36);
}
export function qualityBucket(scale){
 if(!Number.isFinite(scale)||scale<=0)return 1;
 return Math.pow(2,Math.ceil(Math.log2(scale)*2)/2);
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
 for(const property of VISUAL_PROPERTIES){
  const value=style.getPropertyValue(property);
  if(value)clone.style.setProperty(property,value);
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
 const scan=xml=>{
  for(const match of xml.matchAll(/(?:url\(["']?[^"'()]*#([^"'()]+)["']?\)|(?:href|xlink:href)=["']#([^"']+)["'])/g))queue.push(match[1]||match[2]);
 };
 scan(new XMLSerializer().serializeToString(content));
 while(queue.length){
  const id=queue.pop();if(copied.has(id))continue;copied.add(id);
  if(copied.size>512)throw new GeometryError('raster-reference-budget');
  const source=ctx.byId.get(id);if(!source)throw new GeometryError('missing-raster-reference');
  const clone=source.cloneNode(true);
  // Referenced geometry retains <use> instance inheritance. Paint servers and
  // effects instead inherit at their definition site, so freeze their styles.
  if(['linearGradient','radialGradient','filter','mask','pattern','clipPath','marker'].includes(source.localName))setStyle(source,clone,ctx);
  scan(new XMLSerializer().serializeToString(clone));defs.append(clone);
 }
 return defs;
}
function viewport(ctx){
 const vb=ctx.svg.viewBox?.baseVal;
 if(vb&&vb.width>0&&vb.height>0)return[vb.x,vb.y,vb.width,vb.height];
 const width=ctx.svg.width?.baseVal?.value,height=ctx.svg.height?.baseVal?.value;
 if(!(width>0&&height>0))throw new GeometryError('unresolved-svg-viewport');
 return[0,0,width,height];
}
export function makeRasterSnapshot(ctx,element,{world=false}={}){
 const rootBounds=viewport(ctx),inner=document.createElementNS(NS,'svg');
 inner.setAttribute('x',rootBounds[0]);inner.setAttribute('y',rootBounds[1]);
 inner.setAttribute('width',rootBounds[2]);inner.setAttribute('height',rootBounds[3]);
 inner.setAttribute('viewBox',rootBounds.join(' '));inner.setAttribute('preserveAspectRatio','none');
 inner.setAttribute('overflow','visible');inner.setAttribute('xmlns',NS);
 const clone=element.cloneNode(true);setStyle(element,clone,ctx);
 if(!world){clone.removeAttribute('transform');clone.style.removeProperty('transform');}
 if(element===ctx.svg){clone.setAttribute('x',rootBounds[0]);clone.setAttribute('y',rootBounds[1]);clone.setAttribute('width',rootBounds[2]);clone.setAttribute('height',rootBounds[3]);clone.setAttribute('viewBox',rootBounds.join(' '));clone.style.removeProperty('width');clone.style.removeProperty('height');}
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
 return{xml:new XMLSerializer().serializeToString(inner),rootBounds};
}
export function serializeTile(snapshot,tile){
 const b=tile.bounds;
 return '<svg xmlns="'+NS+'" width="'+tile.width+'" height="'+tile.height+'" viewBox="'+b.join(' ')+'" preserveAspectRatio="none">'+snapshot.xml+'</svg>';
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
  const xml=serializeTile(snapshot,tile),signature=tile.width+'x'+tile.height+':'+xml,key=hash(signature);
  if(signature.length>2097152)throw new GeometryError('raster-snapshot-budget');
  let entry=this.entries.get(key);
  // Keep the full signature, not only its hash, so a collision cannot substitute
  // a different glyph, gradient, or confidential document fragment.
  if(entry&&entry.signature===signature){entry.pinned=true;entry.used=++this.clock;this.hits++;return entry.promise;}
  const bytes=tile.width*tile.height*4;
  if(bytes>this.maxBytes)throw new GeometryError('raster-cache-budget');
  while(this.bytes+bytes>this.maxBytes||this.entries.size>=this.maxEntries){
   let oldestKey=null,oldest=Infinity;
   for(const [candidateKey,candidate] of this.entries)if(!candidate.pinned&&candidate.used<oldest){oldestKey=candidateKey;oldest=candidate.used;}
   if(oldestKey===null)throw new GeometryError('raster-cache-budget');this.drop(oldestKey);
  }
  if(entry)this.drop(key);
  entry={signature,bytes,pinned:true,used:++this.clock,promise:null};this.bytes+=bytes;this.misses++;
  entry.promise=Promise.resolve().then(()=>this.raster(xml,tile.width,tile.height)).catch(error=>{if(this.entries.get(key)===entry)this.drop(key);throw error;});
  this.entries.set(key,entry);return entry.promise;
 }
 drop(key){const entry=this.entries.get(key);if(entry){this.bytes-=entry.bytes;this.entries.delete(key);}}
 clear(){this.entries.clear();this.bytes=0;}
 dispose(){this.disposed=true;this.clear();}
}
export function localRasterBounds(element,style,scale){
 const b=element.getBBox(),width=Number.parseFloat(style.strokeWidth)||0,miter=Math.max(1,Number.parseFloat(style.strokeMiterlimit)||4);
 const pad=Math.max(1/scale,style.stroke!=='none'?width*miter*.5:0);
 if(![b.x,b.y,b.width,b.height,pad].every(Number.isFinite))throw new GeometryError('invalid-raster-bounds');
 return[b.x-pad,b.y-pad,Math.max(b.width,0)+2*pad,Math.max(b.height,0)+2*pad];
}
export async function rasterCommands(ctx,element,id,reason,matrix){
 const local=reason==='text'||reason==='image';
 const snapshot=makeRasterSnapshot(ctx,element,{world:!local});
 const scale=qualityBucket(ctx.scale*(local?maxScale(matrix):1));
 const bounds=local?localRasterBounds(element,ctx.style(element),scale):(ctx.rootClip||snapshot.rootBounds);
 const tiles=rasterTiles(bounds,scale,ctx.rasterLimits),commands=[],textures=[];
 for(let i=0;i<tiles.length;i++){
  const tile=tiles[i],resource=await ctx.rasterCache.get(snapshot,tile);textures.push(resource);
  commands.push({id:id+':raster:'+i,kind:'image',matrix:local?matrix:identity(),bounds:tile.bounds,image:resource.id});
 }
 return{commands,textures,reason};
}
