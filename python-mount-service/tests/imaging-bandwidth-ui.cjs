const {chromium}=require('C:/Users/Narit/AppData/Local/OpenAI/Codex/runtimes/cua_node/3dd31cfff853001c/bin/node_modules/playwright');
const assert=require('node:assert/strict');
(async()=>{
 const browser=await chromium.launch({channel:'msedge',headless:true});
 const page=await browser.newPage({viewport:{width:1680,height:960}});
 await page.addInitScript(()=>{
  window.videoFrames=0;window.telemetryFrames=0;
  const Native=window.WebSocket;
  window.WebSocket=class extends Native {
   constructor(...args){super(...args);this.addEventListener('message',()=>{if(this.url.endsWith('/ws/telemetry'))window.telemetryFrames++;else if(this.url.endsWith('/ws/camera/video'))window.videoFrames++;});}
   set onmessage(fn){super.onmessage=event=>{if(this.url.endsWith('/ws/telemetry')&&window.blockTelemetry)return;fn(event);};}
   get onmessage(){return super.onmessage;}
  };
 });
 await page.route('**/api/camera/video/bandwidth-probe',async route=>{await new Promise(r=>setTimeout(r,800));await route.continue();});
 await page.goto('http://localhost:8765');await page.locator('[data-nav="imaging"]').click();
 await page.waitForFunction(()=>!document.querySelector('#acquisition-expose').disabled);
 await page.locator('[data-acquisition-mode="video"]').click();await page.locator('#acquisition-expose').click();
 await page.waitForFunction(()=>document.querySelector('#acquisition-progress-detail').textContent.includes('Measured spare'),null,{timeout:20000});
 await page.waitForTimeout(5000);
 const metrics=await page.locator('#acquisition-progress-detail').innerText();console.log('slow link:',metrics);
 const limit=Number(metrics.match(/Delivery limit ([\d.]+)/)[1]);assert(limit<=2,'Measured slow link must reduce delivery FPS');
 assert(Number(metrics.match(/Video budget ([\d.]+)/)[1])<.25,'Video must reserve spare capacity');
 const before=await page.evaluate(()=>({telemetry:telemetryFrames,video:videoFrames}));await page.waitForTimeout(3000);
 const after=await page.evaluate(()=>({telemetry:telemetryFrames,video:videoFrames}));assert(after.telemetry-before.telemetry>=30,'Telemetry cadence must remain fast');
 assert.equal(await page.locator('#acquisition-image').evaluate(e=>e.naturalWidth),640);
 await page.evaluate(()=>window.blockTelemetry=true);await page.waitForTimeout(1200);
 const paused=await page.evaluate(()=>videoFrames);await page.waitForTimeout(2300);
 assert(await page.locator('#acquisition-phase').innerText().then(s=>s.includes('waiting for telemetry')));
 assert.equal(await page.evaluate(()=>videoFrames),paused,'Video must pause while telemetry is overdue');
 await page.evaluate(()=>window.blockTelemetry=false);await page.waitForTimeout(4000);
 assert((await page.evaluate(()=>videoFrames))>paused,'Video must resume when telemetry returns');
 await page.locator('#acquisition-abort').click();await page.waitForFunction(()=>!document.querySelector('#acquisition-expose').disabled);
 await browser.close();console.log('PASS measured capacity, byte budget, telemetry priority, pause/resume');
})().catch(e=>{console.error(e);process.exit(1)});
