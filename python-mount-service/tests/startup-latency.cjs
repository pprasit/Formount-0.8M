// Verify live session startup never waits for history, even when history fails.
const {chromium}=require(process.env.MOUNT_PLAYWRIGHT_MODULE || 'playwright');
const assert=require('node:assert/strict');
(async()=>{
 const browser=await chromium.launch({channel:'msedge',headless:true});
 for(const fail of [false,true]){
  const page=await browser.newPage();let historyFinished=false,firstFrame;const errors=[];
  page.on('pageerror',e=>errors.push(e.message));
  await page.route('**/api/history?*',async route=>{
   await new Promise(r=>setTimeout(r,4000));
   try {if(fail)await route.fulfill({status:503,body:'Unavailable'});else await route.continue();}catch{}
  });
  page.on('response',r=>{if(r.url().includes('/api/history?'))historyFinished=true;});
  page.on('websocket',ws=>ws.on('framereceived',()=>{firstFrame??=Date.now();}));
  const began=Date.now();
  await page.goto('http://localhost:8765/',{waitUntil:'domcontentloaded'});
  await page.locator('.axis-card').first().waitFor({state:'attached'});await page.waitForFunction(()=>document.querySelector('#combined-az').textContent!=='—');const ready=Date.now()-began;
  assert.equal(historyFinished,false);assert.ok(ready<3000,`Startup took ${ready}ms`);
  await page.getByText('Live stream',{exact:true}).waitFor();
  await page.waitForResponse(r=>r.url().includes('/api/history?'));
  await page.getByText('Live stream',{exact:true}).waitFor();
  assert.equal(await page.locator('.axis-card').count(),4);assert.deepEqual(errors,[]);
  console.log(JSON.stringify({history:fail?'503':'delayed',firstFrame_ms:firstFrame-began,liveCards_ms:ready}));
  await page.close();
 }
 await browser.close();
})().catch(e=>{console.error(e);process.exit(1)});
