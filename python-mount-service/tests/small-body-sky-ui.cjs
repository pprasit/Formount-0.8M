const {chromium}=require('C:/Users/Narit/AppData/Local/OpenAI/Codex/runtimes/cua_node/3dd31cfff853001c/bin/node_modules/playwright');const assert=require('node:assert/strict');
(async()=>{const browser=await chromium.launch({channel:'msedge',headless:true});try{
const page=await browser.newPage({viewport:{width:1680,height:950}});const errors=[];page.on('pageerror',e=>errors.push(e.message));let frame;
page.on('response',async r=>{if(r.url().includes('/api/astronomy/small-bodies/')&&r.status()===200)frame=await r.json();});
await page.goto('http://localhost:8765');await page.locator('[data-nav="non-celestial"]').click();
for(const kind of ['asteroid','comet']){
frame=null;await page.locator('[data-moving="'+kind+'"]').click();await page.waitForFunction(()=>document.querySelector('#moving-small-body').options.length>10,{},{timeout:60000});await page.locator('#satellite-sky-loading').waitFor({state:'hidden'});
assert.equal(await page.locator('#satellite-sky h3').textContent(),(kind==='asteroid'?'Asteroid':'Comet')+' sky · 3D');assert.equal(await page.locator('.visibility-card').isVisible(),false);
const selected=await page.evaluate(async objects=>{const {projectSky}=await import('/assets/sky-projection.js');const canvas=document.querySelector('#satellite-sky-canvas'),box=canvas.getBoundingClientRect();const r=+canvas.dataset.radius;return objects.map(o=>{const p=projectSky(o.azimuth_deg,o.altitude_deg,0,0);return{id:o.id,name:o.name,x:box.x+box.width/2+r*p.x,y:box.y+box.height/2-r*p.y,depth:p.depth,radius:r*Math.hypot(p.x,p.y)};}).find(p=>p.depth>.2&&p.radius<r-18);},frame.objects);
assert(selected);await page.mouse.click(selected.x,selected.y);await page.waitForFunction(id=>document.querySelector('#moving-small-body').value===id,selected.id);await page.waitForFunction(()=>document.querySelector('#moving-ra').textContent!=='—');assert((await page.locator('#small-body-details').textContent()).includes(selected.name));
await page.locator('#satellite-zoom-in').click();assert.equal(await page.locator('#satellite-sky-canvas').getAttribute('data-zoom'),'1.20');await page.locator('#satellite-reset-view').click();
await page.screenshot({path:'python-mount-service/runtime/'+kind+'-sky-desktop.png'});
await page.setViewportSize({width:390,height:844});await page.screenshot({path:'python-mount-service/runtime/'+kind+'-sky-mobile.png'});assert(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1));await page.setViewportSize({width:1680,height:950});
console.log('Passed:',kind,frame.objects.length,'real catalog objects; sky click selected',selected.name,'and populated live coordinates; zoom/reset; mobile width.');
}
await page.locator('[data-moving="satellite"]').click();await page.waitForFunction(()=>document.querySelector('#satellite-sky h3').textContent==='Satellite sky · 3D');assert.equal(await page.locator('.small-body-controls').isVisible(),false);assert.deepEqual(errors,[]);
}finally{await browser.close();}})().catch(e=>{console.error(e);process.exitCode=1});
