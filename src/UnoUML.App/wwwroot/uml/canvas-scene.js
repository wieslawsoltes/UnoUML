import {bounds, center, clone, pointOnRoute, routePath, visibleOwner} from './canvas-geometry.js';

const ns='http://www.w3.org/2000/svg';
const matrixText=m=>`matrix(${m.a} ${m.b} ${m.c} ${m.d} ${m.e} ${m.f})`;
const point=(p,m)=>new DOMPoint(p.x,p.y).matrixTransform(m);
const distance=(a,b)=>Math.hypot(a.x-b.x,a.y-b.y);
const defaults={algorithm:'layered',direction:'down',nodeSpacing:32,layerSpacing:64,routingPadding:12,
  gridColumns:0,iterations:200,seed:1,gridSize:16,snapToGrid:true,snapToObjects:true,automatic:false};

// Compatibility edges refer to SVG element IDs; native edges refer to semantic
// names. Keep their lookup spaces separate: a user may also name a node ent0001.
export class CanvasIds {
  constructor(reservedEdges=[]) {
    this.names=new Map();this.elements=new Map();this.counts=new Map();
    this.edges=new Set();this.reserved=new Set(reservedEdges.filter(Boolean));
  }
  addNode(id,elementId,aliases=[]) {
    const add=(map,key)=>{
      if(key)map.set(key,map.has(key)&&map.get(key)!==id?null:id);
    };
    add(this.names,id);for(const alias of aliases)add(this.names,alias);add(this.elements,elementId);
  }
  resolve(reference,elementFirst=false) {
    for(const map of elementFirst?[this.elements,this.names]:[this.names,this.elements])
      if(map.has(reference))return map.get(reference); // Ambiguous names never fall through to a different node.
    return null;
  }
  edgeId(source,target,preferred='') {
    const key=JSON.stringify([source,target]);let ordinal=this.counts.get(key)||0;
    this.counts.set(key,ordinal+1);
    if(preferred&&!this.edges.has(preferred)){this.edges.add(preferred);return preferred;}
    let id;
    do{id=source+'->'+target+':'+ordinal++;}while(this.edges.has(id)||this.reserved.has(id));
    this.counts.set(key,ordinal);this.edges.add(id);return id;
  }
}

