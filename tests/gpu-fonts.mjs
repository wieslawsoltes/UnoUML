import test from 'node:test';
import assert from 'node:assert/strict';
import {splitFontFamilies,embedSvgFonts} from '../src/UnoUML.App/wwwroot/uml/gpu/fonts.js';

function style(values){
 const names=Object.keys(values);
 return{getPropertyValue:name=>values[name]||'',[Symbol.iterator]:function*(){yield* names;}};
}
async function withFonts(families,rules,run){
 const previous=globalThis.document;
 const doc={fonts:families.map(family=>({family})),styleSheets:[{href:'https://example.test/fonts.css',cssRules:rules}],adoptedStyleSheets:[],baseURI:'https://example.test/'};
 globalThis.document=doc;
 try{return await run({getRootNode:()=>doc},doc);}
 finally{if(previous===undefined)delete globalThis.document;else globalThis.document=previous;}
}
const face=(family,src='url("data:font/woff2;base64,AA==")')=>({type:5,style:style({'font-family':family,src,'font-weight':'100 900','font-style':'normal','font-display':'swap'})});

test('font family parsing preserves quoted commas and case-insensitive names',()=>{
 assert.deepEqual(splitFontFamilies('"Example, Serif", Arial, sans-serif'),['example, serif','arial','sans-serif']);
 assert.deepEqual(splitFontFamilies("'Display Name', serif"),['display name','serif']);
 assert.deepEqual(splitFontFamilies(''),[]);
});
test('system-font-only textures keep their original snapshot without font copies',async()=>{
 const snapshot={xml:'<svg/>',fontFamilies:['Arial, sans-serif']};
 await withFonts([],[],async svg=>assert.equal(await embedSvgFonts(svg,snapshot),snapshot));
});
test('registered faces without inspectable sources fail instead of changing typography',async()=>{
 await withFonts(['Runtime Face'],[],async svg=>{
  await assert.rejects(embedSvgFonts(svg,{xml:'<svg/>',fontFamilies:['Runtime Face']}),/font-source-unavailable/);
 });
});
test('embedded faces preserve variable descriptors and force final font display',async()=>{
 await withFonts(['Variable'],[face('Variable')],async svg=>{
  const cache=new Map(),one=await embedSvgFonts(svg,{xml:'<svg><text>A</text></svg>',fontFamilies:['Variable, serif']},cache);
  const two=await embedSvgFonts(svg,{xml:'<svg><text>B</text></svg>',fontFamilies:['Variable']},cache);
  assert.match(one.fontCss,/font-weight:100 900/);
  assert.match(one.fontCss,/src:url\("data:font\/woff2;base64,AA=="\)/);
  assert.match(one.fontCss,/font-display:block/);
  assert.doesNotMatch(one.fontCss,/font-display:swap/);
  assert.equal(one.xml,'<svg><text>A</text></svg>');
  assert.equal(one.fontCss,two.fontCss);assert.equal(one.fontKey,two.fontKey);assert.equal(cache.size,1);
 });
});
test('ambiguous extra runtime faces do not substitute CSS font sources',async()=>{
 await withFonts(['Custom','Custom'],[face('Custom')],async svg=>{
  await assert.rejects(embedSvgFonts(svg,{xml:'<svg/>',fontFamilies:['Custom']}),/font-source-unavailable/);
 });
});
test('font embedding has a face-count limit before allocating source assets',async()=>{
 const rules=Array.from({length:65},()=>face('Many'));
 await withFonts(Array.from({length:65},()=> 'Many'),rules,async svg=>{
  await assert.rejects(embedSvgFonts(svg,{xml:'<svg/>',fontFamilies:['Many']}),/font-face-budget/);
 });
});
