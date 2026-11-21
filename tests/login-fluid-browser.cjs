// Browser-level regression for the theme-matched login fluid. Run with:
//   npm install --no-save playwright pngjs && npx playwright install chromium
//   node tests/login-fluid-browser.cjs
const assert=require('node:assert/strict');
const fs=require('node:fs');
const path=require('node:path');
const {PNG}=require('pngjs');
const {chromium}=require('playwright');

(async()=>{
 const browser=await chromium.launch({headless:true,args:[
  '--enable-webgl','--use-gl=angle','--use-angle=swiftshader','--enable-unsafe-swiftshader'
 ]});
 try{
  const page=await browser.newPage({viewport:{width:720,height:460},deviceScaleFactor:1});
  const warnings=[];
  page.on('console',m=>{if(m.type()==='warning'||m.type()==='error')warnings.push(m.text());});
  page.on('pageerror',e=>warnings.push(String(e)));
  await page.setContent(`<!doctype html><html><style>
    *{box-sizing:border-box}
    html,body{width:100%;height:100%;margin:0;overflow:hidden}
    body{--bg:#201329;--accent:#bd88ff;--active-border:#a06fe2;--muted:#baa9d1}
    #auth{position:relative;width:100%;height:100%;background:linear-gradient(130deg,#352046,#201329,#49315b)}
    #loginAurora{position:absolute;inset:0;width:100%;height:100%;pointer-events:none}
    </style><body data-theme="purple"><div id="auth"><canvas id="loginAurora"></canvas></div></body></html>`);
  await page.addScriptTag({content:fs.readFileSync(path.join(__dirname,'../server/Inventory.Api/wwwroot/assets/login-fluid.js'),'utf8')});
  const support=await page.evaluate(()=>{
    const c=document.getElementById('loginAurora');
    const gl=c.getContext('webgl2');
    return !!gl && !!gl.getExtension('EXT_color_buffer_float');
  });
  assert.ok(support,'Headless Chromium must support WebGL2 float render targets');
  await page.evaluate(()=>{
    window.testFluid=window.createLoginFluid(document.getElementById('loginAurora'),document.getElementById('auth'));
    window.testFluid.start();
  });
  await page.waitForTimeout(180);
  assert.ok(!warnings.some(t=>/shader|framebuffer|unavailable|GLSL|SyntaxError/i.test(t)),
    'Fluid shader failed to initialize: '+warnings.join(' / '));
  const before=PNG.sync.read(await page.locator('#loginAurora').screenshot());
  await page.mouse.move(70,130);
  for(let i=0;i<9;i++)await page.mouse.move(90+i*55,130+Math.sin(i*.6)*55);
  await page.waitForTimeout(180);
  const after=PNG.sync.read(await page.locator('#loginAurora').screenshot());
  let changed=0,drawn=0;
  for(let i=0;i<before.data.length;i+=4){
    const d=Math.abs(before.data[i]-after.data[i])+Math.abs(before.data[i+1]-after.data[i+1])+Math.abs(before.data[i+2]-after.data[i+2]);
    if(d>15)changed++;
    if(after.data[i+3]>12)drawn++;
  }
  assert.ok(changed>450,`Mouse hover should visibly disturb rendered fluid (changed pixels: ${changed})`);
  assert.ok(drawn>300,'Fluid color must be drawn to the canvas');
  await page.screenshot({path:'/tmp/login-fluid-purple.png'});
  await page.evaluate(()=>{
    window.testFluid.stop();
    document.body.dataset.theme='glacier';
    document.body.style.setProperty('--bg','#f3f7fc');
    document.body.style.setProperty('--accent','#2d8fca');
    window.testFluid.start();
  });
  await page.mouse.move(130,240);
  for(let i=0;i<7;i++)await page.mouse.move(130+i*55,240+Math.cos(i*.6)*50);
  await page.waitForTimeout(160);
  await page.screenshot({path:'/tmp/login-fluid-glacier.png'});
  assert.ok(!warnings.some(t=>/shader|framebuffer|unavailable|GLSL|SyntaxError/i.test(t)),
   'Fluid encountered a WebGL error: '+warnings.join(' / '));
  console.log('Browser fluid smoke verified: hover modifies '+changed+' pixels; purple + glacier canvases rendered.');
 }finally{await browser.close();}
})().catch(err=>{console.error(err);process.exitCode=1;});