import {ganttRange} from './gantt-geometry.js';

// Timeline gestures edit dates and duration, never generic rectangle geometry.
// A preview is local to the current SVG; one validated source command ends it.
export class GanttEditing {
  constructor(view) {
    this.view=view;this.authoring=false;
    this.controls=document.createElement('div');this.controls.className='gantt-controls';this.controls.hidden=true;view.surface.append(this.controls);
    for(const mode of ['start','end']) {
      const handle=document.createElement('button');handle.type='button';handle.className='gantt-handle';handle.dataset.ganttHandle=mode;
      handle.setAttribute('aria-label',`Resize task ${mode}`);handle.title=`Resize task ${mode}`;this.controls.append(handle);
    }
    view.surface.addEventListener('keydown',e=>this.keyDown(e),true);
    view.surface.addEventListener('keyup',e=>{if(e.key==='ArrowLeft'||e.key==='ArrowRight')this.finishNudge();},true);
    // Resize handles are focusable children: their blur does not bubble to the
    // surface. End a held-key transaction whenever focus leaves its target.
    view.surface.addEventListener('focusout',()=>this.finishNudge());
    // Panning/zooming changes the coordinate frame of an in-flight preview.
    // Restore it before the viewport applies the wheel gesture.
    view.surface.addEventListener('wheel',()=>this.cancel(),{capture:true,passive:true});
  }
  task() {return this.view.marked?.closest('[data-gantt-id]');}
  field(task) {
    const label=task?.querySelector('[data-edit-kind="gantt-label"]');
    if(!label||label.closest('[data-source-generated="true"]'))return null;
    const target=this.view.pick(label),hit=this.view.index?.hits.get(target);
    return hit?.field?.kind==='gantt-label'&&!hit.generated&&target.closest('[data-gantt-id]')===task?target:null;
  }
  configure(enabled) {this.cancel();this.authoring=enabled;this.position();}
  eligible(task) {return this.authoring&&this.view.pristine?.backend==='managed'&&this.view.canvas?.revision>=0&&!!this.field(task);}
  position() {
    const task=this.drag?.task||this.nudge?.task||this.task(),bar=task?.querySelector('.gantt-bar');
    const show=this.eligible(task)&&bar&&task.dataset.ganttMilestone!=='true'&&this.view.surface.dataset.tool==='select';
    this.controls.hidden=!show;if(!show)return;
    const box=bar.getBoundingClientRect(),surface=this.view.rect();
    for(const handle of this.controls.children) {
      const x=handle.dataset.ganttHandle==='start'?box.left:box.right;
      Object.assign(handle.style,{left:`${x-surface.left-5}px`,top:`${box.top-surface.top-3}px`,height:`${Math.max(18,box.height+6)}px`});
    }
  }
  begin(task,mode) {
    const bar=task.querySelector('.gantt-bar'),box=bar.getBBox();
    return {task,bar,box,mode,transform:bar.getAttribute('transform'),version:this.view.version,revision:this.view.canvas.revision,
      id:task.dataset.ganttId,start:Number(task.dataset.ganttStart),duration:Number(task.dataset.ganttDuration),
      unit:Number(task.dataset.ganttUnit),milestone:task.dataset.ganttMilestone==='true',delta:0};
  }
  pointerDown(e,el,tool) {
    if(tool!=='select'||e.button!==0||e.shiftKey||e.ctrlKey||e.metaKey)return false;
    const handle=e.target.closest('[data-gantt-handle]');
    const task=handle?this.task():e.target.closest('.gantt-bar')?.closest('[data-gantt-id]');
    if(!this.eligible(task))return false;
    this.finishNudge();this.cancel();
    this.drag={...this.begin(task,handle?.dataset.ganttHandle||'move'),pointer:e.pointerId,x:e.clientX};
    this.view.hit(this.field(task));this.position();e.preventDefault();return true;
  }
  pointerMove(e) {
    const d=this.drag;if(!d||d.pointer!==e.pointerId)return false;
    if(d.version!==this.view.version){this.cancel();return true;}
    // The task frame includes native DPI/style transforms. The root SVG does
    // not, and the bar's frame changes during a resize preview.
    const scale=Math.abs(d.task.getScreenCTM()?.a||this.view.z);
    this.preview(d,Math.round((e.clientX-d.x)/(d.unit*scale)));e.preventDefault();return true;
  }
  pointerUp(e,cancelled) {
    const d=this.drag;if(!d||d.pointer!==e.pointerId)return false;
    this.drag=null;cancelled?this.restore(d):this.commit(d);this.position();return true;
  }
  preview(d,delta) {
    d.delta=delta;d.next=ganttRange(d.start,d.duration,delta,d.mode,d.milestone);
    const dx=(d.next.start-d.start)*d.unit;
    const scale=d.milestone?1:(d.next.duration*d.unit-4)/(d.duration*d.unit-4);
    d.bar.setAttribute('transform',`translate(${d.box.x+dx} 0) scale(${scale} 1) translate(${-d.box.x} 0)`);
    this.view.announcement.textContent=`Start day ${d.next.start+1}. ${d.next.duration} day${d.next.duration===1?'':'s'}.`;
    this.position();
  }
  restore(d) {if(d.transform===null)d.bar.removeAttribute('transform');else d.bar.setAttribute('transform',d.transform);}
  commit(d) {
    this.restore(d);
    if(d.version!==this.view.version||!d.next||d.next.start===d.start&&d.next.duration===d.duration)return;
    this.view.emit('command',{action:'gantt-geometry',text:JSON.stringify({id:d.id,revision:d.revision,mode:d.mode,...d.next})});
  }
  keyDown(e) {
    if(e.key==='Escape'&&(this.drag||this.nudge)){this.cancel();e.preventDefault();e.stopImmediatePropagation();return;}
    if(e.key!=='ArrowLeft'&&e.key!=='ArrowRight'||e.ctrlKey||e.metaKey||e.altKey||
      e.target.closest('input,textarea,[contenteditable]')||this.view.surface.dataset.tool!=='select')return;
    // A pointer gesture owns one source transaction; do not open a keyboard
    // transaction or navigate to another object while that pointer is active.
    if(this.drag){e.preventDefault();e.stopImmediatePropagation();return;}
    const task=this.task();if(!this.eligible(task))return;
    this.nudge??=this.begin(task,e.target.closest('[data-gantt-handle]')?.dataset.ganttHandle||'move');
    const delta=(e.key==='ArrowLeft'?-1:1)*(e.shiftKey?7:1);
    this.preview(this.nudge,this.nudge.delta+delta);e.preventDefault();e.stopImmediatePropagation();
  }
  finishNudge() {const d=this.nudge;this.nudge=null;if(d)this.commit(d);this.position();}
  cancel() {if(this.drag)this.restore(this.drag);if(this.nudge)this.restore(this.nudge);this.drag=this.nudge=null;this.position();}
}
