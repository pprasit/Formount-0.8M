const {chromium}=require('C:/Users/Narit/AppData/Local/OpenAI/Codex/runtimes/cua_node/3dd31cfff853001c/bin/node_modules/playwright');
const assert=require('node:assert/strict');
(async()=>{
 const browser=await chromium.launch({channel:'msedge',headless:true});
 const page=await browser.newPage({reducedMotion:'reduce'});let probes=[],saves=[],fail=false,release;
 const errors=[];page.on('pageerror',e=>errors.push(e.message));
 const result={host:'previous-good.ntp',ok:true,estimated_error_ms:1,rtt_ms:2,offset_ms:0,jitter_ms:0,stratum:2,samples:3};
 await page.route('**/api/pointing/map-config',r=>r.fulfill({json:{provider:'none'}}));
 await page.route('**/api/pointing/location',r=>r.fulfill({json:{location:{latitude:18.85,longitude:98.95,elevation:350}}}));
 await page.route('**/api/pointing/telescopes',r=>r.fulfill({json:{telescopes:[]}}));
 await page.route('**/api/audit/events',r=>r.fulfill({json:{accepted:1,events:[]}}));
 await page.route('**/api/pointing/ntp**',async r=>{
  if(r.request().url().endsWith('/test')){probes.push(r.request().postDataJSON().server);await new Promise(resolve=>release=resolve);return r.fulfill({json:{results:[fail?{host:result.host,ok:false,error:'No response'}:result],best:fail?null:result}});}
  if(r.request().method()==='POST')saves.push(r.request().postDataJSON());
  await r.fulfill({json:{ntp:result}});
 });
 await page.goto('http://localhost:8765');await page.locator('[data-nav="pointing"]').click();await page.locator('#pointing-begin').click();await page.locator('#pointing-location-next').click();
 await page.waitForFunction(()=>document.querySelector('#pointing-ntp-status').textContent==='Measuring server…');
 assert.deepEqual(probes,['previous-good.ntp']);assert.equal(await page.locator('#pointing-ntp-server').inputValue(),result.host);assert.equal(await page.locator('#pointing-ntp-next').isDisabled(),true);
 release();await page.waitForFunction(()=>!document.querySelector('#pointing-ntp-next').disabled);await page.locator('#pointing-ntp-next').click();await page.waitForSelector('#pointing-telescopes',{state:'visible'});assert.deepEqual(saves,[{server:result.host}]);
 fail=true;await page.locator('#pointing-telescopes-back').click();await page.waitForFunction(()=>document.querySelector('#pointing-ntp-status').textContent==='Measuring server…');assert.equal(probes.length,2);assert.equal(await page.locator('#pointing-ntp-next').isDisabled(),true);release();await page.waitForFunction(()=>document.querySelector('#pointing-ntp-status').textContent.startsWith('No synchronized'));assert.equal(await page.locator('#pointing-ntp-next').isDisabled(),true);assert.equal(await page.locator('#pointing-ntp-test').isEnabled(),true);
 fail=false;await page.locator('#pointing-ntp-test').click();await page.waitForFunction(()=>document.querySelector('#pointing-ntp-status').textContent==='Measuring server…');release();await page.waitForFunction(()=>!document.querySelector('#pointing-ntp-next').disabled);assert.equal(probes.length,3);assert.deepEqual(errors,[]);await browser.close();console.log('PASS: saved NTP restored; automatic entry probe; Next enabled only on fresh success; return refresh; failure and manual retry. All NTP/config writes mocked.');
})().catch(e=>{console.error(e);process.exit(1)});
