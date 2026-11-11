import assert from 'node:assert/strict';
import { readFile, mkdir, writeFile } from 'node:fs/promises';
import { createServer } from 'node:http';
import { resolve } from 'node:path';
import { chromium } from 'playwright';
import { graphicsLaunchOptions } from './graphics-options.mjs';

const source = await readFile('src/UnoUML.App/Rendering/Gpu/DiagramGpuShaders.cs', 'utf8');
const shader = source.split('"""')[1];
assert.ok(shader?.includes('@fragment fn image_fragment'), 'Extract the actual production WGSL module.');
const server = createServer((_request, response) => {
  response.writeHead(200, {'Content-Type':'text/html'});
  response.end('<!doctype html><title>UnoUML shader validation</title>');
});
await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
const browser = await chromium.launch(graphicsLaunchOptions(true));
try {
  const page = await browser.newPage();
  await page.goto('http://127.0.0.1:' + server.address().port);
  const report = await page.evaluate(async shader => {
    const adapter = await navigator.gpu?.requestAdapter();
    if (!adapter) throw new Error('This check requires a real WebGPU API adapter.');
    const device = await adapter.requestDevice();
    const failures = [];
    device.addEventListener('uncapturederror', event => failures.push(event.error.message));
    const resources = [];
    const own = resource => { resources.push(resource); return resource; };
    const buffer = (data, usage) => {
      const value = own(device.createBuffer({size:data.byteLength, usage:usage|GPUBufferUsage.COPY_DST}));
      device.queue.writeBuffer(value, 0, data); return value;
    };
    try {
      const module = device.createShaderModule({code:shader});
      const info = await module.getCompilationInfo();
      const errors = info.messages.filter(message => message.type === 'error');
      if (errors.length) throw new Error(errors.map(message => message.lineNum + ':' + message.linePos + ' ' + message.message).join('\n'));
      const layout = device.createBindGroupLayout({entries:[
        {binding:0, visibility:GPUShaderStage.VERTEX|GPUShaderStage.FRAGMENT, buffer:{type:'uniform', minBindingSize:64}},
        {binding:1, visibility:GPUShaderStage.VERTEX|GPUShaderStage.FRAGMENT, buffer:{type:'uniform', hasDynamicOffset:true, minBindingSize:256}},
        {binding:2, visibility:GPUShaderStage.FRAGMENT, buffer:{type:'read-only-storage', minBindingSize:32}},
        {binding:3, visibility:GPUShaderStage.FRAGMENT, texture:{sampleType:'float', viewDimension:'2d'}},
        {binding:4, visibility:GPUShaderStage.FRAGMENT, sampler:{type:'filtering'}}
      ]});
      const pipelineLayout = device.createPipelineLayout({bindGroupLayouts:[layout]});
      const pipelines = new Map();
      const definitions = [
        ['background','background_vertex','background_fragment',false,0],
        ['nonzero','mesh_vertex','stencil_fragment',true,1],
        ['evenodd','mesh_vertex','stencil_fragment',true,2],
        ['mesh','mesh_vertex','stencil_fragment',true,3],
        ['paint','cover_vertex','paint_fragment',false,4],
        ['image','cover_vertex','image_fragment',false,0]
      ];
      device.pushErrorScope('validation');
      for (const [name, vertex, fragment, mesh, mode] of definitions) {
        const stencilOnly = mode >= 1 && mode <= 3;
        const front = {compare:mode === 4 ? 'not-equal' : mode === 3 ? 'equal' : 'always', failOp:'keep', depthFailOp:'keep',
          passOp:mode === 1 ? 'increment-wrap' : mode === 2 ? 'invert' : mode === 3 ? 'increment-clamp' : mode === 4 ? 'zero' : 'keep'};
        const back = {...front, passOp:mode === 1 ? 'decrement-wrap' : front.passOp};
        pipelines.set(name, await device.createRenderPipelineAsync({
          layout:pipelineLayout,
          vertex:{module, entryPoint:vertex, buffers:mesh ? [{arrayStride:8, attributes:[{shaderLocation:0,offset:0,format:'float32x2'}]}] : []},
          fragment:{module, entryPoint:fragment, targets:[{format:'rgba8unorm',writeMask:stencilOnly ? 0 : GPUColorWrite.ALL,
            ...(stencilOnly ? {} : {blend:{color:{srcFactor:'one',dstFactor:'one-minus-src-alpha',operation:'add'},
              alpha:{srcFactor:'one',dstFactor:'one-minus-src-alpha',operation:'add'}}})}]},
          primitive:{topology:'triangle-list',frontFace:'ccw',cullMode:'none'},
          depthStencil:{format:'depth24plus-stencil8',depthWriteEnabled:false,depthCompare:'always',
            stencilFront:front,stencilBack:back,stencilReadMask:255,stencilWriteMask:mode === 0 ? 0 : 255},
          multisample:{count:4}
        }));
      }
      const pipelineError = await device.popErrorScope();
      if (pipelineError) throw new Error(pipelineError.message);

      const globals = buffer(new Float32Array([1,0,0,1,0,0,64,64,64,64,0,0,0,0,0,0]), GPUBufferUsage.UNIFORM);
      const sampler = device.createSampler({magFilter:'linear',minFilter:'linear'});
      const target = own(device.createTexture({size:[64,64],format:'rgba8unorm',usage:GPUTextureUsage.RENDER_ATTACHMENT|GPUTextureUsage.COPY_SRC}));
      const samples = own(device.createTexture({size:[64,64],sampleCount:4,format:'rgba8unorm',usage:GPUTextureUsage.RENDER_ATTACHMENT}));
      const stencil = own(device.createTexture({size:[64,64],sampleCount:4,format:'depth24plus-stencil8',usage:GPUTextureUsage.RENDER_ATTACHMENT}));
      const readback = own(device.createBuffer({size:64*64*4,usage:GPUBufferUsage.COPY_DST|GPUBufferUsage.MAP_READ}));
      const rect = (x,y,w,h) => [x,y,x+w,y,x,y+h,x,y+h,x+w,y,x+w,y+h];
      async function draw(specifications, background = false) {
        device.pushErrorScope('validation');
        const commands = specifications.map(specification => {
          const data = new Float32Array(64), m = specification.matrix || [1,0,0,1,0,0];
          data.set(m.slice(0,4),0);data.set(m.slice(4),4);
          data[6] = specification.paint === 'linear' ? 1 : specification.paint === 'radial' ? 2 : 0;
          data.set(specification.bounds || [0,0,64,64],8);
          data.set(specification.color || [1,0,0,1],12);
          data.set([1,0,0,1],16); data[22] = specification.opacity ?? 1;
          data.set(specification.gradient || [0,0,64,0],24);
          data.set(specification.radial || [32,32,32,32],28);data[32]=32;
          const stops = specification.stops || [[0,[1,0,0,1]],[1,[0,0,1,1]]];
          data[34] = stops.length;
          if (specification.clip) data.set(specification.clip,36); else data[38]=-1;
          const stopData = new Float32Array(stops.length*8);
          stops.forEach(([offset,color],index) => { stopData[index*8]=offset;stopData.set(color,index*8+4); });
          const drawBuffer = buffer(data,GPUBufferUsage.UNIFORM);
          const stopBuffer = buffer(stopData,GPUBufferUsage.STORAGE);
          const pixels = specification.pixels || [255,255,255,255];
          const texture = own(device.createTexture({size:[pixels.length/4,1],format:'rgba8unorm',usage:GPUTextureUsage.TEXTURE_BINDING|GPUTextureUsage.COPY_DST}));
          device.queue.writeTexture({texture},new Uint8Array(pixels),{bytesPerRow:pixels.length},[pixels.length/4,1]);
          const bindings = device.createBindGroup({layout,entries:[
            {binding:0,resource:{buffer:globals,size:64}},
            {binding:1,resource:{buffer:drawBuffer,size:256}},
            {binding:2,resource:{buffer:stopBuffer}},
            {binding:3,resource:texture.createView()},
            {binding:4,resource:sampler}
          ]});
          const vertices = specification.vertices || rect(0,0,64,64);
          return {...specification,bindings,vertexBuffer:buffer(new Float32Array(vertices),GPUBufferUsage.VERTEX),vertexCount:vertices.length/2};
        });
        const encoder = device.createCommandEncoder();
        const pass = encoder.beginRenderPass({
          colorAttachments:[{view:samples.createView(),resolveTarget:target.createView(),loadOp:'clear',storeOp:'discard',clearValue:[1,1,1,1]}],
          depthStencilAttachment:{view:stencil.createView(),depthLoadOp:'clear',depthStoreOp:'discard',depthClearValue:1,
            stencilLoadOp:'clear',stencilStoreOp:'discard',stencilClearValue:0}
        });
        if (background) {
          pass.setBindGroup(0,commands[0].bindings,[0]);pass.setPipeline(pipelines.get('background'));pass.draw(3);
        } else {
          pass.setStencilReference(0);
          const bundles = commands.map(command => {
            const bundle = device.createRenderBundleEncoder({
              colorFormats:['rgba8unorm'],depthStencilFormat:'depth24plus-stencil8',sampleCount:4
            });
            bundle.setBindGroup(0,command.bindings,[0]);
            if (command.kind === 'image') { bundle.setPipeline(pipelines.get('image'));bundle.draw(6); }
            else {
              bundle.setVertexBuffer(0,command.vertexBuffer);
              bundle.setPipeline(pipelines.get(command.kind || 'nonzero'));bundle.draw(command.vertexCount);
              bundle.setPipeline(pipelines.get('paint'));bundle.draw(6);
            }
            return bundle.finish();
          });
          pass.executeBundles(bundles);
        }
        pass.end();
        encoder.copyTextureToBuffer({texture:target},{buffer:readback,bytesPerRow:256},[64,64]);
        device.queue.submit([encoder.finish()]);
        await readback.mapAsync(GPUMapMode.READ);
        const pixels = new Uint8Array(readback.getMappedRange()).slice();
        readback.unmap();
        const error = await device.popErrorScope();
        if (error) throw new Error(error.message);
        return (x,y) => Array.from(pixels.slice((y*64+x)*4,(y*64+x)*4+4));
      }

      const checks = [];
      function pixel(name, actual, expected, tolerance = 3) {
        const pass = actual.every((value,index) => Math.abs(value-expected[index])<=tolerance);
        checks.push({name,actual,expected,tolerance,pass});
        if (!pass) throw new Error(name + ': ' + actual + ' expected ' + expected);
      }
      let sample = await draw([{}]);
      pixel('Solid winding fill',sample(32,32),[255,0,0,255]);
      const ring = [...rect(4,4,56,56),...rect(20,20,24,24)];
      sample = await draw([{kind:'evenodd',vertices:ring}]);
      pixel('Evenodd filled ring',sample(10,10),[255,0,0,255]);
      pixel('Evenodd hole',sample(32,32),[255,255,255,255]);
      const reverse = [];
      const inside = rect(20,20,24,24);
      for(let i=0;i<inside.length;i+=6) reverse.push(...inside.slice(i,i+2),...inside.slice(i+4,i+6),...inside.slice(i+2,i+4));
      sample = await draw([{vertices:[...rect(4,4,56,56),...reverse]}]);
      pixel('Nonzero opposite-winding hole',sample(32,32),[255,255,255,255]);
      sample = await draw([{kind:'mesh',vertices:[...rect(4,20,56,20),...rect(20,4,20,56)],opacity:.5}]);
      pixel('Stroke union blends once at overlaps',sample(28,28),[255,128,128,255]);
      sample = await draw([{paint:'linear'}]);
      pixel('Linear gradient shader',sample(16,32),[189,0,66,255],4);
      sample = await draw([{paint:'radial'}]);
      pixel('Radial gradient shader center',sample(32,32),[249,0,6,255],4);
      sample = await draw([{kind:'image',pixels:[0,255,0,255,0,0,0,0]}]);
      pixel('Premultiplied image filtering avoids dark fringes',sample(32,32),[131,255,131,255],4);
      sample = await draw([{clip:[8,8,16,16]},{color:[0,1,0,1],bounds:[40,40,16,16],vertices:rect(40,40,16,16)}]);
      pixel('World clip retains interior',sample(12,12),[255,0,0,255]);
      pixel('World clip clears stencil outside cover',sample(32,32),[255,255,255,255]);
      pixel('Later draw is independent of clipped stencil',sample(48,48),[0,255,0,255]);
      sample = await draw([{bounds:[0,0,16,24],vertices:rect(0,0,16,24),matrix:[2,0,0,.5,4,10],color:[0,1,0,1]}]);
      pixel('Affine transform',sample(10,14),[0,255,0,255]);
      pixel('Affine bounds',sample(48,14),[255,255,255,255]);
      sample = await draw([{}],true);
      pixel('Grid background',sample(2,2),[247,248,251,255]);
      pixel('Grid dot is centered in the 16px CSS tile',sample(8,8),[215,219,229,255],8);
      if (failures.length) throw new Error(failures.join('\n'));
      const adapterInfo = adapter.info;
      return {
        shaderBytes:shader.length,pipelines:pipelines.size,samples:4,retainedBundles:true,checks,
        adapter:{vendor:adapterInfo.vendor,architecture:adapterInfo.architecture,device:adapterInfo.device,description:adapterInfo.description},
        browser:navigator.userAgent,purpose:'Actual production WGSL and offscreen pixel correctness; not a hardware benchmark.'
      };
    } finally {
      resources.forEach(resource => resource.destroy());
      device.destroy();
    }
  }, shader);
  assert.equal(report.pipelines,6);
  assert.ok(report.checks.length >= 15);
  assert.ok(report.checks.every(check=>check.pass));
  console.log(JSON.stringify(report,null,2));
  if (process.env.SCREENSHOT_DIR) {
    await mkdir(process.env.SCREENSHOT_DIR,{recursive:true});
    await writeFile(resolve(process.env.SCREENSHOT_DIR,'gpu-shaders.json'),JSON.stringify(report,null,2));
  }
} finally {
  await browser.close();
  await new Promise(resolve=>server.close(resolve));
}