// The renderer owns semantic SVG. This adapter projects an independent geometry
// snapshot onto it, retaining its shapes, markers, hyperlinks, labels and hit data.
export class CanvasScene {
  constructor(view) {
    this.view=view; this.svg=view.svg; this.nodes=new Map(); this.edges=new Map(); this.decorations=[];
    this.originalBox=this.svg.getAttribute('viewBox');
    this.base={version:1,nodes:[],edges:[],settings:{...defaults}};
    this.capture(); this.state=clone(this.base);
  }
  matrix(el) {
    // Chromium may expose the legacy SVGMatrix interface from getScreenCTM;
    // its multiply() rejects a modern DOMMatrix. Normalize at the boundary.
    const modern=m=>new DOMMatrix([m.a,m.b,m.c,m.d,m.e,m.f]);
    return modern(this.svg.getScreenCTM()).inverse().multiply(modern(el.getScreenCTM()));
  }
  box(el) {
    const b=el.getBBox(),m=this.matrix(el),p=[point({x:b.x,y:b.y},m),point({x:b.x+b.width,y:b.y},m),
      point({x:b.x,y:b.y+b.height},m),point({x:b.x+b.width,y:b.y+b.height},m)];
    const x=Math.min(...p.map(v=>v.x)),y=Math.min(...p.map(v=>v.y));
    return {x,y,width:Math.max(...p.map(v=>v.x))-x,height:Math.max(...p.map(v=>v.y))-y};
  }
  capture() {
    const candidates=[...this.svg.querySelectorAll('g.entity,g.cluster,g[data-canvas-id]')]
      .filter(el=>!el.matches('.link,.message,[data-dot-kind="edge"]') && !el.closest('.participant-head,.participant-tail'));
    const edgeElements=[...this.svg.querySelectorAll('g.link,g[data-dot-kind="edge"]')];
    const edgeName=el=>el.getAttribute('data-dot-id') || el.getAttribute('data-canvas-id') || '';
    const byElement=new Map(),ids=new CanvasIds(edgeElements.map(edgeName));
    for(const el of candidates) {
      const id=el.getAttribute('data-canvas-id') || el.getAttribute('data-dot-id') ||
        el.getAttribute('data-c4-alias') || el.getAttribute('data-qualified-name');
      if(!id || this.nodes.has(id)) continue;
      let b; try {b=this.box(el);} catch {continue;}
      if(b.width<1 || b.height<1) continue;
      const base={id,...b,locked:false,pinned:false,rotation:0,zIndex:0,collapsed:false};
      const texts=[...el.querySelectorAll('text')].filter(text=>text.closest('g.entity,g.cluster,g[data-canvas-id]')===el)
        .map(text=>({el:text,transform:text.getAttribute('transform') || '',x:Number.parseFloat(text.getAttribute('x')) || 0,y:Number.parseFloat(text.getAttribute('y')) || 0}));
      this.nodes.set(id,{el,base,matrix:this.matrix(el),texts,display:el.style.display});
      byElement.set(el,id); this.base.nodes.push(base); el.dataset.canvasNode=id;
      ids.addNode(id,el.getAttribute('id'),['data-qualified-name','data-c4-alias','data-dot-id','data-entity'].map(name=>el.getAttribute(name)));
      // Keep strokes stable while resizing. Text receives an inverse scale below.
      for(const shape of el.querySelectorAll('path,rect,circle,ellipse,polygon,polyline,line')) shape.setAttribute('vector-effect','non-scaling-stroke');
    }
    for(const record of this.nodes.values()) {
      const explicit=record.el.getAttribute('data-canvas-parent'),parentElement=record.el.parentElement?.closest('[data-canvas-node]');
      const qualified=record.el.getAttribute('data-qualified-name')||'',split=qualified.lastIndexOf('.');
      const c4Parent=record.el.hasAttribute('data-c4-alias')&&split>0?ids.resolve(qualified.slice(0,split)):null;
      const parent=explicit || byElement.get(parentElement) || c4Parent;
      if(parent && parent!==record.base.id && this.nodes.has(parent)) record.base.parent=parent;
    }
    const elementFirst=this.view.pristine?.backend!=='managed';
    for(const el of edgeElements) {
      const dot=el.hasAttribute('data-dot-from-id');
      let source=dot ? 'node:'+el.getAttribute('data-dot-from-id') : el.getAttribute('data-entity-1');
      let target=dot ? 'node:'+el.getAttribute('data-dot-to-id') : el.getAttribute('data-entity-2');
      source=ids.resolve(source,!dot&&elementFirst);target=ids.resolve(target,!dot&&elementFirst);
      if(!this.nodes.has(source) || !this.nodes.has(target)) continue;
      const id=ids.edgeId(source,target,edgeName(el));
      const geometry=[...el.querySelectorAll('path,polyline,line')].map(shape=>{
        try { return {el:shape,length:shape.getTotalLength()}; } catch {return {el:shape,length:0};}
      }).filter(r=>r.length>0 && !/[zZ]\s*$/.test(r.el.getAttribute('d') || '')).sort((a,b)=>b.length-a.length);
      if(!geometry.length) continue;
      const longest=geometry[0],initial=this.points(longest.el),a=center(this.nodes.get(source).base),b=center(this.nodes.get(target).base);
      if(initial.length<2) continue;
      const reversed=distance(initial[0],a)+distance(initial.at(-1),b)>distance(initial[0],b)+distance(initial.at(-1),a);
      if(reversed) initial.reverse();
      const routes=geometry.filter(r=>r.length>=longest.length*.94);
      const paths=routes.map(({el:old})=>{
        const path=document.createElementNS(ns,'path');
        for(const attribute of old.attributes) if(!['points','x1','x2','y1','y2'].includes(attribute.name)) path.setAttribute(attribute.name,attribute.value);
        // The standalone viewport can render before a native layout snapshot
        // arrives. Retain a complete original route throughout that interval.
        if(old.localName==='line') path.setAttribute('d',`M${old.x1.baseVal.value},${old.y1.baseVal.value}L${old.x2.baseVal.value},${old.y2.baseVal.value}`);
        else if(old.localName==='polyline') path.setAttribute('d',routePath([...old.points].map(p=>({x:p.x,y:p.y})),'polyline'));
        old.replaceWith(path); path.setAttribute('vector-effect','non-scaling-stroke');
        return path;
      });
      const routeSet=new Set(paths),decorations=[];
      for(const child of el.children) {
        if(routeSet.has(child) || ['title','desc','defs'].includes(child.localName)) continue;
        let cb; try { cb=this.box(child); } catch {continue;}
        const c=center(cb),near=Math.min(distance(c,initial[0]),distance(c,initial.at(-1)));
        const isLabel=child.localName==='text' || child.localName==='rect' || child.querySelector('text');
        const end=distance(c,initial[0])<=distance(c,initial.at(-1)) ? 0 : 1;
        const fraction=isLabel ? (near<30 && distance(c,pointOnRoute(initial))>near*1.8 ? end : .5) : end;
        decorations.push({el:child,matrix:this.matrix(child),fraction,arrow:!isLabel});
      }
      const base={id,source,target,waypoints:initial,routing:'orthogonal',manual:false,locked:false};
      this.base.edges.push(base); this.edges.set(id,{el,base,paths,decorations,reversed,display:el.style.display});
      el.dataset.canvasEdge=id;
    }
    for(const el of this.svg.querySelectorAll('[data-canvas-decoration-for]')) {
      const owner=el.getAttribute('data-canvas-decoration-for');
      if(this.nodes.has(owner))this.decorations.push({el,owner,matrix:this.matrix(el),display:el.style.display});
    }
  }
  points(el) {
    const m=this.matrix(el);
    if(el.localName==='polyline') return [...el.points].map(p=>point(p,m)).map(p=>({x:p.x,y:p.y}));
    if(el.localName==='line') return [point({x:el.x1.baseVal.value,y:el.y1.baseVal.value},m),point({x:el.x2.baseVal.value,y:el.y2.baseVal.value},m)].map(p=>({x:p.x,y:p.y}));
    const length=el.getTotalLength(),count=Math.min(64,Math.max(2,Math.ceil(length/12))),result=[];
    for(let i=0;i<=count;i++) {const p=point(el.getPointAtLength(length*i/count),m);result.push({x:p.x,y:p.y});}
    for(let i=result.length-2;i>0;i--) {
      const a=result[i-1],b=result[i],c=result[i+1];
      if(Math.abs((b.x-a.x)*(c.y-b.y)-(b.y-a.y)*(c.x-b.x))<.001) result.splice(i,1);
    }
    return result;
  }
  merge(saved) {
    const nodes=new Map((saved?.nodes || []).map(n=>[n.id,n])),edges=new Map((saved?.edges || []).map(e=>[e.id,e]));
    const added=[];
    const state={version:1,settings:{...defaults,...saved?.settings},nodes:this.base.nodes.map(base=>{
      const stored=nodes.get(base.id); if(!stored) added.push(base.id);
      // Source ownership wins after a semantic reparent operation.
      return {...base,...stored,parent:base.parent};
    }),edges:this.base.edges.map(base=>{
      const stored=edges.get(base.id);
      return stored && stored.source===base.source && stored.target===base.target ? {...base,...stored} : clone(base);
    })};
    return {state,added};
  }
  apply(state,resizeViewport=false,projection=null) {
    this.state=state; const byId=new Map(state.nodes.map(n=>[n.id,n])),changes=new Map();
    const depth=el=>{let count=0;for(let p=el.parentElement;p&&p!==this.svg;p=p.parentElement)count++;return count;};
    // Canonical metadata orders IDs; projection must establish parent transforms first.
    const ordered=[...state.nodes].sort((a,b)=>depth(this.nodes.get(a.id).el)-depth(this.nodes.get(b.id).el));
    for(const n of ordered) {
      const r=this.nodes.get(n.id); if(!r) continue;
      const hidden=visibleOwner(n,byId).id!==n.id; r.el.style.display=hidden?'none':r.display;
      if(hidden) continue;
      const sx=n.width/r.base.width,sy=n.height/r.base.height;
      const change=new DOMMatrix().translate(n.x+n.width/2,n.y+n.height/2).rotate(n.rotation || 0)
        .scale(sx,sy).translate(-r.base.x-r.base.width/2,-r.base.y-r.base.height/2);
      changes.set(n.id,change);
      const parent=this.matrix(r.el.parentElement);
      r.el.setAttribute('transform',matrixText(parent.inverse().multiply(change).multiply(r.matrix)));
      r.el.classList.toggle('canvas-locked',!!n.locked); r.el.classList.toggle('canvas-pinned',!!n.pinned);
      r.el.classList.toggle('canvas-collapsed',!!n.collapsed);
      for(const t of r.texts) t.el.setAttribute('transform',`${t.transform} translate(${t.x} ${t.y}) scale(${1/sx} ${1/sy}) translate(${-t.x} ${-t.y})`);
    }
    for(const decoration of this.decorations) {
      const change=changes.get(decoration.owner);decoration.el.style.display=change?decoration.display:'none';
      if(change)decoration.el.setAttribute('transform',matrixText(this.matrix(decoration.el.parentElement).inverse().multiply(change).multiply(decoration.matrix)));
    }
    // Only reorder siblings that represent nodes, never defs or renderer ornaments.
    const parents=new Map();
    for(const n of state.nodes) { const r=this.nodes.get(n.id); if(!r) continue;
      const list=parents.get(r.el.parentElement)||[];list.push({n,r});parents.set(r.el.parentElement,list); }
    for(const [parent,list] of parents) {
      if(!list.some(v=>v.n.zIndex)) continue;
      const anchor=document.createComment('canvas-z'); parent.insertBefore(anchor,list[0].r.el);
      list.sort((a,b)=>a.n.zIndex-b.n.zIndex || this.base.nodes.indexOf(a.r.base)-this.base.nodes.indexOf(b.r.base));
      for(const {r} of list) parent.insertBefore(r.el,anchor);anchor.remove();
    }
    for(const edge of state.edges) {
      const r=this.edges.get(edge.id);if(!r)continue;
      const source=byId.get(edge.source),target=byId.get(edge.target);
      const a=source && visibleOwner(source,byId),b=target && visibleOwner(target,byId);
      const hidden=!a || !b || a.id===b.id && source.id!==target.id;
      r.el.style.display=hidden?'none':r.display;if(hidden)continue;
      const projected=projection?.get(edge.id);
      const points=projected?.waypoints?.length>=2 ? projected.waypoints : edge.waypoints?.length>=2 ? edge.waypoints : r.base.waypoints;
      const draw=r.reversed?[...points].reverse():points;
      for(const path of r.paths) {
        path.setAttribute('d',routePath(draw,edge.routing));
        path.setAttribute('transform',matrixText(this.matrix(path.parentElement).inverse()));
      }
      for(const d of r.decorations) {
        const old=pointOnRoute(r.base.waypoints,d.fraction),next=pointOnRoute(points,d.fraction);
        let rotation=0;
        if(d.arrow && points.length>1) {
          const index=d.fraction===0?0:points.length-1,adj=d.fraction===0?1:points.length-2;
          const oi=d.fraction===0?0:r.base.waypoints.length-1,oj=d.fraction===0?1:r.base.waypoints.length-2;
          rotation=(Math.atan2(points[adj].y-points[index].y,points[adj].x-points[index].x)-
            Math.atan2(r.base.waypoints[oj].y-r.base.waypoints[oi].y,r.base.waypoints[oj].x-r.base.waypoints[oi].x))*180/Math.PI;
        }
        const offset=d.arrow?{x:0,y:0}:edge.labelOffset||{x:0,y:0};
        const change=new DOMMatrix().translate(next.x+offset.x,next.y+offset.y).rotate(rotation).translate(-old.x,-old.y);
        d.el.setAttribute('transform',matrixText(this.matrix(d.el.parentElement).inverse().multiply(change).multiply(d.matrix)));
      }
    }
    if(resizeViewport) this.expandBounds();
  }
  expandBounds() {
    const old=this.svg.viewBox.baseVal,previous={x:old.x,y:old.y};
    // The root BBox includes untouched titles, legends and annotations too.
    let b;try {b=this.svg.getBBox();}catch{return;}
    const x=Math.floor(b.x-24),y=Math.floor(b.y-24),width=Math.max(1,Math.ceil(b.width+48)),height=Math.max(1,Math.ceil(b.height+48));
    this.svg.setAttribute('viewBox',`${x} ${y} ${width} ${height}`);
    this.svg.setAttribute('width',width);this.svg.setAttribute('height',height);
    this.view.x+=(x-previous.x)*this.view.z;this.view.y+=(y-previous.y)*this.view.z;
  }
  nodeFor(el) {const owner=el?.closest('[data-canvas-node]');return owner && this.state.nodes.find(n=>n.id===owner.dataset.canvasNode);}
  edgeFor(el) {const owner=el?.closest('[data-canvas-edge]');return owner && this.state.edges.find(n=>n.id===owner.dataset.canvasEdge);}
  world(x,y) {return point({x,y},this.svg.getScreenCTM().inverse());}
  screen(p) {const value=point(p,this.svg.getScreenCTM()),r=this.view.rect();return {x:value.x-r.left,y:value.y-r.top};}
}
