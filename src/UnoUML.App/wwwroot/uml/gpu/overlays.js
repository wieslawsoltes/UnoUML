// Retained viewport-space paint for the canvas's finite set of authoring decorations.
// DOM controls keep their geometry, pointer routing, focus and accessibility. This
// module never samples pixels, creates a GPU device, or enters the export SVG.
import {GeometryError,rectContours,fillTriangles,strokeTriangles,flattenPath,boundsOfPoints} from './geometry.js';

const ROOTS='.selection,.target,.link-guide,.marquee,.connection-port,.canvas-controls,.canvas-guides,.gantt-controls,.gpu-selections,.gpu-hover';
const SHAPES='.selection,.target,.marquee,.connection-port,.canvas-handle,.canvas-guide,.gantt-handle,.gpu-selection,.gpu-hover,.link-guide line';
const COLORS=new Map();
const rounded=value=>Math.round(value*1e5)/1e5;
const same=(a,b)=>a===b||Array.isArray(a)&&Array.isArray(b)&&a.length===b.length&&a.every((n,i)=>n===b[i]);
const positive=value=>Number.isFinite(value)&&value>0;
const px=value=>Number.parseFloat(value)||0;
const identity=()=>[1,0,0,1,0,0];

function color(value){
  value=String(value||'transparent').trim().toLowerCase();
  if(COLORS.has(value))return COLORS.get(value);
  let result;
  if(value==='transparent'||value==='none')result=[0,0,0,0];
  else if(/^#[0-9a-f]{3,8}$/.test(value)){
    let hex=value.slice(1);if(hex.length===3||hex.length===4)hex=[...hex].map(c=>c+c).join('');
    if(hex.length!==6&&hex.length!==8)throw new GeometryError('overlay-color');
    result=[0,2,4].map(i=>parseInt(hex.slice(i,i+2),16)/255);result.push(hex.length===8?parseInt(hex.slice(6),16)/255:1);
  }else{
    const rgb=/^rgba?\((.*)\)$/.exec(value);
    if(!rgb)throw new GeometryError('overlay-color');
    const parts=rgb[1].replace(/[,/]/g,' ').split(/\s+/).filter(Boolean);
    if(parts.length<3||parts.length>4)throw new GeometryError('overlay-color');
    result=parts.slice(0,3).map(v=>parseFloat(v)/(v.endsWith('%')?100:255));
    result.push(parts[3]===undefined?1:parseFloat(parts[3])/(parts[3].endsWith('%')?100:1));
  }
  if(!result.every(Number.isFinite))throw new GeometryError('overlay-color');
  result=result.map(v=>Math.max(0,Math.min(1,v)));
  if(COLORS.size>=256)COLORS.delete(COLORS.keys().next().value);
  COLORS.set(value,result);return result;
}
function splitShadows(value){
  if(!value||value==='none')return[];
  const parts=[];let depth=0,start=0;
  for(let i=0;i<value.length;i++){if(value[i]==='(')depth++;else if(value[i]===')')depth--;else if(value[i]===','&&!depth){parts.push(value.slice(start,i));start=i+1;}}
  parts.push(value.slice(start));
  return parts.map(part=>{
    if(/\binset\b/.test(part))return null;
    const match=part.match(/rgba?\([^)]*\)|#[0-9a-f]{3,8}\b/i);
    if(!match)return null;
    const lengths=part.replace(match[0],'').trim().split(/\s+/).map(px);
    return lengths.length>=2?{color:color(match[0]),x:lengths[0],y:lengths[1],blur:Math.max(0,lengths[2]||0),spread:lengths[3]||0}:null;
  }).filter(Boolean);
}
function exposed(element,surface,styles){
  if(!element.isConnected)return false;
  for(let node=element;node&&node!==surface.parentElement;node=node.parentElement){
    const style=styles(node);
    if(node.hasAttribute('hidden')||style.display==='none'||style.visibility==='hidden'||style.visibility==='collapse')return false;
    if(node===surface)break;
  }
  return true;
}
function localBox(element,style,surfaceRect){
  const rect=element.getBoundingClientRect();
  if(!positive(rect.width)||!positive(rect.height))return null;
  let width=px(style.width),height=px(style.height);
  if(style.boxSizing!=='border-box'){
    width+=px(style.paddingLeft)+px(style.paddingRight)+px(style.borderLeftWidth)+px(style.borderRightWidth);
    height+=px(style.paddingTop)+px(style.paddingBottom)+px(style.borderTopWidth)+px(style.borderBottomWidth);
  }
  if(!positive(width)||!positive(height)){width=rect.width;height=rect.height;}
  const transform=style.transform&&style.transform!=='none'?new DOMMatrixReadOnly(style.transform):null;
  if(transform&&!transform.is2D)throw new GeometryError('overlay-perspective');
  let a=transform?.a??1,b=transform?.b??0,c=transform?.c??0,d=transform?.d??1;
  const predictedWidth=Math.abs(a)*width+Math.abs(c)*height,predictedHeight=Math.abs(b)*width+Math.abs(d)*height;
  if(!positive(predictedWidth)||!positive(predictedHeight))return null;
  const sx=rect.width/predictedWidth,sy=rect.height/predictedHeight;
  a*=sx;c*=sx;b*=sy;d*=sy;
  const matrix=[a,b,c,d,rect.left-surfaceRect.left+rect.width/2-(a*width+c*height)/2,
    rect.top-surfaceRect.top+rect.height/2-(b*width+d*height)/2].map(rounded);
  width=rounded(width);height=rounded(height);
  if(!matrix.every(Number.isFinite)||![width,height].every(Number.isFinite))throw new GeometryError('overlay-bounds');
  return{width,height,matrix};
}
function radius(value,width,height){
  const part=String(value||'0').split(/\s+/)[0];
  return Math.min(width/2,height/2,Math.max(0,parseFloat(part)*(part.endsWith('%')?Math.min(width,height)/100:1)||0));
}
function stacking(element,surface,styles){
  const result=[];
  for(let node=element;node&&node!==surface;node=node.parentElement){
    const z=styles(node).zIndex;if(z!=='auto'&&Number.isFinite(Number(z)))result.unshift(Number(z));
  }
  return result.length?result:[0];
}
function compareStack(a,b){
  const length=Math.max(a.stack.length,b.stack.length);
  for(let i=0;i<length;i++){const delta=(a.stack[i]||0)-(b.stack[i]||0);if(delta)return delta;}
  return a.index-b.index;
}

