import {GeometryError,identity,inverse,multiply,parseTransform} from './geometry.js';
const clamp=(n,min=0,max=1)=>Math.max(min,Math.min(max,n));
let colorContext;
export function localReference(value){
 const m=/^url\(\s*["']?([^"')]+)["']?\s*\)$/i.exec(String(value??'').trim());
 if(!m)return null;const index=m[1].lastIndexOf('#');return index<0?null:m[1].slice(index+1);
}
export function parseColor(value,element=null){
 let text=String(value??'').trim().toLowerCase();
 if(text==='none')return null;if(text==='transparent')return[0,0,0,0];
 if(text==='currentcolor'){if(!element)throw new GeometryError('unresolved-current-color');text=getComputedStyle(element).color;}
 const h=/^#([0-9a-f]{3,8})$/i.exec(text);
 if(h){let v=h[1];if(v.length===3||v.length===4)v=[...v].map(c=>c+c).join('');if(v.length!==6&&v.length!==8)throw new GeometryError('invalid-color');
  return[parseInt(v.slice(0,2),16)/255,parseInt(v.slice(2,4),16)/255,parseInt(v.slice(4,6),16)/255,v.length===8?parseInt(v.slice(6,8),16)/255:1];}
 const rgb=/^rgba?\(\s*([^)]+)\s*\)$/i.exec(text);
 if(rgb){
  const parts=rgb[1].trim().split(/[\s,/]+/);if(parts.length!==3&&parts.length!==4)throw new GeometryError('invalid-color');
  const values=parts.map((p,i)=>{if(!/^[+-]?(?:\d*\.?\d+)%?$/.test(p))throw new GeometryError('invalid-color');return clamp(parseFloat(p)/(p.endsWith('%')?100:i<3?255:1));});
  return[values[0],values[1],values[2],values[3]??1];
 }
 if(typeof document==='undefined'||!CSS.supports('color',text))throw new GeometryError('unsupported-color');
 colorContext??=document.createElement('canvas').getContext('2d',{willReadFrequently:true});
 colorContext.canvas.width=colorContext.canvas.height=1;colorContext.clearRect(0,0,1,1);colorContext.fillStyle=text;colorContext.fillRect(0,0,1,1);
 return Array.from(colorContext.getImageData(0,0,1,1).data,n=>n/255);
}
function number(value,percentBase=1){
 const text=String(value).trim();if(!/^[+-]?(?:\d+\.?\d*|\.\d+)(?:e[+-]?\d+)?(?:%|px)?$/i.test(text))throw new GeometryError('unsupported-gradient-length');
 const n=parseFloat(text)*(text.endsWith('%')?percentBase/100:1);if(!Number.isFinite(n)||Math.abs(n)>1e9)throw new GeometryError('invalid-gradient');return n;
}
function href(element){return element.getAttribute('href')||element.getAttributeNS('http://www.w3.org/1999/xlink','href');}
function gradientChain(ctx,first){
 const chain=[],seen=new Set();let el=first;
 while(el){if(seen.has(el)||chain.length>=16)throw new GeometryError('gradient-cycle');seen.add(el);chain.push(el);const reference=href(el);
  if(!reference)break;if(!reference.startsWith('#'))throw new GeometryError('external-gradient');el=ctx.byId.get(reference.slice(1));
  if(!el||!['linearGradient','radialGradient'].includes(el.localName))throw new GeometryError('missing-gradient');
 }return chain;
}
export function resolvePaint(ctx,element,value,objectBounds,opacity=1){
 value=String(value??'black').trim();opacity=clamp(Number(opacity));if(!Number.isFinite(opacity))throw new GeometryError('invalid-opacity');
 if(value==='none'||opacity===0)return null;
 const reference=localReference(value);
 if(!reference){const color=parseColor(value,element);return color&&color[3]>0?{kind:'solid',color,opacity}:null;}
 const first=ctx.byId.get(reference);
 if(!first||!['linearGradient','radialGradient'].includes(first.localName))throw new GeometryError(first?.localName==='pattern'?'pattern':'unsupported-paint-reference');
 const chain=gradientChain(ctx,first),attribute=(name,defaultValue)=>{for(const el of chain)if(el.hasAttribute(name))return el.getAttribute(name);return defaultValue;};
 if(chain.some(el=>ctx.style(el).colorInterpolation==='linearRGB'))throw new GeometryError('linear-rgb-gradient');
 const stopOwner=chain.find(el=>Array.from(el.children).some(c=>c.localName==='stop'));
 if(!stopOwner)return null;
 const stops=[];let last=0;
 for(const stop of Array.from(stopOwner.children).filter(el=>el.localName==='stop')){
  const style=ctx.style(stop),color=parseColor(style.stopColor||stop.getAttribute('stop-color')||'black',stop);
  const alpha=number(style.stopOpacity||stop.getAttribute('stop-opacity')||'1');if(color)color[3]*=clamp(alpha);
  const offset=clamp(number(stop.getAttribute('offset')||'0'));last=Math.max(last,offset);stops.push({offset:last,color:color||[0,0,0,0]});
 }
 if(stops.length>64)throw new GeometryError('gradient-stop-budget');
 if(stops.length===1)return{kind:'solid',color:stops[0].color,opacity};
 const units=attribute('gradientUnits','objectBoundingBox'),object=units==='objectBoundingBox';
 if(!object&&units!=='userSpaceOnUse')throw new GeometryError('unsupported-gradient-units');
 const svg=element.ownerSVGElement||ctx.svg,view=svg.viewBox?.baseVal;
 const width=object?1:(view?.width||(svg===ctx.svg?ctx.viewport?.[2]:0)||svg.width?.baseVal?.value||1),height=object?1:(view?.height||(svg===ctx.svg?ctx.viewport?.[3]:0)||svg.height?.baseVal?.value||1),diagonal=Math.hypot(width,height)/Math.SQRT2;
 let transform=parseTransform(attribute('gradientTransform',''));
 if(object){if(!(objectBounds[2]>0&&objectBounds[3]>0))throw new GeometryError('zero-gradient-bounds');transform=multiply([objectBounds[2],0,0,objectBounds[3],objectBounds[0],objectBounds[1]],transform);}
 const spread=attribute('spreadMethod','pad');if(!['pad','repeat','reflect'].includes(spread))throw new GeometryError('unsupported-gradient-spread');
 const result={kind:first.localName==='linearGradient'?'linear':'radial',transform:inverse(transform),stops,spread,opacity,color:[1,1,1,1]};
 if(result.kind==='linear'){
  result.start=[number(attribute('x1','0%'),width),number(attribute('y1','0%'),height)];result.end=[number(attribute('x2','100%'),width),number(attribute('y2','0%'),height)];
 }else{
  const cx=attribute('cx','50%'),cy=attribute('cy','50%');
  result.center=[number(cx,width),number(cy,height)];result.focal=[number(attribute('fx',cx),width),number(attribute('fy',cy),height)];
  result.radius=number(attribute('r','50%'),diagonal);result.focalRadius=number(attribute('fr','0%'),diagonal);
  if(result.radius<0||result.focalRadius<0)throw new GeometryError('invalid-radial-gradient');
  if(result.radius===0)return{kind:'solid',color:stops.at(-1).color,opacity};
  // SVG clamps focal circles that extend beyond the outer circle.
  if(result.focalRadius>=result.radius)throw new GeometryError('radial-gradient-edge');
  result.focalRadius=Math.min(result.radius,result.focalRadius);const dx=result.focal[0]-result.center[0],dy=result.focal[1]-result.center[1],distance=Math.hypot(dx,dy),allowed=Math.max(0,result.radius-result.focalRadius);
  if(distance>=allowed&&distance>0)throw new GeometryError('radial-gradient-edge');
  if(distance>allowed&&distance>0){result.focal=[result.center[0]+dx*allowed/distance,result.center[1]+dy*allowed/distance];}
 }
 return result;
}
