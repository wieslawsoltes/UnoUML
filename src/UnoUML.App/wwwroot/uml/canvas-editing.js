import {CanvasScene} from './canvas-scene.js';
import {bounds,center,clone,descendants,moveWaypoint,pointOnRoute,projectToRoute,resize,rotate,round,snapMove,visibleOwner} from './canvas-geometry.js';

// Pointer changes are previews. Only finish()/commit() crosses the source-history
// boundary, so a complete drag or a held arrow key is exactly one undo operation.
export class CanvasEditing {
  constructor(view,calculate) {
    this.view=view;this.calculate=calculate;this.revision=-1;
    this.overlay=document.createElement('div');this.overlay.className='canvas-controls';view.surface.append(this.overlay);
    this.guides=document.createElement('div');this.guides.className='canvas-guides';view.surface.append(this.guides);
    view.surface.addEventListener('keydown',e=>this.keyDown(e),true);
    view.surface.addEventListener('keyup',e=>{if(e.key.startsWith('Arrow'))this.finishNudge();},true);
    view.surface.addEventListener('blur',()=>this.finishNudge());
    view.surface.addEventListener('dblclick',e=>{
      if(e.target.closest('[data-canvas-handle]')){e.preventDefault();e.stopImmediatePropagation();return;}
      if(this.calculate && e.altKey && this.scene?.edgeFor(e.target)) {this.insertWaypoint(e);e.preventDefault();e.stopImmediatePropagation();}
    },true);
  }
  attach() {this.cancel();this.scene=new CanvasScene(this.view);this.layoutApplied=false;this.position();}
  load(json,revision) {
    this.cancel();this.revision=revision;if(!this.scene)return;
    let saved;try {saved=json?JSON.parse(json):null;}catch{return;}
    const {state,added}=this.scene.merge(saved);
    this.apply(state,!!saved||this.layoutApplied);this.layoutApplied=!!saved;this.position();this.view.position();
    if(saved?.settings.automatic && added.length && saved.nodes.length && added.length<state.nodes.length) {
      // A new source declaration is laid out around all existing fixed geometry.
      queueMicrotask(()=>{if(this.revision!==revision)return;this.action('layout',{selection:added,merge:true});});
    }
  }
  selectedNodes() {
    if(!this.scene)return [];
    return [...new Set([this.view.marked,...this.view.additional||[]].map(el=>this.scene.nodeFor(el)?.id).filter(Boolean))];
  }
  selectedEdges() {
    if(!this.scene)return [];
    return [...new Set([this.view.marked,...this.view.additional||[]].map(el=>this.scene.edgeFor(el)?.id).filter(Boolean))];
  }
  info() {return JSON.stringify({state:this.scene?.state||null,nodes:this.selectedNodes(),edges:this.selectedEdges()});}
  announce(text,error=false) {this.view.announcement.textContent=text;this.view.emit('canvasStatus',{text,error});}
  compute(action,state,extra={}) {
    if(!this.calculate)throw Error('The native canvas layout engine is unavailable.');
    const result=JSON.parse(this.calculate(JSON.stringify({action,state,...extra})));
    if(result.error)throw Error(result.error);return result;
  }
  route(state,before) {
    return before ? this.compute('geometry',state,{before}) : this.compute('route',state);
  }
  apply(state,resizeViewport=false) {
    let projection;
    if(state.nodes.some(n=>n.collapsed)) {
      const byId=new Map(state.nodes.map(n=>[n.id,n])),visible=state.nodes.filter(n=>visibleOwner(n,byId).id===n.id);
      const ids=new Set(visible.map(n=>n.id));
      const edges=state.edges.filter(e=>e.source===e.target || visibleOwner(byId.get(e.source),byId).id!==visibleOwner(byId.get(e.target),byId).id)
        .map(e=>{const source=visibleOwner(byId.get(e.source),byId).id,target=visibleOwner(byId.get(e.target),byId).id;
          return {...e,source,target,manual:e.source===source && e.target===target && e.manual,
            sourcePort:e.source===source?e.sourcePort:undefined,targetPort:e.target===target?e.targetPort:undefined};});
      const projected=this.compute('route',{...state,nodes:visible.map(n=>({...n,parent:ids.has(n.parent)?n.parent:undefined})),edges});
      projection=new Map(projected.edges.map(e=>[e.id,e]));
    }
    // Collapse changes presentation only. Original leaf routes and bends survive expansion.
    this.scene.apply(state,resizeViewport,projection);
  }
  commit(state,before,label,merge=false) {
    if(!this.scene || JSON.stringify(state)===JSON.stringify(before))return;
    try {
      state=this.compute('validate',state);
      this.apply(state,true);this.view.position();
      this.pendingCommit={accepted:null};
      this.view.emit('canvasLayout',{revision:this.revision,json:JSON.stringify(state),merge});
      const accepted=this.pendingCommit.accepted;this.pendingCommit=null;
      if(accepted===false)return;
      if(accepted!==true)throw Error('The document did not accept this canvas edit.');
      this.announce(label);
      queueMicrotask(()=>this.replaySelection());
    }catch(error){this.apply(before);this.view.position();this.announce(error.message,true);}
  }
  replaySelection() {
    const hits=[...new Set([this.view.marked,...this.view.additional||[]])].map(el=>this.view.index?.hits.get(el)).filter(Boolean);
    if(hits.length===1)this.view.emit('hit',{action:'select',...hits[0]});
    else if(hits.length)this.view.region(hits,false,true);this.position();
  }
  action(action,options={}) {
    this.finishNudge();if(!this.scene?.state.nodes.length){this.announce('Select a structural diagram with editable shapes to arrange its canvas.',true);return;}
    const before=clone(this.scene.state),ids=options.selection || this.selectedNodes(),edgeIds=this.selectedEdges();
    let state=clone(before),placedId;
    try {
      if(action==='reset') {
        this.pendingCommit={accepted:null};this.view.emit('canvasLayout',{revision:this.revision,json:'null'});
        const accepted=this.pendingCommit.accepted;this.pendingCommit=null;if(accepted!==true)return;
        this.apply(clone(this.scene.base),true);
        this.view.position();this.announce('Restored renderer layout.');return;
      }
      if(action==='selectAll') {
        const hits=[...this.scene.nodes.values()].map(r=>this.view.index.hits.get(r.el)).filter(Boolean);
        this.view.region(hits,false,true);return;
      }
      if(action==='place') {
        const previous=new Set(options.existing||[]),added=state.nodes.filter(n=>!previous.has(n.id));
        if(!added.length)return;
        placedId=added[0].id;
        const b=bounds(added),grid=state.settings.snapToGrid?state.settings.gridSize:0;
        let dx=options.x-b.x-b.width/2,dy=options.y-b.y-b.height/2;
        if(grid){dx=Math.round((b.x+dx)/grid)*grid-b.x;dy=Math.round((b.y+dy)/grid)*grid-b.y;}
        for(const n of added){n.x=round(n.x+dx);n.y=round(n.y+dy);}
        state=this.route(state,before);
      }else if(action==='layout') {
        if(!options.all&&options.selection&&ids.length===0)throw Error('Select shapes before applying a selection layout.');
        state.settings={...state.settings,...options.settings};
        state=this.compute('layout',state,{selection:options.all?null:ids.length?[...descendants(state.nodes,ids)]:null});
      }else if(['align','distribute','matchSize'].includes(action)) {
        if(ids.length<2)throw Error('Select at least two shapes for this arrangement.');
        state=this.compute(action,state,{selection:[...descendants(state.nodes,ids)],value:options.value,reference:options.reference||ids[0]});
      }else if(action==='route' || action==='routing') {
        const scope=edgeIds.length?new Set(edgeIds):new Set(state.edges.filter(e=>!ids.length||ids.includes(e.source)||ids.includes(e.target)).map(e=>e.id));
        if(action==='routing')state.edges=state.edges.map(e=>scope.has(e.id)&&!e.locked?{...e,routing:options.value}:e);
        state=this.compute('resetRoutes',state,{selection:[...scope]});
      }else if(action==='settings')state.settings={...state.settings,...options.settings};
      else if(action==='bounds') {
        if(ids.length!==1)throw Error('Select one shape to edit its exact bounds.');
        this.changeNode(state,ids[0],options.bounds);
        state=this.route(state,before);
      }else {
        const selected=new Set(ids),all=descendants(state.nodes,ids);
        if(action==='lock'||action==='unlock') {
          state.nodes=state.nodes.map(n=>all.has(n.id)?{...n,locked:action==='lock'}:n);
          state.edges=state.edges.map(e=>edgeIds.includes(e.id)?{...e,locked:action==='lock'}:e);
        }else if(action==='pin'||action==='unpin')state.nodes=state.nodes.map(n=>all.has(n.id)?{...n,pinned:action==='pin'}:n);
        else if(action==='front'||action==='back') {
          const z=action==='front'?Math.max(0,...state.nodes.map(n=>n.zIndex||0))+1:Math.min(0,...state.nodes.map(n=>n.zIndex||0))-1;
          state.nodes=state.nodes.map(n=>selected.has(n.id)&&!n.locked?{...n,zIndex:z}:n);
        }else if(action==='collapse'||action==='expand') {
          state.nodes=state.nodes.map(n=>selected.has(n.id)&&!n.locked?{...n,collapsed:action==='collapse'}:n);
          state=this.route(state,before);
        }else throw Error('Unknown canvas action.');
      }
      this.commit(state,before,action==='layout'?'Automatic layout applied.':'Canvas arrangement updated.',options.merge===true);
      if(placedId)queueMicrotask(()=>this.view.hit(this.scene.nodes.get(placedId)?.el));
    }catch(error){this.announce(error.message,true);}
  }
  pointerDown(e,el,tool) {
    if(!this.calculate || !this.scene || e.button!==0 || tool!=='select' || this.view.linkStart)return false;
    const handle=e.target.closest('[data-canvas-handle]')?.dataset.canvasHandle;
    const node=this.scene.nodeFor(el),edge=this.scene.edgeFor(el);
    if(!handle && (!node || e.ctrlKey || e.metaKey))return false;
    const ids=this.selectedNodes();
    if(e.shiftKey && node && !ids.includes(node.id) && !handle)return false;
    if(node && !ids.includes(node.id) && !handle) {this.view.hit(el,e.shiftKey?'toggle':'select');}
    const selected=this.selectedNodes(),moving=descendants(this.scene.state.nodes,selected);
    const byId=new Map(this.scene.state.nodes.map(n=>[n.id,n]));
    const lockedParent=n=>{for(let p=byId.get(n?.parent);p;p=byId.get(p.parent))if(p.locked)return true;return false;};
    const blocked=this.scene.state.nodes.some(n=>moving.has(n.id)&&(n.locked||lockedParent(n)));
    if(node?.locked||blocked){this.announce('Unlock this selection before moving it.');return false;}
    const point=this.scene.world(e.clientX,e.clientY);
    this.gesture={pointer:e.pointerId,handle:handle||'move',start:point,screen:{x:e.clientX,y:e.clientY},
      before:clone(this.scene.state),ids:selected,moving,nodeId:node?.id||selected[0],edgeId:edge?.id||this.selectedEdges()[0],hit:el,moved:false,add:e.shiftKey};
    this.waypointSelection=handle?.startsWith('point:')?Number(handle.split(':')[1]):null;
    this.waypointEdge=this.gesture.edgeId;
    e.preventDefault();return true;
  }
  pointerMove(e) {
    const g=this.gesture;if(!g||g.pointer!==e.pointerId)return false;
    g.moved ||= Math.hypot(e.clientX-g.screen.x,e.clientY-g.screen.y)>3;if(!g.moved)return true;
    const p=this.scene.world(e.clientX,e.clientY),state=clone(g.before),node=state.nodes.find(n=>n.id===g.nodeId);
    this.guides.replaceChildren();
    if(g.handle==='move') {
      const moving=state.nodes.filter(n=>g.moving.has(n.id)),other=state.nodes.filter(n=>!g.moving.has(n.id));
      const result=snapMove(bounds(moving),p.x-g.start.x,p.y-g.start.y,other,state.settings,6/this.view.z,e.altKey,e.shiftKey);
      for(const n of moving){n.x=round(n.x+result.dx);n.y=round(n.y+result.dy);}
      this.showGuides(result.guides);
    }else if(g.handle==='rotate' && node && !node.locked) {
      const origin=center(node),angle=Math.atan2(p.y-origin.y,p.x-origin.x)*180/Math.PI+90;
      this.changeNode(state,node.id,{rotation:round(e.shiftKey?Math.round(angle/15)*15:angle)});
    }else if(g.handle.startsWith('resize:') && node && !node.locked) {
      Object.assign(node,resize(node,g.handle.split(':')[1],p,{grid:state.settings.snapToGrid?state.settings.gridSize:0,free:e.altKey,aspect:e.shiftKey,centered:e.ctrlKey||e.metaKey}));
    }else {
      const edge=state.edges.find(n=>n.id===g.edgeId);if(!edge||edge.locked)return true;
      if(g.handle.startsWith('point:')) {
        const index=Number(g.handle.split(':')[1]),grid=state.settings.snapToGrid&&!e.altKey?state.settings.gridSize:0;
        const moved=moveWaypoint(edge.waypoints,index,{x:round(grid?Math.round(p.x/grid)*grid:p.x),y:round(grid?Math.round(p.y/grid)*grid:p.y)},['orthogonal','curved'].includes(edge.routing));
        edge.waypoints=moved.points;this.waypointSelection=moved.index;edge.manual=true;
      }else if(g.handle==='label') {
        const old=g.before.edges.find(n=>n.id===edge.id).labelOffset||{x:0,y:0};edge.labelOffset={x:round(old.x+p.x-g.start.x),y:round(old.y+p.y-g.start.y)};
      }else if(g.handle.startsWith('dock:')) {
        const source=g.handle==='dock:source',terminal=state.nodes.find(n=>n.id===(source?edge.source:edge.target));
        const endpoint=source?edge.waypoints[0]:edge.waypoints.at(-1);
        const moved={x:endpoint.x+p.x-g.start.x,y:endpoint.y+p.y-g.start.y};
        const local=rotate(moved,center(terminal),-(terminal.rotation||0)),x=Math.max(0,Math.min(1,(local.x-terminal.x)/terminal.width)),y=Math.max(0,Math.min(1,(local.y-terminal.y)/terminal.height));
        const distances=[x,1-x,y,1-y],side=distances.indexOf(Math.min(...distances));
        edge[source?'sourcePort':'targetPort']={x:side===0?0:side===1?1:round(x),y:side===2?0:side===3?1:round(y)};
      }
    }
    try {this.apply(this.route(state,g.before));this.view.autoFit=false;this.view.position();}
    catch(error){this.announce(error.message,true);}
    return true;
  }
  pointerUp(e,cancelled) {
    const g=this.gesture;if(!g||g.pointer!==e.pointerId)return false;
    this.gesture=null;this.guides.replaceChildren();
    if(cancelled){this.apply(g.before);this.view.position();return true;}
    if(g.moved)this.commit(this.scene.state,g.before,'Canvas geometry saved.');
    else if(g.handle==='move') {
      const el=g.hit||this.scene.nodes.get(g.nodeId).el;
      if(e.pointerType==='touch') {
        const now=performance.now(),twice=this.view.tap?.el===el && now-this.view.tap.at<400;
        this.view.tap=twice?null:{el,at:now};if(twice){this.view.touchEditTime=now;this.view.hit(el,'edit');return true;}
      }
      this.view.hit(el,g.add?'toggle':'select');
    }
    this.position();return true;
  }
  cancel() {
    if(this.gesture&&this.scene)this.apply(this.gesture.before);
    if(this.nudge&&this.scene)this.apply(this.nudge);
    this.gesture=null;this.nudge=null;this.guides.replaceChildren();this.position();
  }
  finishNudge() {
    if(!this.nudge)return;const before=this.nudge;this.nudge=null;
    try {this.commit(this.route(this.scene.state,before),before,'Selection nudged.');}catch(error){this.apply(before);this.announce(error.message,true);}
  }
  changeNode(state,id,values) {
    const node=state.nodes.find(n=>n.id===id),members=descendants(state.nodes,[id]);
    if(node.locked || state.nodes.some(n=>members.has(n.id)&&n.locked))throw Error('Unlock this shape and its children before changing its geometry.');
    const next={...node,...values},dx=next.x-node.x,dy=next.y-node.y,angle=(next.rotation||0)-(node.rotation||0);
    for(const child of state.nodes)if(child.id!==id&&members.has(child.id)) {
      const moved=rotate({x:child.x+dx+child.width/2,y:child.y+dy+child.height/2},center(next),angle);
      child.x=round(moved.x-child.width/2);child.y=round(moved.y-child.height/2);child.rotation=round((child.rotation||0)+angle);
    }
    Object.assign(node,next);
  }
  keyDown(e) {
    if(!this.calculate)return;
    if(e.target.closest('input,textarea,[contenteditable]'))return;
    if(e.key==='Escape'&&(this.gesture||this.nudge)){this.cancel();e.preventDefault();e.stopImmediatePropagation();this.view.position();return;}
    if((e.ctrlKey||e.metaKey)&&e.key.toLowerCase()==='a'&&this.scene?.state.nodes.length){this.action('selectAll');e.preventDefault();e.stopImmediatePropagation();return;}
    if((e.key==='Delete'||e.key==='Backspace')&&this.waypointSelection!=null&&this.selectedEdges().length===1&&this.selectedEdges()[0]===this.waypointEdge) {
      const before=clone(this.scene.state),state=clone(before),edge=state.edges.find(n=>n.id===this.selectedEdges()[0]),index=this.waypointSelection;
      if(index>0&&index<edge.waypoints.length-1&&!edge.locked){edge.waypoints.splice(index,1);edge.manual=true;this.commit(state,before,'Waypoint removed.');}
      this.waypointSelection=null;e.preventDefault();e.stopImmediatePropagation();return;
    }
    if(!e.key.startsWith('Arrow')||e.ctrlKey||e.metaKey||e.altKey||!this.selectedNodes().length)return;
    const ids=descendants(this.scene.state.nodes,this.selectedNodes());
    if(this.scene.state.nodes.some(n=>ids.has(n.id)&&n.locked)){e.preventDefault();e.stopImmediatePropagation();return;}
    this.nudge??=clone(this.scene.state);
    const step=e.shiftKey?this.scene.state.settings.gridSize:1,dx=e.key==='ArrowLeft'?-step:e.key==='ArrowRight'?step:0,dy=e.key==='ArrowUp'?-step:e.key==='ArrowDown'?step:0;
    const state=clone(this.scene.state);for(const n of state.nodes)if(ids.has(n.id)){n.x+=dx;n.y+=dy;}
    this.apply(state);this.view.autoFit=false;this.view.position();e.preventDefault();e.stopImmediatePropagation();
  }
  insertWaypoint(e) {
    const original=this.scene.edgeFor(e.target);if(!original||original.locked)return;
    const before=clone(this.scene.state),state=clone(before),edge=state.edges.find(n=>n.id===original.id),p=this.scene.world(e.clientX,e.clientY);
    const nearest=projectToRoute(p,edge.waypoints);edge.waypoints.splice(nearest.index+1,0,{x:round(nearest.point.x),y:round(nearest.point.y)});edge.manual=true;
    this.view.hit(this.scene.edges.get(edge.id).el);this.commit(state,before,'Waypoint inserted.');
    this.waypointSelection=nearest.index+1;
    this.waypointEdge=edge.id;
  }
  showGuides(guides) {
    for(const guide of guides){const el=document.createElement('div'),p=this.scene.screen({x:guide.value,y:guide.value});
      el.className=guide.axis==='x'?'canvas-guide vertical':'canvas-guide horizontal';el.style[guide.axis==='x'?'left':'top']=(guide.axis==='x'?p.x:p.y)+'px';this.guides.append(el);}
  }
  position() {
    this.overlay.replaceChildren();if(!this.calculate||!this.scene)return;
    const ids=this.selectedNodes(),edgeIds=this.selectedEdges();
    const button=(name,p,label)=>{
      const b=document.createElement('button');b.type='button';b.className='canvas-handle '+name.split(':')[0];b.dataset.canvasHandle=name;
      b.setAttribute('aria-label',label);b.title=label;b.tabIndex=-1;const screen=this.scene.screen(p);b.style.left=screen.x+'px';b.style.top=screen.y+'px';
      b.addEventListener('click',e=>e.stopPropagation());this.overlay.append(b);
    };
    if(ids.length===1) {
      const n=this.scene.state.nodes.find(v=>v.id===ids[0]);if(!n||n.locked)return;const origin=center(n);
      for(const [name,x,y] of [['nw',0,0],['n',.5,0],['ne',1,0],['e',1,.5],['se',1,1],['s',.5,1],['sw',0,1],['w',0,.5]])
        button('resize:'+name,rotate({x:n.x+n.width*x,y:n.y+n.height*y},origin,n.rotation||0),'Resize '+name.toUpperCase()+'; Shift keeps aspect, Control resizes from center');
      button('rotate',rotate({x:origin.x,y:n.y-26/this.view.z},origin,n.rotation||0),'Rotate; Shift snaps to 15 degrees');
    }else if(edgeIds.length===1&&!ids.length) {
      const edge=this.scene.state.edges.find(v=>v.id===edgeIds[0]);if(!edge||edge.locked||edge.waypoints.length<2)return;
      edge.waypoints.slice(1,-1).forEach((p,i)=>button('point:'+(i+1),p,'Move waypoint '+(i+1)+'; Delete removes it'));
      // Keep attachment controls clear of the S/T semantic retarget buttons.
      // Offset in screen pixels; dragging still starts at the real attachment.
      const dock=(name,index,adjacent,label)=>{
        const p=edge.waypoints[index],q=edge.waypoints[adjacent],dx=q.x-p.x,dy=q.y-p.y,length=Math.hypot(dx,dy)||1;
        const offset=28/this.view.z;
        button(name,{x:p.x-dy/length*offset,y:p.y+(dx||(!dy?1:0))/length*offset},label);
      };
      dock('dock:source',0,1,'Move source attachment on its shape');
      dock('dock:target',edge.waypoints.length-1,edge.waypoints.length-2,'Move target attachment on its shape');
      const p=pointOnRoute(edge.waypoints),offset=edge.labelOffset||{x:0,y:0};button('label',{x:p.x+offset.x,y:p.y+offset.y},'Move connector label');
    }
  }
}