export class CanvasOverlayScene{
  constructor(view,onChange=()=>{}){
    this.view=view;this.onChange=onChange;this.disposed=false;this.geometry=new Map();this.materials=new Map();this.previous=new Map();this.elements=[];
    this.observer=new MutationObserver(records=>this.changed(records));
    this.observer.observe(view.surface,{attributes:true,childList:true,subtree:true,attributeFilter:['style','class','hidden','data-tool','data-side','data-wire','data-canvas-handle','data-gantt-handle','disabled']});
    this.focus=()=>this.invalidate();
    view.surface.addEventListener('focusin',this.focus);view.surface.addEventListener('focusout',this.focus);
    this.media=globalThis.matchMedia?.('(pointer: coarse)');
    this.media?.addEventListener('change',this.focus);
    this.reindex();
  }
  relevant(record){
    const target=record.target;
    if(target===this.view.surface)return record.type==='childList'||record.attributeName==='data-tool';
    if(target===this.view.paper||this.view.paper?.contains(target))return false;
    for(let node=target;node&&node!==this.view.surface;node=node.parentElement)if(node.matches?.(ROOTS))return true;
    return false;
  }
  changed(records){
    if(this.disposed||!records.some(record=>this.relevant(record)))return;
    this.reindex();this.invalidate();
  }
  reindex(){
    const elements=[];
    for(const root of this.view.surface.children){
      if(!root.matches(ROOTS))continue;
      if(root.matches(SHAPES))elements.push(root);
      elements.push(...root.querySelectorAll(SHAPES));
    }
    this.elements=[...new Set(elements)];
  }
  invalidate(){if(!this.disposed)this.onChange();}
  mesh(key,create){
    if(this.geometry.has(key)){const item=this.geometry.get(key);this.geometry.delete(key);this.geometry.set(key,item);return item;}
    const vertices=create();
    const bounds=boundsOfPoints(Array.from({length:vertices.length/2},(_,i)=>[vertices[2*i],vertices[2*i+1]]));
    const item={vertices,bounds};this.geometry.set(key,item);
    while(this.geometry.size>2048)this.geometry.delete(this.geometry.keys().next().value);
    return item;
  }
  material(key,create){
    if(this.materials.has(key)){const item=this.materials.get(key);this.materials.delete(key);this.materials.set(key,item);return item;}
    const item=create();this.materials.set(key,item);
    while(this.materials.size>512)this.materials.delete(this.materials.keys().next().value);
    return item;
  }
  snapshot(){
    if(this.disposed)return[];
    const mutations=this.observer.takeRecords();
    if(mutations.some(record=>this.relevant(record)))this.reindex();
    const {surface}=this.view,rect=surface.getBoundingClientRect();
    if(!surface.isConnected||!positive(rect.width)||!positive(rect.height))return[];
    const styleCache=new Map(),styles=element=>{
      if(!styleCache.has(element))styleCache.set(element,getComputedStyle(element));
      return styleCache.get(element);
    };
    const ratio=Math.max(1,globalThis.devicePixelRatio||1),tolerance=.2/ratio,commands=[],next=new Map(),counts=new Map();
    const add=(id,kind,mesh,paintColor,matrix,alpha=1,rule='evenodd')=>{
      if(!mesh.vertices.length||Array.isArray(paintColor)&&paintColor[3]*alpha<=0)return;
      if(commands.length>=8192)throw new GeometryError('overlay-command-budget');
      const paint=Array.isArray(paintColor)?this.material('solid|'+paintColor+'|'+alpha,
        ()=>({kind:'solid',color:paintColor,opacity:alpha})):paintColor;
      const old=this.previous.get(id);
      const command=old&&old.kind===kind&&old.vertices===mesh.vertices&&same(old.matrix,matrix)&&old.paint===paint?old:
        {id,kind,space:'viewport',vertices:mesh.vertices,bounds:mesh.bounds,matrix,rule,paint};
      commands.push(command);next.set(id,command);
    };
    const fill=(id,shapeKey,contours,paintColor,matrix,alpha=1)=>add(id,'fill',
      this.mesh('f|'+tolerance+'|'+shapeKey,()=>fillTriangles(contours,'evenodd')),paintColor,matrix,alpha);
    const stroke=(id,shapeKey,contours,width,paintColor,matrix,options={},alpha=1)=>add(id,'mesh',
      this.mesh('s|'+tolerance+'|'+shapeKey+'|'+width+'|'+JSON.stringify(options),()=>strokeTriangles(contours,width,{tolerance,...options})),paintColor,matrix,alpha);
    const ring=(id,w,h,r,inside,outside,paintColor,matrix,alpha=1)=>{
      if(!(outside>inside))return;
      const outer=rectContours(-outside,-outside,w+outside*2,h+outside*2,Math.max(0,r+outside),undefined,tolerance);
      const inner=rectContours(-inside,-inside,w+inside*2,h+inside*2,Math.max(0,r+inside),undefined,tolerance);
      fill(id,['ring',w,h,r,inside,outside].join('|'),[...outer,...inner],paintColor,matrix,alpha);
    };
    const shapes=this.elements.map((element,index)=>({element,index,stack:stacking(element,surface,styles)})).sort(compareStack);
    for(const {element} of shapes){
      if(!exposed(element,surface,styles))continue;
      const style=styles(element);
      let name=element.dataset.canvasHandle?'handle:'+element.dataset.canvasHandle:
        element.dataset.ganttHandle?'gantt:'+element.dataset.ganttHandle:
        element.dataset.wire?'port:'+element.dataset.wire:
        element.matches('.link-guide line')?'link-guide':
        element.matches('.gpu-selection')?'additional':
        element.matches('.gpu-hover')?'hover':
        element.matches('.canvas-guide')?'snap-guide':
        element.matches('.selection')?'selection':
        element.matches('.target')?'target':'marquee';
      const ordinal=counts.get(name)||0;counts.set(name,ordinal+1);
      const id='overlay:'+name+(ordinal?':'+ordinal:'');
      if(element.matches('.link-guide line')){
        const m=element.getScreenCTM();if(!m)continue;
        const point=(x,y)=>[m.a*x+m.c*y+m.e-rect.left,m.b*x+m.d*y+m.f-rect.top];
        const a=point(element.x1.baseVal.value,element.y1.baseVal.value),b=point(element.x2.baseVal.value,element.y2.baseVal.value);
        const length=Math.hypot(b[0]-a[0],b[1]-a[1]);if(length<1e-6)continue;
        const matrix=[(b[0]-a[0])/length,(b[1]-a[1])/length,-(b[1]-a[1])/length,(b[0]-a[0])/length,a[0],a[1]].map(rounded);
        const dash=style.strokeDasharray==='none'?undefined:style.strokeDasharray.split(/[,\s]+/).filter(Boolean).map(px);
        stroke(id,length,[{points:[[0,0],[rounded(length),0]],closed:false}],px(style.strokeWidth)||2,color(style.stroke),matrix,{dash,cap:'butt'});
        continue;
      }
      const box=localBox(element,style,rect);if(!box)continue;
      const {width:w,height:h,matrix}=box,r=radius(style.borderTopLeftRadius,w,h);
      const opacity=element.matches('.canvas-guide') ? .9 : 1;
      const shadows=splitShadows(style.boxShadow).reverse();
      shadows.forEach((shadow,index)=>{
        const shift=matrix.slice();shift[4]+=matrix[0]*shadow.x+matrix[2]*shadow.y;shift[5]+=matrix[1]*shadow.x+matrix[3]*shadow.y;
        if(!shadow.blur){if(shadow.spread>0)ring(id+':shadow:'+index,w,h,r,0,shadow.spread,shadow.color,shift,opacity);return;}
        // Four linear sides and four radial corners keep the soft edge in
        // shader paint, with bounded draw count and no intermediate bitmap.
        const extent=Math.max(0,shadow.spread)+shadow.blur*1.5,outer=r+extent;
        const key=['shadow',w,h,r,shadow.blur,shadow.spread,shadow.color,opacity].join('|');
        const erfc=x=>{
          const z=Math.abs(x),t=1/(1+.3275911*z);
          const tail=(((((1.061405429*t-1.453152027)*t)+1.421413741)*t-.284496736)*t+.254829592)*t*Math.exp(-z*z);
          return x<0?2-tail:tail;
        };
        const stops=corner=>Array.from({length:25},(_,i)=>{
          const distance=extent*i/24,alpha=i===24?0:.5*erfc((distance-shadow.spread)*Math.SQRT2/shadow.blur);
          return{offset:corner?(r+distance)/outer:i/24,color:[...shadow.color.slice(0,3),shadow.color[3]*alpha]};
        });
        const sides=[
          {name:'top',rect:[r,-extent,w-r*2,extent],start:[0,0],end:[0,-extent]},
          {name:'right',rect:[w,r,extent,h-r*2],start:[w,0],end:[w+extent,0]},
          {name:'bottom',rect:[r,h,w-r*2,extent],start:[0,h],end:[0,h+extent]},
          {name:'left',rect:[-extent,r,extent,h-r*2],start:[0,0],end:[-extent,0]}
        ];
        for(const side of sides){
          const paint=this.material(key+'|'+side.name,()=>({kind:'linear',opacity,transform:identity(),
            start:side.start,end:side.end,spread:'pad',stops:stops(false)}));
          fill(id+':shadow:'+index+':'+side.name,['shadow-side',...side.rect].join('|'),rectContours(...side.rect),paint,shift);
        }
        const corners=[[r,r,Math.PI],[w-r,r,Math.PI*1.5],[w-r,h-r,0],[r,h-r,Math.PI*.5]];
        corners.forEach(([cx,cy,start],corner)=>{
          const end=start+Math.PI*.5,point=(radius,angle)=>[cx+radius*Math.cos(angle),cy+radius*Math.sin(angle)].map(rounded).join(' ');
          const path='M'+point(outer,start)+'A'+outer+' '+outer+' 0 0 1 '+point(outer,end)+'L'+point(r,end)+
            (r?'A'+r+' '+r+' 0 0 0 '+point(r,start):'')+'Z';
          const paint=this.material(key+'|corner:'+corner,()=>({kind:'radial',opacity,transform:identity(),
            center:[cx,cy],focal:[cx,cy],radius:outer,focalRadius:0,spread:'pad',stops:stops(true)}));
          fill(id+':shadow:'+index+':corner:'+corner,['shadow-corner',cx,cy,r,outer,corner].join('|'),
            flattenPath(path,tolerance),paint,shift);
        });
      });
      const background=color(style.backgroundColor);
      if(background[3])fill(id+':background',['rect',w,h,r].join('|'),rectContours(0,0,w,h,r,undefined,tolerance),background,matrix,opacity);
      const sides=['Top','Right','Bottom','Left'].map(side=>({side,width:px(style['border'+side+'Width']),style:style['border'+side+'Style'],color:color(style['border'+side+'Color'])}))
        .filter(side=>side.width>0&&!['none','hidden'].includes(side.style)&&side.color[3]>0);
      if(sides.length){
        const common=sides.reduce((a,b)=>a.width<=b.width?a:b);
        const bw=common.width,contours=rectContours(bw/2,bw/2,Math.max(0,w-bw),Math.max(0,h-bw),Math.max(0,r-bw/2),undefined,tolerance);
        const dash=common.style==='dashed'?[bw*3,bw*3]:common.style==='dotted'?[bw,bw]:undefined;
        stroke(id+':border',['border',w,h,r].join('|'),contours,bw,common.color,matrix,{dash,join:'round'},opacity);
        for(const side of sides){
          if(side.width===bw&&side.style===common.style&&same(side.color,common.color))continue;
          const q=side.width/2,points=side.side==='Top'?[[r,q],[w-r,q]]:side.side==='Right'?[[w-q,r],[w-q,h-r]]:
            side.side==='Bottom'?[[w-r,h-q],[r,h-q]]:[[q,h-r],[q,r]];
          stroke(id+':border:'+side.side,['side',w,h,r,side.side].join('|'),[{points,closed:false}],side.width,side.color,matrix,{cap:'butt'},opacity);
        }
      }
      if(element.matches('.connection-port')){
        const size=Math.min(w,h)*.52,cx=w/2,cy=h/2;
        let contours;
        if(element.dataset.wire==='retargetFrom')contours=flattenPath('M .82 -.7 C .35 -1 -.6 -1 -.68 -.35 C -.76 .15 .7 -.1 .68 .5 C .64 1 -.45 1 -.86 .65',.04);
        else if(element.dataset.wire==='retargetTo')contours=[{points:[[-.8,-.8],[.8,-.8]],closed:false},{points:[[0,-.8],[0,.85]],closed:false}];
        else contours=[{points:[[-1,0],[1,0]],closed:false},{points:[[.25,-.7],[1,0],[.25,.7]],closed:false}];
        const glyph=contours.map(c=>({...c,points:c.points.map(p=>[cx+p[0]*size*.5,cy+p[1]*size*.5])}));
        stroke(id+':symbol',['symbol',element.dataset.wire,w,h].join('|'),glyph,element.dataset.wire==='connect'?1.6:1.5,color(style.color),matrix,{cap:'round',join:'round'},opacity);
      }
      if(element.matches(':focus-visible')&&!['none','hidden'].includes(style.outlineStyle)&&px(style.outlineWidth)>0){
        const ow=px(style.outlineWidth),offset=px(style.outlineOffset);
        ring(id+':focus',w,h,r,offset,offset+ow,color(style.outlineColor),matrix);
      }
    }
    // The surface itself cannot be made transparent: it owns the live input
    // subtree. Its focus ring has an explicit paint token shared with CSS.
    if(surface.matches(':focus-visible')){
      const c=color(styles(surface).getPropertyValue('--canvas-focus-color').trim()||'#7376e5');
      ring('overlay:canvas-focus',rect.width,rect.height,0,-2,0,c,identity());
    }
    this.previous=next;return commands;
  }
  dispose(){
    if(this.disposed)return;this.disposed=true;this.observer.disconnect();
    this.view.surface.removeEventListener('focusin',this.focus);this.view.surface.removeEventListener('focusout',this.focus);
    this.media?.removeEventListener('change',this.focus);this.geometry.clear();this.materials.clear();this.previous.clear();this.elements=[];this.onChange=()=>{};
  }
}
