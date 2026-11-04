// Mono's UTF-16 TextDecoder consumes a leading U+FEFF when Uno projects Text to
// the native textarea. Keep this workaround scoped to a registered source editor;
// the managed Text property and document history remain authoritative.
const registrations=new Map();

function repair(entry) {
  if(registrations.get(entry.id)!==entry||!entry.root.isConnected)return;
  const input=entry.root.querySelector('textarea');
  entry.input=input;
  const expected=entry.expected;
  if(!input||!expected?.startsWith('\uFEFF')||input.value!==expected.slice(1))return;
  const start=input.selectionStart,end=input.selectionEnd,direction=input.selectionDirection;
  const top=input.scrollTop,left=input.scrollLeft;
  input.value=expected;
  // Both offsets refer to the prefix-stripped native value. Restoring exactly
  // one UTF-16 code unit maps that same caret/selection one position forward.
  input.setSelectionRange(Math.min(start+1,expected.length),Math.min(end+1,expected.length),direction);
  input.scrollTop=top;input.scrollLeft=left;
}

export function syncSourceText(id,json) {
  const source=JSON.parse(json);
  if(typeof source!=='string')throw new TypeError('Source text must be a JSON string.');
  // HTML textarea values normalize line endings. This comparison affects only
  // the DOM projection; the managed source keeps its original CRLF/CR bytes.
  const expected=source.replace(/\r\n?/g,'\n');
  const root=document.getElementById(id);
  let entry=registrations.get(id);
  if(entry?.root!==root){removeSourceText(id);entry=null;}
  if(!root)return;
  if(!entry) {
    entry={id,root,expected:null,input:null,observer:null,onInput:null};
    // A user can intentionally remove the BOM. Never restore an older managed
    // value while the native input event is updating Text and document history.
    entry.onInput=event=>{if(event.target===entry.input)entry.expected=null;};
    root.addEventListener('input',entry.onInput,true);
    entry.observer=new MutationObserver(()=>repair(entry));
    entry.observer.observe(root,{childList:true,subtree:true});
    registrations.set(id,entry);
  }
  entry.expected=expected;
  repair(entry);
  // A property callback may precede the remaining native template updates.
  // Recheck only the same registration and its newest expected value.
  queueMicrotask(()=>repair(entry));
}

export function removeSourceText(id) {
  const entry=registrations.get(id);
  if(!entry)return;
  registrations.delete(id);
  entry.observer.disconnect();
  entry.root.removeEventListener('input',entry.onInput,true);
}
