// Geometry shared by the live gesture preview and its headless regression tests.
// Committed automatic layouts and obstacle routing are calculated by the C# engine.
export const clamp = (value, low, high) => Math.max(low, Math.min(high, value));
export const round = value => Math.round(value * 1000) / 1000;
export const center = node => ({x: node.x + node.width / 2, y: node.y + node.height / 2});
export const clone = value => structuredClone(value);
export function rotate(point, origin, degrees) {
  const angle = degrees * Math.PI / 180, c = Math.cos(angle), s = Math.sin(angle);
  return {x: origin.x + (point.x - origin.x) * c - (point.y - origin.y) * s,
    y: origin.y + (point.x - origin.x) * s + (point.y - origin.y) * c};
}
export function bounds(nodes) {
  if (!nodes.length) return {x: 0, y: 0, width: 0, height: 0};
  let left = Infinity, top = Infinity, right = -Infinity, bottom = -Infinity;
  for (const node of nodes) {
    const origin = center(node);
    for (const point of [{x:node.x,y:node.y},{x:node.x+node.width,y:node.y},
      {x:node.x+node.width,y:node.y+node.height},{x:node.x,y:node.y+node.height}]) {
      const p = rotate(point, origin, node.rotation || 0);
      left = Math.min(left,p.x); top = Math.min(top,p.y); right = Math.max(right,p.x); bottom = Math.max(bottom,p.y);
    }
  }
  return {x:left,y:top,width:right-left,height:bottom-top};
}
export function descendants(nodes, ids) {
  const result = new Set(ids);
  for (let changed = true; changed;) {
    changed = false;
    for (const node of nodes) if (node.parent && result.has(node.parent) && !result.has(node.id)) {
      result.add(node.id); changed = true;
    }
  }
  return result;
}
export function visibleOwner(node, byId) {
  let owner = node;
  for (let parent = byId.get(node.parent), depth = 0; parent && depth++ < byId.size; parent = byId.get(parent.parent))
    if (parent.collapsed) owner = parent;
  return owner;
}
export function snapMove(box, dx, dy, others, settings, tolerance, free = false, axis = false) {
  if (axis) { if (Math.abs(dx) >= Math.abs(dy)) dy = 0; else dx = 0; }
  const guides = [];
  if (free) return {dx,dy,guides};
  const moved = {...box,x:box.x+dx,y:box.y+dy};
  for (const direction of ['x','y']) {
    if (axis && (direction === 'x' ? dx === 0 : dy === 0)) continue;
    const size = direction === 'x' ? 'width' : 'height';
    const positions = [moved[direction], moved[direction]+moved[size]/2, moved[direction]+moved[size]];
    let correction = Infinity, guide;
    if (settings.snapToObjects !== false) for (const other of others) {
      for (const target of [other[direction],other[direction]+other[size]/2,other[direction]+other[size]])
        for (const position of positions) {
          const delta = target-position;
          if (Math.abs(delta) <= tolerance && Math.abs(delta) < Math.abs(correction)) { correction = delta; guide = target; }
        }
    }
    if (Number.isFinite(correction)) {
      if (direction === 'x') dx += correction; else dy += correction;
      guides.push({axis:direction,value:guide});
    } else if (settings.snapToGrid !== false && settings.gridSize > 0) {
      const delta = Math.round(moved[direction]/settings.gridSize)*settings.gridSize-moved[direction];
      if (direction === 'x') dx += delta; else dy += delta;
    }
  }
  return {dx:round(dx),dy:round(dy),guides};
}
export function resize(node, handle, point, options = {}) {
  const local = rotate(point,center(node),-(node.rotation || 0));
  let left = node.x, right = node.x+node.width, top = node.y, bottom = node.y+node.height;
  const grid = options.free || options.grid <= 0 ? value=>value : value=>Math.round(value/options.grid)*options.grid;
  if (handle.includes('w')) left = Math.min(right-24,grid(local.x));
  if (handle.includes('e')) right = Math.max(left+24,grid(local.x));
  if (handle.includes('n')) top = Math.min(bottom-24,grid(local.y));
  if (handle.includes('s')) bottom = Math.max(top+24,grid(local.y));
  if (options.centered) {
    if (handle.includes('w')) right = node.x+node.width+(node.x-left);
    if (handle.includes('e')) left = node.x-(right-node.x-node.width);
    if (handle.includes('n')) bottom = node.y+node.height+(node.y-top);
    if (handle.includes('s')) top = node.y-(bottom-node.y-node.height);
  }
  if (options.aspect) {
    const ratio = node.width/node.height, width = right-left, height = bottom-top;
    if (width/height > ratio) {
      const next = width/ratio;
      if (options.centered) { top = node.y+(node.height-next)/2; bottom=top+next; }
      else if (handle.includes('n')) top=bottom-next; else bottom=top+next;
    } else {
      const next = height*ratio;
      if (options.centered) { left=node.x+(node.width-next)/2; right=left+next; }
      else if (handle.includes('w')) left=right-next; else right=left+next;
    }
  }
  // Keep the opposite handle fixed in world coordinates for rotated nodes.
  const oldCenter=center(node), newCenter={x:(left+right)/2,y:(top+bottom)/2};
  const rotated=rotate(newCenter,oldCenter,node.rotation || 0);
  return {...node,x:round(left+rotated.x-newCenter.x),y:round(top+rotated.y-newCenter.y),
    width:round(Math.max(24,right-left)),height:round(Math.max(24,bottom-top))};
}
export function pointOnRoute(points, fraction = .5) {
  if (!points?.length) return {x:0,y:0};
  const lengths=points.slice(1).map((p,i)=>Math.hypot(p.x-points[i].x,p.y-points[i].y));
  let target=lengths.reduce((sum,n)=>sum+n,0)*clamp(fraction,0,1);
  for(let i=0;i<lengths.length;i++) {
    if(target<=lengths[i]) { const t=lengths[i] ? target/lengths[i] : 0; return {x:points[i].x+(points[i+1].x-points[i].x)*t,y:points[i].y+(points[i+1].y-points[i].y)*t}; }
    target-=lengths[i];
  }
  return {...points.at(-1)};
}
export function projectToRoute(point, points) {
  let distance=Infinity, best={index:0,point:points?.[0] || point};
  for(let i=0;i+1<points.length;i++) {
    const a=points[i],b=points[i+1],dx=b.x-a.x,dy=b.y-a.y;
    const t=clamp(((point.x-a.x)*dx+(point.y-a.y)*dy)/(dx*dx+dy*dy || 1),0,1);
    const p={x:a.x+t*dx,y:a.y+t*dy},d=Math.hypot(point.x-p.x,point.y-p.y);
    if(d<distance) {distance=d;best={index:i,point:p,distance:d};}
  }
  return best;
}
export function moveWaypoint(points,index,position,orthogonal=false) {
  if(index<=0 || index>=points.length-1) return {points:clone(points),index};
  const result=clone(points),axis=(a,b)=>Math.abs(a.y-b.y)<.001?'h':Math.abs(a.x-b.x)<.001?'v':null;
  if(!orthogonal || points.some((p,i)=>i>0&&!axis(points[i-1],p))) {
    result[index]={...position};return {points:result,index};
  }
  const incoming=axis(points[index-1],points[index]),outgoing=axis(points[index],points[index+1]);
  const left=result.slice(0,index),right=result.slice(index+1),a=left.at(-1),b=right[0];
  // Sliding adjacent bends preserves their outer segments. At a fixed terminal
  // or a collinear inserted point, add an elbow instead of moving the terminal.
  if(index>1 && axis(points[index-2],points[index-1])!==incoming) a[incoming==='h'?'y':'x']=position[incoming==='h'?'y':'x'];
  else left.push(incoming==='h'?{x:position.x,y:a.y}:{x:a.x,y:position.y});
  if(index+2<points.length && axis(points[index+1],points[index+2])!==outgoing) b[outgoing==='h'?'y':'x']=position[outgoing==='h'?'y':'x'];
  else right.unshift(outgoing==='h'?{x:b.x,y:position.y}:{x:position.x,y:b.y});
  const combined=[],append=p=>{if(!combined.length||Math.hypot(p.x-combined.at(-1).x,p.y-combined.at(-1).y)>.0001)combined.push(p);};
  left.forEach(append);append({...position});const selected=combined.length-1;right.forEach(append);
  return {points:combined,index:selected};
}
export function routePath(points, routing) {
  if(!points?.length) return '';
  let path=`M${round(points[0].x)} ${round(points[0].y)}`;
  if(routing==='curved' && points.length===2) {
    const [a,b]=points,dx=(b.x-a.x)/2;
    return `${path}C${round(a.x+dx)} ${round(a.y)} ${round(b.x-dx)} ${round(b.y)} ${round(b.x)} ${round(b.y)}`;
  }
  for(let i=1;i<points.length;i++) {
    const p=points[i];
    if(routing==='curved' && i+1<points.length) {
      const previous=points[i-1],next=points[i+1];
      const d1=Math.hypot(p.x-previous.x,p.y-previous.y),d2=Math.hypot(next.x-p.x,next.y-p.y),r=Math.min(10,d1/2,d2/2);
      path+=`L${round(p.x-(p.x-previous.x)*r/(d1||1))} ${round(p.y-(p.y-previous.y)*r/(d1||1))}`;
      path+=`Q${round(p.x)} ${round(p.y)} ${round(p.x+(next.x-p.x)*r/(d2||1))} ${round(p.y+(next.y-p.y)*r/(d2||1))}`;
    } else path+=`L${round(p.x)} ${round(p.y)}`;
  }
  return path;
}
