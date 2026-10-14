const {chromium}=require(process.env.MOUNT_PLAYWRIGHT_MODULE||'playwright');
const assert=require('node:assert/strict');
(async()=>{
 const browser=await chromium.launch({channel:'msedge',headless:true});
 const page=await browser.newPage({viewport:{width:1902,height:934}});const errors=[];let payload;
 page.on('pageerror',e=>errors.push(e.message));
 // Intercept motion before testing the form. Never send it to the real drive.
 await page.route('**/api/axes/**',async route=>{if(route.request().url().endsWith('/combined/goto')){payload=route.request().postDataJSON();await route.fulfill({json:{ok:true}});}else await route.abort();});
 await page.route('**/api/enable-all',route=>route.abort());await page.route('**/api/stop',route=>route.abort());
 await page.goto(process.env.MOUNT_TEST_URL||'http://localhost:8765');
 await page.waitForFunction(()=>document.querySelector('#input-quality').textContent.includes('ms old'));
 assert.equal(await page.locator('#combined-panel').isVisible(),true);
 assert.equal(await page.locator('.axis-card:visible').count(),0);
 const inputs=await (await page.request.get(new URL('/api/inputs',page.url()).href)).json();assert.ok(inputs.valid);
 assert.ok(await page.locator('#input-raw').textContent());
 const ratio=await page.evaluate(()=>document.querySelector('.sky-panel').getBoundingClientRect().width/document.querySelector('#combined-panel').getBoundingClientRect().width);assert.ok(ratio>.62&&ratio<.69);
 for(const size of [{width:1902,height:934},{width:1366,height:768},{width:1280,height:720}]){
  await page.setViewportSize(size);
  for(const id of ['combined','azimuth','altitude','rotator','m3']){
   await page.locator(`[data-axis-tab="${id}"]`).click();
   const dimensions=await page.evaluate(()=>{const e=document.querySelector('.dashboard-scroll'),sidebar=document.querySelector('.sidebar'),c=document.querySelector('.combined-console');return {root:document.documentElement.scrollHeight,window:innerHeight,client:e.clientHeight,scroll:e.scrollHeight,sideClient:sidebar.clientHeight,sideScroll:sidebar.scrollHeight,consoleClient:c.clientHeight,consoleScroll:c.scrollHeight};});
   assert.ok(dimensions.root<=dimensions.window,JSON.stringify(dimensions));assert.ok(dimensions.scroll<=dimensions.client+1,JSON.stringify(dimensions));assert.ok(dimensions.sideScroll<=dimensions.sideClient+1,JSON.stringify(dimensions));
   if(id==='combined')assert.ok(dimensions.consoleScroll<=dimensions.consoleClient+1,JSON.stringify(dimensions));
   else {assert.equal(await page.locator('.axis-card:visible').count(),1);assert.equal(await page.locator(`[data-axis="${id}"]`).isVisible(),true);await page.locator(`[data-axis="${id}"] .metric-select`).selectOption('current');}
  }
 }
 await page.locator('[data-axis-tab="combined"]').click();
 await page.locator('#combined-target-az').fill('120');await page.locator('#combined-target-alt').fill('35');await page.locator('#combined-speed-az').fill('2');await page.locator('#combined-speed-alt').fill('3');
 assert.equal(await page.locator('#combined-form').evaluate(e=>e.checkValidity()),true);
 await page.evaluate(()=>{document.querySelector('#combined-goto').disabled=false;document.querySelector('#combined-form').requestSubmit();});
 await page.waitForFunction(()=>document.querySelector('#combined-error').textContent.includes('acknowledged'));
 assert.deepEqual(payload,{azimuth:120,altitude:35,azimuth_speed:2,altitude_speed:3});
 await page.locator('[data-axis-tab="altitude"]').click();await page.locator('[data-nav="telemetry"]').click();assert.equal(await page.locator('#combined-panel').isVisible(),true);
 for(const nav of ['astronomy','pointing','camera','events']){await page.locator(`[data-nav="${nav}"]`).click();assert.equal(await page.locator('#'+nav).isVisible(),true);assert.equal(await page.locator('#combined-panel').isVisible(),false);}
 await page.locator('[data-nav="camera"]').click();await page.locator('#show-points').click();assert.equal(await page.locator('.points-table-wrap').isVisible(),true);await page.locator('#show-camera').click();assert.equal(await page.locator('#camera .observing-grid').isVisible(),true);
 await page.setViewportSize({width:390,height:844});await page.locator('[data-nav="telemetry"]').click();assert.ok(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth));
 assert.deepEqual(errors,[]);await browser.close();console.log('Telemetry tabs passed: real inputs, 2/3 sky layout, viewport fit at three desktop sizes, tab isolation, metric selection, mocked combined command, page navigation, mobile width.');
})().catch(e=>{console.error(e);process.exit(1);});
