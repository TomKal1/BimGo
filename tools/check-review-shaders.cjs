// Run: node tools/check-review-shaders.cjs [output-directory]. Set CHROME_PATH for a different Chromium executable.
// Local headless WebGL2 regression: grid width is independent of distance in parallel views; cuts clip the line.
const fs = require('node:fs');
const cp = require('node:child_process');
const path = require('node:path');
const root = process.argv[2] ? path.resolve(process.argv[2]) : fs.mkdtempSync(path.join(require('node:os').tmpdir(), 'bimgo-shaders-'));
fs.mkdirSync(root, { recursive: true });
const source = fs.readFileSync(path.resolve(__dirname, '../src/BimGo.App/Rendering/Shaders.cs'), 'utf8');
function shader(name) {
  const m = source.match(new RegExp('public const string ' + name + ' = @"([\\s\\S]*?)";'));
  if (!m) throw Error('Missing shader ' + name);
  return m[1].replace('#version 330 core', '#version 300 es\nprecision highp float;\nprecision highp int;');
}
const shaders = Object.fromEntries(['GRID_VS','GRID_FS','CALC_VS','CALC_FS'].map(n => [n, shader(n)]));
const body = `
const sources = ${JSON.stringify(shaders)};
try {
const canvas = document.createElement('canvas'); canvas.width=canvas.height=128;
const gl=canvas.getContext('webgl2',{antialias:false,preserveDrawingBuffer:true});
if(!gl) throw Error('WebGL2 unavailable');
function program(v,f){
 const p=gl.createProgram();
 for(const [t,s] of [[gl.VERTEX_SHADER,v],[gl.FRAGMENT_SHADER,f]]){
  const x=gl.createShader(t);gl.shaderSource(x,s);gl.compileShader(x);
  if(!gl.getShaderParameter(x,gl.COMPILE_STATUS))throw Error(gl.getShaderInfoLog(x));gl.attachShader(p,x);
 }
 gl.linkProgram(p);if(!gl.getProgramParameter(p,gl.LINK_STATUS))throw Error(gl.getProgramInfoLog(p));return p;
}
program(sources.CALC_VS,sources.CALC_FS);
const p=program(sources.GRID_VS,sources.GRID_FS);gl.useProgram(p);
const u=n=>gl.getUniformLocation(p,n);
gl.uniform1f(u('uAlpha'),1);gl.uniform1i(u('uDashed'),0);
gl.uniform3f(u('uEye'),0,0,0);gl.uniform3f(u('uForward'),0,0,-1);
gl.uniform1i(u('uOrthographic'),1);gl.uniform1f(u('uHalfWidth'),20/128);
gl.uniformMatrix4fv(u('uViewProj'),false,new Float32Array([.1,0,0,0,0,.1,0,0,0,0,0,0,0,0,0,1]));
const vao=gl.createVertexArray();gl.bindVertexArray(vao);
const buf=gl.createBuffer();gl.bindBuffer(gl.ARRAY_BUFFER,buf);
for(const [loc,size,off]of [[0,3,0],[1,3,12],[2,1,24],[3,1,28]]){gl.enableVertexAttribArray(loc);gl.vertexAttribPointer(loc,size,gl.FLOAT,false,32,off);}
gl.disableVertexAttribArray(4);gl.vertexAttrib4f(4,1,1,1,1);
gl.viewport(0,0,128,128);gl.clearColor(0,0,0,1);
function draw(depth,clip){
 const verts=[];for(const [x,s]of [[-2,-1],[2,-1],[2,1],[-2,-1],[2,1],[-2,1]])verts.push(x,0,-depth,1,0,0,0,s);
 gl.bufferData(gl.ARRAY_BUFFER,new Float32Array(verts),gl.STATIC_DRAW);
 gl.uniform1i(u('uSectionCount'),clip?1:0);if(clip)gl.uniform4f(u('uSectionPlanes[0]'),1,0,0,-.5);
 gl.clear(gl.COLOR_BUFFER_BIT);gl.drawArrays(gl.TRIANGLES,0,6);
 const pix=new Uint8Array(128*128*4);gl.readPixels(0,0,128,128,gl.RGBA,gl.UNSIGNED_BYTE,pix);
 let rows=0,left=0;for(let y=0;y<128;y++){if(pix[(y*128+64)*4]>100)rows++;if(pix[(y*128+55)*4]>100)left++;}
 return {rows,left};
}
const near=draw(10,false),far=draw(300,false),clipped=draw(300,true);
if(near.rows!==2||far.rows!==2)throw Error('Orthographic grid width changes with eye distance: '+JSON.stringify({near,far}));
if(clipped.rows!==0||clipped.left!==2)throw Error('Grid section clipping failed: '+JSON.stringify(clipped));
if(gl.getError()!==gl.NO_ERROR)throw Error('Unexpected GL error');
document.body.textContent='PASS '+JSON.stringify({gridRowsAt10m:near.rows,gridRowsAt300m:far.rows,clipped,shaderPairsLinked:2});
}catch(e){document.body.textContent='FAIL '+e.stack;}
`;
const html=path.join(root,'grid-shader-test.html');
fs.writeFileSync(html,'<!doctype html><body><script>'+body+'</script>');
const result=cp.spawnSync(process.env.CHROME_PATH || 'C:/Program Files/Google/Chrome/Application/chrome.exe',[
 '--headless=new','--no-first-run','--no-default-browser-check','--disable-background-networking',
 '--use-angle=swiftshader','--enable-unsafe-swiftshader','--user-data-dir='+path.join(root,'shader-browser-profile'),
 '--dump-dom','file:///'+html.replaceAll('\\','/')
],{encoding:'utf8',windowsHide:true,timeout:45000,maxBuffer:2*1024*1024});
if(result.error)throw result.error;
const match=result.stdout?.match(/<body>(PASS[\s\S]*?)<\/body>/);
if(!match){console.error(JSON.stringify({status:result.status,signal:result.signal})+'\n'+(result.stdout||'')+'\n'+(result.stderr||'').slice(-1500));process.exit(1);}
console.log(match[1]);
