/* Interactive theme-driven login fluid. No external runtime dependencies.
 * WebGL2 incompressible velocity + pressure projection + vorticity, dye advection.
 * Transparent to all pointer events; falls back to a soft static CSS background.
 */
(function () {
  'use strict';
  const VERTEX = [
    '#version 300 es',
    'precision highp float;',
    'out vec2 uv;',
    'void main(){',
    '  vec2 p=vec2(gl_VertexID==2?3.0:-1.0,gl_VertexID==1?3.0:-1.0);',
    '  uv=p*.5+.5; gl_Position=vec4(p,0.0,1.0);',
    '}'
  ].join('\n');
  const HEAD = '#version 300 es\nprecision highp float;\nin vec2 uv;\nout vec4 frag;\n';
  const SHADERS = {
    advect: [
      'uniform sampler2D velocity,source;',
      'uniform vec2 sourceSize;',
      'uniform float dt,fade;',
      'vec4 sampleLinear(sampler2D field,vec2 p){',
      ' vec2 xy=clamp(p,vec2(0.0),vec2(1.0))*sourceSize-.5;',
      ' ivec2 lower=ivec2(floor(xy));vec2 t=fract(xy);',
      ' ivec2 dim=ivec2(sourceSize);',
      ' ivec2 a=clamp(lower,ivec2(0),dim-1);',
      ' ivec2 b=clamp(lower+ivec2(1,0),ivec2(0),dim-1);',
      ' ivec2 c=clamp(lower+ivec2(0,1),ivec2(0),dim-1);',
      ' ivec2 d=clamp(lower+ivec2(1,1),ivec2(0),dim-1);',
      ' return mix(mix(texelFetch(field,a,0),texelFetch(field,b,0),t.x),',
      '            mix(texelFetch(field,c,0),texelFetch(field,d,0),t.x),t.y);',
      '}',
      'void main(){',
      ' vec2 backwards=uv-dt*texture(velocity,uv).xy;',
      ' frag=sampleLinear(source,backwards)*fade;',
      '}'
    ],
    splat: [
      'uniform sampler2D target;',
      'uniform vec2 point;',
      'uniform vec3 value;',
      'uniform float radius,aspect;',
      'void main(){',
      ' vec2 d=uv-point;d.x*=aspect;',
      ' float falloff=exp(-dot(d,d)/max(radius*radius,.000001));',
      ' frag=clamp(texture(target,uv)+vec4(value*falloff,0.0),vec4(-2,-2,-2,0),vec4(2,2,2,1));',
      '}'
    ],
    curl: [
      'uniform sampler2D velocity;',
      'uniform vec2 stepSize;',
      'void main(){',
      ' float l=texture(velocity,uv-vec2(stepSize.x,0)).y;',
      ' float r=texture(velocity,uv+vec2(stepSize.x,0)).y;',
      ' float b=texture(velocity,uv-vec2(0,stepSize.y)).x;',
      ' float t=texture(velocity,uv+vec2(0,stepSize.y)).x;',
      ' frag=vec4(r-l-t+b,0,0,1);',
      '}'
    ],
    vorticity: [
      'uniform sampler2D velocity,curlField;',
      'uniform vec2 stepSize;',
      'uniform float dt;',
      'void main(){',
      ' float l=abs(texture(curlField,uv-vec2(stepSize.x,0)).x);',
      ' float r=abs(texture(curlField,uv+vec2(stepSize.x,0)).x);',
      ' float b=abs(texture(curlField,uv-vec2(0,stepSize.y)).x);',
      ' float t=abs(texture(curlField,uv+vec2(0,stepSize.y)).x);',
      ' vec2 grad=vec2(t-b,r-l);',
      ' grad/=length(grad)+.00001;',
      ' float spin=texture(curlField,uv).x;',
      ' vec2 force=vec2(grad.x,-grad.y)*spin*dt*5.5;',
      ' vec2 v=clamp(texture(velocity,uv).xy+force,vec2(-3),vec2(3));',
      ' frag=vec4(v,0,1);',
      '}'
    ],
    divergence: [
      'uniform sampler2D velocity;',
      'uniform vec2 stepSize;',
      'void main(){',
      ' vec2 l=texture(velocity,uv-vec2(stepSize.x,0)).xy;',
      ' vec2 r=texture(velocity,uv+vec2(stepSize.x,0)).xy;',
      ' vec2 b=texture(velocity,uv-vec2(0,stepSize.y)).xy;',
      ' vec2 t=texture(velocity,uv+vec2(0,stepSize.y)).xy;',
      ' frag=vec4((r.x-l.x+t.y-b.y)*.5,0,0,1);',
      '}'
    ],
    pressure: [
      'uniform sampler2D pressureField,divergenceField;',
      'uniform vec2 stepSize;',
      'void main(){',
      ' float l=texture(pressureField,uv-vec2(stepSize.x,0)).x;',
      ' float r=texture(pressureField,uv+vec2(stepSize.x,0)).x;',
      ' float b=texture(pressureField,uv-vec2(0,stepSize.y)).x;',
      ' float t=texture(pressureField,uv+vec2(0,stepSize.y)).x;',
      ' float div=texture(divergenceField,uv).x;',
      ' frag=vec4((l+r+b+t-div)*.25,0,0,1);',
      '}'
    ],
    subtract: [
      'uniform sampler2D pressureField,velocity;',
      'uniform vec2 stepSize;',
      'void main(){',
      ' float l=texture(pressureField,uv-vec2(stepSize.x,0)).x;',
      ' float r=texture(pressureField,uv+vec2(stepSize.x,0)).x;',
      ' float b=texture(pressureField,uv-vec2(0,stepSize.y)).x;',
      ' float t=texture(pressureField,uv+vec2(0,stepSize.y)).x;',
      ' vec2 v=texture(velocity,uv).xy-vec2(r-l,t-b)*.5;',
      ' frag=vec4(v,0,1);',
      '}'
    ],
    display: [
      'uniform sampler2D dye;',
      'uniform float lightMode;',
      'void main(){',
      ' uniform vec2 sourceSize;',
      ' vec4 smoothDye(vec2 p){',
      '   vec2 coord=p*sourceSize-.5;',
      '   ivec2 lo=ivec2(floor(coord));vec2 f=fract(coord);',
      '   ivec2 edge=ivec2(sourceSize)-1;',
      '   vec3 a=texelFetch(dye,clamp(lo,ivec2(0),edge),0).rgb;',
      '   vec3 b=texelFetch(dye,clamp(lo+ivec2(1,0),ivec2(0),edge),0).rgb;',
      '   vec3 c=texelFetch(dye,clamp(lo+ivec2(0,1),ivec2(0),edge),0).rgb;',
      '   vec3 d=texelFetch(dye,clamp(lo+ivec2(1,1),ivec2(0),edge),0).rgb;',
      '   return vec4(mix(mix(a,b,f.x),mix(c,d,f.x),f.y),1.0);',
      ' }',
      'void main(){',
      ' vec3 ink=max(smoothDye(uv).rgb,vec3(0));',
      ' float energy=max(ink.r,max(ink.g,ink.b));',
      ' float alpha=clamp((1.0-exp(-energy*1.7))*(lightMode>.5?.46:.68),0.0,.72);',
      ' vec3 color=clamp(ink/(energy+.08),vec3(0),vec3(1));',
      ' frag=vec4(color,alpha);',
      '}'
    ]
  };

  function hexColor(value) {
    const match=/^#([0-9a-f]{6})$/i.exec(String(value||'').trim());
    if(!match)return [0.5,0.7,0.85];
    return [0,2,4].map(i=>parseInt(match[1].slice(i,i+2),16)/255);
  }
  function richer(rgb,factor) {
    return rgb.map(x=>Math.min(1,x*factor+.045));
  }
  function create(canvas,root) {
    const reduce=window.matchMedia('(prefers-reduced-motion: reduce)');
    const gl=canvas.getContext('webgl2',{alpha:true,premultipliedAlpha:false,antialias:false,depth:false,stencil:false,preserveDrawingBuffer:false,powerPreference:'low-power'});
    let ready=false,active=false,raf=0,last=0,width=0,height=0,simW=0,simH=0,dyeW=0,dyeH=0,colors=[],pointer=null,queue=[],step=0,inkBudget=1.05,lastBudget=0,lastSample=0;
    let targets=null,programs=null;
    function theme(){
      const css=getComputedStyle(document.body);
      const a=hexColor(css.getPropertyValue('--accent'));
      const b=hexColor(css.getPropertyValue('--active-border'));
      const c=hexColor(css.getPropertyValue('--muted'));
      // Saturated companion inks stay within each chosen theme's visual family.
      const inkColors={
        mint:['#8df9c0','#6ad8dd','#b9f7a2'],
        purple:['#bd88ff','#e57add','#8293ff'],
        ocean:['#5bd8ff','#4be8d2','#87a9ff'],
        ember:['#ffb471','#ff846f','#f5cb80'],
        rose:['#f6a1d1','#bf89ed','#ff8fa7'],
        slate:['#a6cefa','#7cb1d9','#c9d9f6'],
        forest:['#abed86','#62d5a7','#d5e98d'],
        gold:['#f6d889','#d7aa59','#fff0b1'],
        crimson:['#ff8ba6','#d95b81','#ffb5a3'],
        glacier:['#2d8fca','#65b7e6','#9dd2ee']
      };
      const chosen=inkColors[document.body.dataset.theme];
      colors=chosen?chosen.map(hexColor):[richer(a,1.1),richer(b,1.45),richer(c,1.32)];
      if(targets)clearTargets();
    }
    if(gl && gl.getExtension('EXT_color_buffer_float')) {
      try{
        function shader(type,body){
          const obj=gl.createShader(type);
          gl.shaderSource(obj,body);gl.compileShader(obj);
          if(!gl.getShaderParameter(obj,gl.COMPILE_STATUS))throw new Error(gl.getShaderInfoLog(obj));
          return obj;
        }
        const vert=shader(gl.VERTEX_SHADER,VERTEX);
        programs={};
        for(const [key,source] of Object.entries(SHADERS)){
          const frag=shader(gl.FRAGMENT_SHADER,HEAD+source.join('\n'));
          const program=gl.createProgram();
          gl.attachShader(program,vert);gl.attachShader(program,frag);gl.linkProgram(program);
          if(!gl.getProgramParameter(program,gl.LINK_STATUS))throw new Error(gl.getProgramInfoLog(program));
          programs[key]=program;
          gl.deleteShader(frag);
        }
        gl.deleteShader(vert);
        ready=true;
      }catch(e){console.warn('Login fluid disabled: shader initialization failed',e);}
    }
    function frameBuffer(w,h){
      const texture=gl.createTexture();
      gl.bindTexture(gl.TEXTURE_2D,texture);
      gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_MIN_FILTER,gl.NEAREST);
      gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_MAG_FILTER,gl.NEAREST);
      gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_WRAP_S,gl.CLAMP_TO_EDGE);
      gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_WRAP_T,gl.CLAMP_TO_EDGE);
      gl.texImage2D(gl.TEXTURE_2D,0,gl.RGBA16F,w,h,0,gl.RGBA,gl.HALF_FLOAT,null);
      const fb=gl.createFramebuffer();
      gl.bindFramebuffer(gl.FRAMEBUFFER,fb);
      gl.framebufferTexture2D(gl.FRAMEBUFFER,gl.COLOR_ATTACHMENT0,gl.TEXTURE_2D,texture,0);
      if(gl.checkFramebufferStatus(gl.FRAMEBUFFER)!==gl.FRAMEBUFFER_COMPLETE)throw new Error('Float framebuffer unsupported');
      return {texture,fb,w,h};
    }
    function pair(w,h) {
      return {a:frameBuffer(w,h),b:frameBuffer(w,h),swap(){const t=this.a;this.a=this.b;this.b=t;}};
    }
    function cleanup(){
      if(!targets)return;
      const all=[targets.velocity.a,targets.velocity.b,targets.dye.a,targets.dye.b,targets.pressure.a,targets.pressure.b,targets.divergence,targets.curl];
      for(const t of all){gl.deleteFramebuffer(t.fb);gl.deleteTexture(t.texture);}
      targets=null;
    }
    function clearTargets(){
      if(!targets)return;
      for(const t of [targets.velocity.a,targets.velocity.b,targets.dye.a,targets.dye.b,targets.pressure.a,targets.pressure.b,targets.divergence,targets.curl]){
        gl.bindFramebuffer(gl.FRAMEBUFFER,t.fb);
        gl.viewport(0,0,t.w,t.h);
        gl.clearColor(0,0,0,0);gl.clear(gl.COLOR_BUFFER_BIT);
      }
    }
    function resize(){
      if(!ready)return;
      const bounds=canvas.getBoundingClientRect();
      width=bounds.width;height=bounds.height;
      if(width<1||height<1)return;
      const dpr=Math.min(window.devicePixelRatio||1,2);
      canvas.width=Math.max(1,Math.round(width*dpr));canvas.height=Math.max(1,Math.round(height*dpr));
      const ratio=width/height;
      const compact=Math.min(width,height)<600;
      simH=compact?118:180;simW=Math.max(60,Math.round(simH*ratio));
      // Higher-resolution dye improves edge fidelity without full-resolution pressure solve.
      dyeH=compact?440:720;dyeW=Math.max(150,Math.min(1280,Math.round(dyeH*ratio)));
      cleanup();
      try{
        targets={
          velocity:pair(simW,simH),dye:pair(dyeW,dyeH),
          pressure:pair(simW,simH),
          divergence:frameBuffer(simW,simH),curl:frameBuffer(simW,simH)
        };
        clearTargets();
      }catch(err){
        console.warn('Login fluid unavailable on this GPU',err);
        cleanup();ready=false;canvas.style.display='none';
      }
    }
    function pass(name,out,textures,uniforms={}){
      const p=programs[name];
      gl.useProgram(p);
      gl.bindFramebuffer(gl.FRAMEBUFFER,out?out.fb:null);
      gl.viewport(0,0,out?out.w:canvas.width,out?out.h:canvas.height);
      let unit=0;
      for(const [key,source] of Object.entries(textures)){
        gl.activeTexture(gl.TEXTURE0+unit);
        gl.bindTexture(gl.TEXTURE_2D,source.texture);
        gl.uniform1i(gl.getUniformLocation(p,key),unit++);
      }
      for(const [key,value] of Object.entries(uniforms)){
        const loc=gl.getUniformLocation(p,key);
        if(loc===null)continue;
        if(Array.isArray(value)){
          if(value.length===2)gl.uniform2fv(loc,value);
          else if(value.length===3)gl.uniform3fv(loc,value);
        }else gl.uniform1f(loc,value);
      }
      gl.drawArrays(gl.TRIANGLES,0,3);
    }
    function splat(x,y,vx,vy,col,radius=.065){
      if(!targets)return;
      const aspect=width/height,point=[x,y];
      pass('splat',targets.velocity.b,{target:targets.velocity.a},{point,value:[vx,vy,0],radius,aspect});
      targets.velocity.swap();
      pass('splat',targets.dye.b,{target:targets.dye.a},{point,value:col,radius:radius*1.5,aspect});
      targets.dye.swap();
    }

    // Pointer motion and dragging both stir the dye. The regenerating budget
    // caps total ink per second, so a long movement cannot cover the screen.
    function motion(event){
      if(!active||reduce.matches||!targets)return;
      const bounds=root.getBoundingClientRect();
      if(!bounds.width||!bounds.height)return;
      const next={x:Math.max(0,Math.min(1,(event.clientX-bounds.left)/bounds.width)),
                  y:Math.max(0,Math.min(1,1-(event.clientY-bounds.top)/bounds.height)),id:event.pointerId};
      if(!pointer||pointer.id!==next.id){
        pointer=next;
        return;
      }
      const dx=next.x-pointer.x,dy=next.y-pointer.y;
      pointer=next;
      const dist=Math.hypot(dx*width,dy*height);
      const now=performance.now();
      if(dist<5||now-lastSample<25)return;
      lastSample=now;
      const dragging=(event.pointerType==='mouse'||event.pointerType==='pen')
        ? (event.buttons&1)!==0 : true;
      // Continuous, bounded budget refills even during a single long drag.
      if(!lastBudget)lastBudget=now;
      inkBudget=Math.min(1.05,inkBudget+(now-lastBudget)*.00042);
      lastBudget=now;
      const desired=Math.min(dragging?.19:.11,dist/(dragging?340:630));
      const amount=Math.min(desired,inkBudget);
      if(amount<.008)return;
      inkBudget-=amount;
      const velocity=dragging?7.0:4.5;
      queue.push({
        x:next.x,y:next.y,
        vx:Math.max(-.9,Math.min(.9,dx*velocity)),
        vy:Math.max(-.9,Math.min(.9,dy*velocity)),
        color:colors[step%colors.length].map(v=>v*amount*(dragging?1.5:1.25)),
        radius:dragging?.035:.028
      });
      if(queue.length>12)queue.shift();
    }
    function beginDrag(event){
      if(event.pointerType==='mouse'&&event.button!==0)return;
      // Allow the first move to measure displacement from pointer-down.
      const bounds=root.getBoundingClientRect();
      if(!bounds.width||!bounds.height)return;
      pointer={id:event.pointerId,x:(event.clientX-bounds.left)/bounds.width,
        y:1-(event.clientY-bounds.top)/bounds.height};
    }
    function forget(event){if(!event||!pointer||event.pointerId===pointer.id)pointer=null;}
    function simulate(now){
      if(!active)return;
      raf=requestAnimationFrame(simulate);
      if(now-last<30)return;
      const dt=Math.min(.034,Math.max(.01,(now-last||33)/1000));
      last=now;
      if(!targets)return;
      step++;
      const vel=targets.velocity,dye=targets.dye,pressure=targets.pressure,texel=[1/simW,1/simH];
      gl.disable(gl.BLEND);
      // Semi-Lagrangian transport; manual bilinear sampling supports GPUs without float-linear filtering.
      pass('advect',vel.b,{velocity:vel.a,source:vel.a},{sourceSize:[simW,simH],dt,fade:.965});
      vel.swap();
      pass('advect',dye.b,{velocity:vel.a,source:dye.a},{sourceSize:[dyeW,dyeH],dt,fade:.975});
      dye.swap();
      for(let i=0;i<Math.min(queue.length,3);i++){
        const q=queue.shift();
        splat(q.x,q.y,q.vx,q.vy,q.color,q.radius);
      }
      // No periodic dye injection: the fluid settles instead of taking over the page.
      pass('curl',targets.curl,{velocity:vel.a},{stepSize:texel});
      pass('vorticity',vel.b,{velocity:vel.a,curlField:targets.curl},{stepSize:texel,dt});
      vel.swap();
      pass('divergence',targets.divergence,{velocity:vel.a},{stepSize:texel});
      for(let i=0;i<11;i++){
        pass('pressure',pressure.b,{pressureField:pressure.a,divergenceField:targets.divergence},{stepSize:texel});
        pressure.swap();
      }
      pass('subtract',vel.b,{pressureField:pressure.a,velocity:vel.a},{stepSize:texel});
      vel.swap();
      pass('display',null,{dye:dye.a},{sourceSize:[dyeW,dyeH],lightMode:document.body.dataset.theme==='glacier'?1:0});
    }
    function stop(){
      active=false;cancelAnimationFrame(raf);raf=0;last=0;pointer=null;queue=[];inkBudget=1.05;lastBudget=0;lastSample=0;
    }
    function start(){
      stop();theme();
      if(!ready||reduce.matches||document.hidden||root.classList.contains('hidden'))return;
      resize();if(!ready||!targets)return;
      active=true;raf=requestAnimationFrame(simulate);
    }
    root.addEventListener('pointermove',motion,{passive:true});
    root.addEventListener('pointerdown',beginDrag,{passive:true});
    root.addEventListener('pointerleave',forget,{passive:true});
    root.addEventListener('pointerup',forget,{passive:true});
    root.addEventListener('pointercancel',forget,{passive:true});
    window.addEventListener('resize',()=>{if(active)resize()},{passive:true});
    document.addEventListener('visibilitychange',()=>{
      if(document.hidden)stop();else if(!root.classList.contains('hidden'))start();
    });
    reduce.addEventListener('change',()=>{if(reduce.matches)stop();else if(!root.classList.contains('hidden'))start();});
    canvas.addEventListener('webglcontextlost',e=>{e.preventDefault();stop();ready=false;canvas.style.display='none';});
    return{start,stop,palette:theme};
  }
  window.createLoginFluid=create;
})();