const {chromium}=require('C:/Users/Narit/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright');
const assert=require('node:assert/strict');
(async()=>{
 const b=await chromium.launch({channel:'msedge',headless:true});
 const p=await b.newPage({viewport:{width:1680,height:900}});const errors=[];p.on('pageerror',e=>errors.push(e.message));
 const rows=[{name:'Recovery satellite',line1:'1 25544U 98067A   24001.00000000  .00000000  00000-0  00000-0 0  9999',line2:'2 25544  51.6400  20.0000 0001000  30.0000  40.0000 15.50000000123456'}];
 await p.route('**/api/astronomy/satellites/catalog/*',r=>r.fulfill({json:{objects:rows}}));
 let connections=0,frames=0;const timers=[];
 await p.routeWebSocket('**/ws/astronomy/satellites',ws=>{const connection=++connections;let started=false;ws.onMessage(()=>{if(started)return;started=true;const send=()=>{frames++;ws.send(JSON.stringify({utc:new Date().toISOString(),objects:[{id:'25544',name:'Recovery satellite',azimuth_deg:frames,altitude_deg:40}],calculation_ms:1,cpu_ms:1,sample_interval_ms:1000}));};send();if(connection>1)timers.push(setInterval(send,1000));});});
 await p.goto('http://127.0.0.1:8765');await p.locator('[data-nav="non-celestial"]').click();await p.locator('[data-moving="satellite"]').click();
 await p.waitForFunction(()=>document.querySelector('#satellite-sky-canvas').dataset.frameUtc,{},{timeout:10000});
 const first=await p.locator('#satellite-sky-canvas').getAttribute('data-frame-utc');
 await p.waitForFunction(old=>document.querySelector('#satellite-sky-canvas').dataset.frameUtc!==old,first,{timeout:22000});
 assert.ok(connections>=2);await p.waitForTimeout(2000);assert.ok(frames>=4);assert.deepEqual(errors,[]);
 timers.forEach(clearInterval);await b.close();console.log('PASS: silent open socket reconnected automatically; subsequent satellite positions keep updating; no JavaScript errors.');
})().catch(e=>{console.error(e);process.exit(1)});
