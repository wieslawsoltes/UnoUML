// Embed the exact web-font faces used by SVG fragment textures. System fonts
// need no copy; inaccessible or programmatically supplied faces retain the SVG
// presentation instead of silently rasterizing a different fallback typeface.
import {GeometryError} from './geometry.js';
import {bytesToBase64} from './resources.js';
const cache=new Map();let cachedBytes=0,clock=0;
const MAX_FONT_BYTES=4194304,MAX_CACHE_BYTES=25165824,MAX_FACES=64;
function normalized(value){return String(value).trim().replace(/^(['"])([\s\S]*)\1$/,'$2').toLowerCase();}
export function splitFontFamilies(value){
 const result=[];let part='',quote='',escape=false;
 for(const c of String(value)){
  if(escape){part+=c;escape=false;continue;}
  if(c==='\\'){part+=c;escape=true;continue;}
  if(quote){part+=c;if(c===quote)quote='';continue;}
  if(c==='"'||c==="'"){quote=c;part+=c;continue;}
  if(c===','){if(part.trim())result.push(normalized(part));part='';}else part+=c;
 }
 if(part.trim())result.push(normalized(part));return result;
}
function fontRules(svg){
 const rules=[],seen=new Set(),sheets=[...document.styleSheets,...(document.adoptedStyleSheets||[])],root=svg.getRootNode();
 if(root!==document){
  sheets.push(...(root.adoptedStyleSheets||[]));
  for(const node of root.querySelectorAll('style,link[rel="stylesheet"]'))if(node.sheet)sheets.push(node.sheet);
 }
 function read(sheet){
  if(!sheet||seen.has(sheet))return;seen.add(sheet);let entries;
  try{entries=sheet.cssRules;}catch{return;}
  function visit(items){
   for(const rule of items){
    if(rule.type===5){rules.push({family:normalized(rule.style.getPropertyValue('font-family')),style:rule.style,base:sheet.href||document.baseURI});}
    else if(rule.styleSheet)read(rule.styleSheet);
    else if(rule.cssRules)visit(rule.cssRules);
   }
  }visit(entries);
 }
 for(const sheet of sheets)read(sheet);return rules;
}
async function embedUrl(raw,base){
 let url;try{url=new URL(raw,base);}catch{throw new GeometryError('invalid-font-url');}
 if(url.protocol==='data:'){if(raw.length>MAX_FONT_BYTES*1.5)throw new GeometryError('font-byte-budget');return raw;}
 if(!['https:','http:','blob:'].includes(url.protocol))throw new GeometryError('unsupported-font-url');
 const key=url.href;let entry=cache.get(key);
 if(entry){entry.used=++clock;return entry.promise;}
 entry={used:++clock,bytes:0,promise:null};
 entry.promise=(async()=>{
  const response=await fetch(key,{credentials:url.origin===location.origin?'same-origin':'omit',mode:'cors'});
  if(!response.ok)throw new GeometryError('font-fetch-failed');
  const declared=Number(response.headers.get('content-length')||0);if(declared>MAX_FONT_BYTES)throw new GeometryError('font-byte-budget');
  // Stream into a hard byte limit so missing/incorrect Content-Length cannot
  // turn font embedding into an unbounded allocation.
  const reader=response.body?.getReader(),chunks=[];let length=0;
  if(reader){
   try{while(true){const item=await reader.read();if(item.done)break;length+=item.value.length;if(length>MAX_FONT_BYTES){await reader.cancel();throw new GeometryError('font-byte-budget');}chunks.push(item.value);}}
   finally{reader.releaseLock();}
  }else{const bytes=new Uint8Array(await response.arrayBuffer());length=bytes.length;if(length>MAX_FONT_BYTES)throw new GeometryError('font-byte-budget');chunks.push(bytes);}
  const bytes=new Uint8Array(length);let offset=0;for(const chunk of chunks){bytes.set(chunk,offset);offset+=chunk.length;}
  const mime=response.headers.get('content-type')?.split(';')[0]||'application/octet-stream';
  const data='data:'+mime+';base64,'+bytesToBase64(bytes);
  if(cache.get(key)===entry){
   while(cachedBytes+data.length>MAX_CACHE_BYTES||cache.size>64){
    let oldestKey=null,oldest=Infinity;
    for(const [candidateKey,candidate] of cache)if(candidate!==entry&&candidate.used<oldest){oldestKey=candidateKey;oldest=candidate.used;}
    if(oldestKey===null)break;cachedBytes-=cache.get(oldestKey).bytes;cache.delete(oldestKey);
   }
   entry.bytes=data.length;cachedBytes+=entry.bytes;
  }return data;
 })().catch(error=>{if(cache.get(key)===entry){cachedBytes-=entry.bytes;cache.delete(key);}throw error;});
 cache.set(key,entry);return entry.promise;
}
async function embedSources(value,base){
 const matches=Array.from(value.matchAll(/url\(\s*(?:"([^"]*)"|'([^']*)'|([^)]*?))\s*\)/g));let result='',prior=0;
 for(const match of matches){result+=value.slice(prior,match.index)+'url("'+await embedUrl(match[1]??match[2]??match[3],base)+'")';prior=match.index+match[0].length;}
 return result+value.slice(prior);
}
function fontKey(css){
 let a=2166136261,b=5381;for(let i=0;i<css.length;i++){const c=css.charCodeAt(i);a=Math.imul(a^c,16777619);b=Math.imul(b,33)^c;}
 return(a>>>0).toString(36)+(b>>>0).toString(36);
}
export async function embedSvgFonts(svg,snapshot,embeddingCache=new Map()){
 if(!document.fonts||!snapshot.fontFamilies?.length)return snapshot;
 const needed=new Set(snapshot.fontFamilies.flatMap(splitFontFamilies));
 const registered=Array.from(document.fonts).filter(face=>needed.has(normalized(face.family)));
 if(!registered.length)return snapshot;
 const families=new Set(registered.map(face=>normalized(face.family))),key=Array.from(families).sort().join('\0');
 let promise=embeddingCache.get(key);
 if(!promise){
  promise=(async()=>{
   const rules=fontRules(svg).filter(rule=>families.has(rule.family));
   for(const family of families){
    const matching=rules.filter(rule=>rule.family===family);
    if(!matching.length||registered.filter(face=>normalized(face.family)===family).length>matching.length)throw new GeometryError('font-source-unavailable');
   }
   if(rules.length>MAX_FACES)throw new GeometryError('font-face-budget');
   const chunks=[];
   for(const rule of rules){
    const style=rule.style,source=style.getPropertyValue('src');if(!source)throw new GeometryError('font-source-unavailable');
    let css='@font-face{';
    for(const property of Array.from(style)){
     if(property==='font-display')continue;
     const value=property==='src'?await embedSources(source,rule.base):style.getPropertyValue(property);
     css+=property+':'+value+';';
    }chunks.push(css+'font-display:block;}');
   }
   const css=chunks.join('');return{fontCss:css,fontKey:fontKey(css)};
  })();embeddingCache.set(key,promise);
 }
 // The shared CSS string is stored separately from each texture signature:
 // thousands of labels must not duplicate hundreds of KB of embedded fonts.
 return{...snapshot,...await promise};
}
