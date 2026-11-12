// Pure SVG geometry used by the retained WebGPU scene compiler.
// All coordinates are local SVG user units. Curves are bounded by screen-space
// tolerance supplied by the caller; fills retain winding instead of ear clipping.
export class GeometryError extends Error {}
const EPS=1e-9, MAX_POINTS=32768, MAX_VERTICES=1048576;
const numeric=/[+-]?(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?/g;
const commands=new Set('MmZzLlHhVvCcSsQqTtAa');
const finite=(v)=>Number.isFinite(v)&&Math.abs(v)<=1e9;
const cross=(a,b)=>a[0]*b[1]-a[1]*b[0];
const subtract=(a,b)=>[a[0]-b[0],a[1]-b[1]];
const dot=(a,b)=>a[0]*b[0]+a[1]*b[1];
const lerp=(a,b,t)=>[a[0]+(b[0]-a[0])*t,a[1]+(b[1]-a[1])*t];
const equal=(a,b)=>Math.abs(a[0]-b[0])<EPS&&Math.abs(a[1]-b[1])<EPS;
export const identity=()=>[1,0,0,1,0,0];
export function multiply(a,b){return[
 a[0]*b[0]+a[2]*b[1],a[1]*b[0]+a[3]*b[1],
 a[0]*b[2]+a[2]*b[3],a[1]*b[2]+a[3]*b[3],
 a[0]*b[4]+a[2]*b[5]+a[4],a[1]*b[4]+a[3]*b[5]+a[5]];}
export function inverse(m){
 const d=m[0]*m[3]-m[1]*m[2];if(!finite(d)||Math.abs(d)<1e-14)throw new GeometryError('singular-transform');
 return[m[3]/d,-m[1]/d,-m[2]/d,m[0]/d,(m[2]*m[5]-m[3]*m[4])/d,(m[1]*m[4]-m[0]*m[5])/d];
}
export function transformPoint(m,p){return[m[0]*p[0]+m[2]*p[1]+m[4],m[1]*p[0]+m[3]*p[1]+m[5]];}
export function maxScale(m){
 const s=m[0]*m[0]+m[1]*m[1],t=m[2]*m[2]+m[3]*m[3],u=m[0]*m[2]+m[1]*m[3];
 return Math.sqrt(Math.max(0,(s+t+Math.sqrt((s-t)*(s-t)+4*u*u))/2));
}
export function numbers(value){
 const text=String(value??''),matches=[...text.matchAll(numeric)];
 let end=0;const result=[];
 for(const match of matches){if(!/^[\s,]*$/.test(text.slice(end,match.index)))throw new GeometryError('invalid-number-list');
  const value=Number(match[0]);if(!finite(value))throw new GeometryError('invalid-coordinate');result.push(value);end=match.index+match[0].length;}
 if(!/^[\s,]*$/.test(text.slice(end)))throw new GeometryError('invalid-number-list');return result;
}
export function parseTransform(value){
 const text=String(value??'');let result=identity(),end=0;
 const expression=/([a-zA-Z]+)\s*\(([^)]*)\)/g;
 for(const match of text.matchAll(expression)){
  if(!/^[\s,]*$/.test(text.slice(end,match.index)))throw new GeometryError('invalid-transform');
  end=match.index+match[0].length;const n=numbers(match[2]),name=match[1];let m;
  if(name==='matrix'&&n.length===6)m=n;
  else if(name==='translate'&&(n.length===1||n.length===2))m=[1,0,0,1,n[0],n[1]??0];
  else if(name==='scale'&&(n.length===1||n.length===2))m=[n[0],0,0,n[1]??n[0],0,0];
  else if(name==='rotate'&&(n.length===1||n.length===3)){
   const a=n[0]*Math.PI/180,c=Math.cos(a),s=Math.sin(a);m=[c,s,-s,c,0,0];
   if(n.length===3)m=multiply(multiply([1,0,0,1,n[1],n[2]],m),[1,0,0,1,-n[1],-n[2]]);
  }else if((name==='skewX'||name==='skewY')&&n.length===1){
   const a=Math.tan(n[0]*Math.PI/180);m=name==='skewX'?[1,0,a,1,0,0]:[1,a,0,1,0,0];
  }else throw new GeometryError('unsupported-transform');
  result=multiply(result,m);if(!result.every(finite))throw new GeometryError('invalid-transform');
 }
 if(!/^[\s,]*$/.test(text.slice(end)))throw new GeometryError('invalid-transform');return result;
}
function lineDistance(p,a,b){
 const v=subtract(b,a),length=Math.hypot(...v);return length<EPS?Math.hypot(...subtract(p,a)):Math.abs(cross(subtract(p,a),v))/length;
}
function flattenCubic(a,b,c,d,tolerance,add,depth=0){
 // The chord test also bounds control-polygon excess, so collinear backtracking
 // curves are not reduced to a segment that loses their extrema.
 const chord=Math.hypot(...subtract(d,a)),polygon=Math.hypot(...subtract(b,a))+Math.hypot(...subtract(c,b))+Math.hypot(...subtract(d,c));
 if(depth>=18||Math.max(lineDistance(b,a,d),lineDistance(c,a,d),polygon-chord)<=tolerance){add(d);return;}
 const ab=lerp(a,b,.5),bc=lerp(b,c,.5),cd=lerp(c,d,.5),abc=lerp(ab,bc,.5),bcd=lerp(bc,cd,.5),mid=lerp(abc,bcd,.5);
 flattenCubic(a,ab,abc,mid,tolerance,add,depth+1);flattenCubic(mid,bcd,cd,d,tolerance,add,depth+1);
}
function flattenQuadratic(a,b,c,tolerance,add){
 flattenCubic(a,lerp(a,b,2/3),lerp(c,b,2/3),c,tolerance,add);
}
export function arcPoints(start,rx,ry,rotation,large,sweep,end,tolerance=.25){
 rx=Math.abs(rx);ry=Math.abs(ry);
 if(![...start,rx,ry,rotation,...end].every(finite)||![0,1].includes(large)||![0,1].includes(sweep))throw new GeometryError('invalid-arc');
 if(equal(start,end))return[];if(rx<EPS||ry<EPS)return[end];
 const phi=rotation*Math.PI/180,cp=Math.cos(phi),sp=Math.sin(phi),dx=(start[0]-end[0])/2,dy=(start[1]-end[1])/2;
 const xp=cp*dx+sp*dy,yp=-sp*dx+cp*dy;
 const radii=xp*xp/(rx*rx)+yp*yp/(ry*ry);if(radii>1){const s=Math.sqrt(radii);rx*=s;ry*=s;}
 const numerator=Math.max(0,rx*rx*ry*ry-rx*rx*yp*yp-ry*ry*xp*xp),denominator=rx*rx*yp*yp+ry*ry*xp*xp;
 const sign=large===sweep?-1:1,factor=sign*Math.sqrt(denominator?numerator/denominator:0);
 const cxp=factor*rx*yp/ry,cyp=-factor*ry*xp/rx,cx=cp*cxp-sp*cyp+(start[0]+end[0])/2,cy=sp*cxp+cp*cyp+(start[1]+end[1])/2;
 const u=[(xp-cxp)/rx,(yp-cyp)/ry],v=[(-xp-cxp)/rx,(-yp-cyp)/ry];
 const theta=Math.atan2(u[1],u[0]);let delta=Math.atan2(cross(u,v),dot(u,v));
 if(!sweep&&delta>0)delta-=2*Math.PI;if(sweep&&delta<0)delta+=2*Math.PI;
 const step=Math.max(.0005,2*Math.acos(Math.max(-1,1-Math.max(1e-5,tolerance)/Math.max(rx,ry))));
 const count=Math.max(1,Math.ceil(Math.abs(delta)/(Math.PI/2)),Math.ceil(Math.abs(delta)/step));if(count>MAX_POINTS)throw new GeometryError('geometry-budget');
 const result=[];for(let i=1;i<=count;i++){const a=theta+delta*i/count;result.push([cx+cp*rx*Math.cos(a)-sp*ry*Math.sin(a),cy+sp*rx*Math.cos(a)+cp*ry*Math.sin(a)]);}
 result[result.length-1]=end;return result;
}
function tokenizePath(value){
 const text=String(value??''),tokens=[];let i=0;
 while(i<text.length){
  const c=text[i];if(/[\s,]/.test(c)){i++;continue;}
  if(commands.has(c)){tokens.push(c);i++;continue;}
  const match=/^[+-]?(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?/.exec(text.slice(i));
  if(!match)throw new GeometryError('invalid-path');const n=Number(match[0]);if(!finite(n))throw new GeometryError('invalid-coordinate');
  tokens.push(n);i+=match[0].length;if(tokens.length>MAX_POINTS*8)throw new GeometryError('geometry-budget');
 }return tokens;
}
export function flattenPath(value,tolerance=.25){
 const tokens=tokenizePath(value),result=[];let i=0,command='',previous='',current=[0,0],start=[0,0],cubic=null,quadratic=null,contour=null,count=0;
 tolerance=Math.max(1e-5,Number(tolerance)||.25);
 const add=p=>{if(!p.every(finite))throw new GeometryError('invalid-coordinate');if(++count>MAX_POINTS)throw new GeometryError('geometry-budget');contour.points.push(p);current=p;};
 const take=n=>{if(i+n>tokens.length||tokens.slice(i,i+n).some(v=>typeof v!=='number'))throw new GeometryError('invalid-path');const result=tokens.slice(i,i+n);i+=n;return result;};
 while(i<tokens.length){
  if(typeof tokens[i]==='string')command=tokens[i++];
  if(!command)throw new GeometryError('invalid-path');
  const upper=command.toUpperCase(),relative=command!==upper,origin=current;
  const point=(x,y)=>relative?[origin[0]+x,origin[1]+y]:[x,y];
  if(upper==='Z'){
   if(!contour)throw new GeometryError('invalid-path');contour.closed=true;current=start;previous='Z';command='';cubic=quadratic=null;continue;
  }
  if(upper!=='M'&&!contour)throw new GeometryError('path-must-start-with-move');
  if(upper==='M'){const n=take(2);current=point(...n);start=current;contour={points:[current],closed:false};result.push(contour);if(++count>MAX_POINTS)throw new GeometryError('geometry-budget');command=relative?'l':'L';}
  else if(upper==='L'){const n=take(2);add(point(...n));}
  else if(upper==='H'){const n=take(1);add([relative?current[0]+n[0]:n[0],current[1]]);}
  else if(upper==='V'){const n=take(1);add([current[0],relative?current[1]+n[0]:n[0]]);}
  else if(upper==='C'){
   const n=take(6),a=point(n[0],n[1]),b=point(n[2],n[3]),end=point(n[4],n[5]);flattenCubic(current,a,b,end,tolerance,add);cubic=b;
  }else if(upper==='S'){
   const n=take(4),a=(previous==='C'||previous==='S')&&cubic?[2*current[0]-cubic[0],2*current[1]-cubic[1]]:current,b=point(n[0],n[1]),end=point(n[2],n[3]);
   flattenCubic(current,a,b,end,tolerance,add);cubic=b;
  }else if(upper==='Q'){
   const n=take(4),a=point(n[0],n[1]),end=point(n[2],n[3]);flattenQuadratic(current,a,end,tolerance,add);quadratic=a;
  }else if(upper==='T'){
   const n=take(2),a=(previous==='Q'||previous==='T')&&quadratic?[2*current[0]-quadratic[0],2*current[1]-quadratic[1]]:current,end=point(n[0],n[1]);flattenQuadratic(current,a,end,tolerance,add);quadratic=a;
  }else if(upper==='A'){
   const n=take(7),end=point(n[5],n[6]);for(const p of arcPoints(current,n[0],n[1],n[2],n[3],n[4],end,tolerance))add(p);
  }else throw new GeometryError('unsupported-path-command');
  if(upper!=='C'&&upper!=='S')cubic=null;if(upper!=='Q'&&upper!=='T')quadratic=null;previous=upper;
 }
 return result;
}
export function ellipseContours(cx,cy,rx,ry,tolerance=.25){
 if(rx<=0||ry<=0)return[];
 const points=[[cx+rx,cy],...arcPoints([cx+rx,cy],rx,ry,0,0,1,[cx-rx,cy],tolerance),...arcPoints([cx-rx,cy],rx,ry,0,0,1,[cx+rx,cy],tolerance)];
 points.pop();return[{points,closed:true}];
}
export function rectContours(x,y,width,height,rx=0,ry=rx,tolerance=.25){
 if(width<=0||height<=0)return[];rx=Math.min(Math.max(0,rx),width/2);ry=Math.min(Math.max(0,ry),height/2);
 if(!rx||!ry)return[{points:[[x,y],[x+width,y],[x+width,y+height],[x,y+height]],closed:true}];
 return flattenPath('M'+(x+rx)+' '+y+'H'+(x+width-rx)+'A'+rx+' '+ry+' 0 0 1 '+(x+width)+' '+(y+ry)+'V'+(y+height-ry)+'A'+rx+' '+ry+' 0 0 1 '+(x+width-rx)+' '+(y+height)+'H'+(x+rx)+'A'+rx+' '+ry+' 0 0 1 '+x+' '+(y+height-ry)+'V'+(y+ry)+'A'+rx+' '+ry+' 0 0 1 '+(x+rx)+' '+y+'Z',tolerance);
}
export function boundsOfPoints(points){
 if(!points.length)return[0,0,0,0];let left=Infinity,top=Infinity,right=-Infinity,bottom=-Infinity;
 for(const p of points){if(!p.every(finite))throw new GeometryError('invalid-coordinate');left=Math.min(left,p[0]);top=Math.min(top,p[1]);right=Math.max(right,p[0]);bottom=Math.max(bottom,p[1]);}
 return[left,top,Math.max(0,right-left),Math.max(0,bottom-top)];
}
export function boundsOf(contours){return boundsOfPoints(contours.flatMap(c=>c.points));}
export function transformBounds(bounds,m){
 const[x,y,w,h]=bounds;return boundsOfPoints([[x,y],[x+w,y],[x+w,y+h],[x,y+h]].map(p=>transformPoint(m,p)));
}
export function intersectBounds(a,b){
 if(!a)return b?.slice();if(!b)return a.slice();const x=Math.max(a[0],b[0]),y=Math.max(a[1],b[1]),r=Math.min(a[0]+a[2],b[0]+b[2]),d=Math.min(a[1]+a[3],b[1]+b[3]);return[x,y,Math.max(0,r-x),Math.max(0,d-y)];
}
function triangle(output,a,b,c){
 if(output.length+6>MAX_VERTICES*2)throw new GeometryError('geometry-budget');output.push(a[0],a[1],b[0],b[1],c[0],c[1]);
}
export function fillTriangles(contours,rule='nonzero'){
 // An 8-bit stencil cannot represent arbitrary winding magnitude. This bound
 // limits possible signed ray crossings below 256; larger paths use the
 // faithful isolated subtree renderer instead of overflowing silently.
 if(rule==='nonzero'&&contours.reduce((n,c)=>n+c.points.length,0)>384)throw new GeometryError('winding-budget');
 const output=[];for(const c of contours){const p=c.points;for(let i=1;i+1<p.length;i++)triangle(output,p[0],p[i],p[i+1]);}return output;
}
export function dashContours(contours,dash,offset=0){
 if(!dash?.length)return contours;
 if(dash.some(n=>!finite(n)||n<0)||!finite(offset))throw new GeometryError('invalid-dash');
 if(dash.length%2)dash=[...dash,...dash];const period=dash.reduce((a,b)=>a+b,0);
 if(period<EPS||dash.filter((_,i)=>i%2).reduce((a,b)=>a+b,0)<EPS)return contours;
 const result=[];
 for(const contour of contours){
  const points=contour.points;if(points.length<2){result.push(contour);continue;}
  let index=0,phase=((offset%period)+period)%period,remaining=0,steps=0;
  while(phase>=dash[index]-EPS){phase-=dash[index];index=(index+1)%dash.length;if(++steps>dash.length*2)break;}
  remaining=dash[index]-phase;let current=null;
  const finish=()=>{if(current?.points.length>1)result.push(current);current=null;};
  const first=result.length,end=contour.closed?points.length:points.length-1;
  for(let i=0;i<end;i++){
   const a=points[i],b=points[(i+1)%points.length],length=Math.hypot(...subtract(b,a));if(length<EPS)continue;let at=0;
   while(at<length-EPS){
    let zero=0;while(remaining<EPS){const was=index%2===0;index=(index+1)%dash.length;remaining=dash[index];if(was&&index%2!==0&&remaining>EPS)finish();if(++zero>dash.length)throw new GeometryError('invalid-dash');}
    const amount=Math.min(remaining,length-at),p=lerp(a,b,at/length),q=lerp(a,b,(at+amount)/length);
    if(index%2===0){current??={points:[p],closed:false};if(!equal(current.points.at(-1),p))current.points.push(p);current.points.push(q);}
    at+=amount;remaining-=amount;if(result.length>MAX_POINTS)throw new GeometryError('geometry-budget');
   }
  }
  finish();
  if(contour.closed&&result.length>first+1){
   const a=result[first],b=result.at(-1);if(equal(a.points[0],points[0])&&equal(b.points.at(-1),points[0])){a.points=[...b.points.slice(0,-1),...a.points];result.pop();}
  }
 }
 return result;
}
export function strokeTriangles(contours,width,options={}){
 if(!finite(width)||width<0)throw new GeometryError('invalid-stroke');if(width<EPS)return[];
 const cap=options.cap||'butt',join=options.join||'miter',limit=Math.max(1,Number(options.miterLimit)||4),tolerance=Math.max(1e-5,options.tolerance||.25),h=width/2;
 if(!['butt','round','square'].includes(cap)||!['miter','round','bevel'].includes(join))throw new GeometryError('unsupported-stroke');
 if(options.dash?.some(n=>n===0)&&cap!=='butt')throw new GeometryError('zero-length-dash');
 const output=[];
 const disk=p=>{const c=ellipseContours(p[0],p[1],h,h,tolerance);for(const contour of c){const a=contour.points;for(let i=0;i<a.length;i++)triangle(output,p,a[i],a[(i+1)%a.length]);}};
 const capQuad=(p,u,direction)=>{const n=[-u[1]*h,u[0]*h],q=[p[0]+u[0]*h*direction,p[1]+u[1]*h*direction],a=[p[0]+n[0],p[1]+n[1]],b=[p[0]-n[0],p[1]-n[1]],c=[q[0]-n[0],q[1]-n[1]],d=[q[0]+n[0],q[1]+n[1]];triangle(output,a,b,c);triangle(output,a,c,d);};
 for(const contour of dashContours(contours,options.dash,options.dashOffset)){
  const points=[];for(const p of contour.points)if(!points.length||!equal(p,points.at(-1)))points.push(p);
  if(contour.closed&&points.length>1&&equal(points[0],points.at(-1)))points.pop();
  if(points.length===1){if(cap==='round')disk(points[0]);else if(cap==='square'){capQuad(points[0],[1,0],1);capQuad(points[0],[1,0],-1);}continue;}
  if(points.length<2)continue;const count=contour.closed?points.length:points.length-1,dirs=[];
  for(let i=0;i<count;i++){
   const a=points[i],b=points[(i+1)%points.length],length=Math.hypot(...subtract(b,a)),u=[(b[0]-a[0])/length,(b[1]-a[1])/length],n=[-u[1]*h,u[0]*h];dirs.push(u);
   const aa=[a[0]+n[0],a[1]+n[1]],ab=[a[0]-n[0],a[1]-n[1]],ba=[b[0]+n[0],b[1]+n[1]],bb=[b[0]-n[0],b[1]-n[1]];triangle(output,aa,ab,bb);triangle(output,aa,bb,ba);
  }
  const first=contour.closed?0:1,last=contour.closed?points.length:points.length-1;
  for(let i=first;i<last;i++){
   const p=points[i],u=dirs[(i+dirs.length-1)%dirs.length],v=dirs[i%dirs.length],turn=cross(u,v);
   if(Math.abs(turn)<EPS){if(dot(u,v)<0&&join==='round')disk(p);continue;}
   const side=turn>0?-1:1,a=[p[0]-u[1]*h*side,p[1]+u[0]*h*side],b=[p[0]-v[1]*h*side,p[1]+v[0]*h*side];
   if(join==='round'){
    const from=Math.atan2(a[1]-p[1],a[0]-p[0]),delta=Math.atan2(turn,dot(u,v)),step=Math.max(.001,2*Math.acos(Math.max(-1,1-tolerance/h))),n=Math.max(1,Math.ceil(Math.abs(delta)/step));
    let prior=a;for(let j=1;j<=n;j++){const angle=from+delta*j/n,next=[p[0]+h*Math.cos(angle),p[1]+h*Math.sin(angle)];triangle(output,p,prior,next);prior=next;}
   }else if(join==='miter'){
    const t=cross(subtract(b,a),v)/turn,m=[a[0]+t*u[0],a[1]+t*u[1]];
    if(Math.hypot(...subtract(m,p))<=h*limit+EPS){triangle(output,p,a,m);triangle(output,p,m,b);}else triangle(output,p,a,b);
   }else triangle(output,p,a,b);
  }
  if(!contour.closed){if(cap==='round'){disk(points[0]);disk(points.at(-1));}else if(cap==='square'){capQuad(points[0],dirs[0],-1);capQuad(points.at(-1),dirs.at(-1),1);}}
 }
 return output;
}
