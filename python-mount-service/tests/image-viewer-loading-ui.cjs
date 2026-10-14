const {chromium}=require('C:/Users/Narit/AppData/Local/OpenAI/Codex/runtimes/cua_node/3dd31cfff853001c/bin/node_modules/playwright');
const assert=require('node:assert/strict');
(async()=>{
 const browser=await chromium.launch({channel:'msedge',headless:true});const page=await browser.newPage({viewport:{width:1680,height:960}});const forbidden=[];
 page.on('request',r=>{if(r.url().includes('/stretch-source/')||r.url().endsWith('/fits')||r.url().endsWith('/assets/styles.css'))forbidden.push(r.url());});
 await page.route('**/assets/image-viewer.js',async route=>{await new Promise(r=>setTimeout(r,2500));await route.continue();});
 await page.route('**/display.webp?*',async route=>{if(new URL(route.request().url()).searchParams.get('maximum')==='2560')await new Promise(r=>setTimeout(r,1500));await route.continue();});
 await page.goto('http://127.0.0.1:8765/assets/image-viewer.html?image=capture-1791763217236831000.fits',{waitUntil:'commit'});
 await page.locator('h1').waitFor({state:'visible',timeout:1000});assert.equal(await page.locator('h1').innerText(),'Image viewer');
 await page.waitForFunction(()=>!document.querySelector('#viewer-image').hidden,null,{timeout:10000});
 await page.waitForFunction(()=>document.querySelector('#viewer-message').textContent==='Preview ready',null,{timeout:20000});
 assert(await page.locator('#viewer-image').evaluate(e=>e.naturalWidth>1280&&e.naturalWidth<=2560));assert((await page.locator('#viewer-header-panel').innerText()).includes('BITPIX'));assert.equal(await page.locator('#viewer-header-panel').getAttribute('hidden'),null);
 assert.deepEqual(forbidden,[]);await page.screenshot({path:'runtime/imaging-detailed-webp-viewer.png'});
 await page.setViewportSize({width:390,height:844});assert(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth));await page.screenshot({path:'runtime/imaging-detailed-webp-mobile.png'});
 const failed=await browser.newPage();await failed.route('**/assets/image-viewer.js',route=>route.abort());await failed.goto('http://127.0.0.1:8765/assets/image-viewer.html?image=missing.fits');await failed.locator('#viewer-retry').waitFor({state:'visible',timeout:10000});assert((await failed.locator('#viewer-message').innerText()).includes('retry'));
 await browser.close();console.log('PASS immediate shell, quick/detailed WebP, right header, bounded resolution, no raw/FITS transfers, retry on script failure, mobile');
})().catch(e=>{console.error(e);process.exit(1)});
