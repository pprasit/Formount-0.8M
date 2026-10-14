const {chromium}=require(process.env.MOUNT_PLAYWRIGHT_MODULE||'C:/Users/Narit/AppData/Local/OpenAI/Codex/runtimes/cua_node/3dd31cfff853001c/bin/node_modules/playwright');
const assert=require('node:assert/strict');
(async()=>{
 const browser=await chromium.launch({channel:'msedge',headless:true});const page=await browser.newPage({viewport:{width:1600,height:1100}});const errors=[];page.on('pageerror',e=>errors.push(e.message));
 await page.goto(process.env.MOUNT_TEST_URL||'http://localhost:8765');await page.locator('[data-nav="pointing"]').click();await page.waitForFunction(()=>document.querySelector('#pointing-run-state').textContent==='IDLE');
 assert.equal(await page.locator('#pointing-start').isDisabled(),true);assert.equal(await page.locator('#pointing-seeing-start').isDisabled(),true);
 const fixture=await (await page.request.get(new URL('/api/pointing/workbench',page.url()).href)).json();fixture.plan=[];fixture.points=[];const actions=[];
 await page.route('**/api/pointing/**',async route=>{const req=route.request(),path=new URL(req.url()).pathname;let result=fixture;
  if(path.endsWith('/settings'))fixture.settings=req.postDataJSON();
  if(path.endsWith('/action')){const action=req.postDataJSON();actions.push(action.action);if(action.action==='grid'){fixture.plan=Array.from({length:40},(_,i)=>({id:String(i),azimuth_deg:Math.floor(i/5)*45,altitude_deg:30+i%5*13.75,status:'pending',included:true}));fixture.total=40;fixture.state='ready';}
   if(action.action==='delete'){fixture.plan=fixture.plan.filter(p=>!action.identifiers.includes(p.id));fixture.total=fixture.plan.length;}
   if(action.action==='fit'){fixture.candidate={path:'test-only.pxp',used:9,terms:8,rms_arcsec:1.2,max_error_arcsec:2.5,residuals:[{name:'Fixture 0',used:true,ra_error_arcsec:1,dec_error_arcsec:-1,total_error_arcsec:1.4}],coefficients:[{index:1,enabled:true,value_arcsec:2.5}]};fixture.state='complete';}
   if(action.action==='keep')fixture.candidate=null;
  }
  if(path.includes('/points/')){fixture.plan.find(p=>p.id===path.split('/').at(-1)).included=req.postDataJSON().included;}
  await route.fulfill({json:result});
 });
 assert.equal(await page.locator('#pointing-legacy-calibration').isVisible(),false);assert.equal(await page.locator('#pointing-begin').isVisible(),true);
 fixture.points=Array.from({length:9},(_,i)=>({id:String(i),name:'Fixture '+i,included:true}));fixture.model.available=true;
 await page.locator('[data-pointing-tab="model"]').click();await page.waitForFunction(()=>!document.querySelector('#model-fit').disabled);await page.locator('#model-fit').click();await page.waitForFunction(()=>!document.querySelector('#pointing-candidate').hidden);
 assert.match(await page.locator('#pointing-candidate-metrics').innerText(),/1.20/);assert.equal(await page.locator('#pointing-terms tr').count(),1);await page.screenshot({path:'python-mount-service/runtime/pointing-model-desktop.png'});
 await page.locator('#pointing-keep').click();await page.waitForFunction(()=>document.querySelector('#pointing-candidate').hidden);
 await page.locator('[data-pointing-tab="analysis"]').click();await page.locator('[data-pointing-graph="tracking"]').click();await page.locator('[data-pointing-graph="pointing"]').click();
 for(const name of ['calibration','model','analysis']){await page.setViewportSize({width:390,height:844});await page.locator(`[data-pointing-tab="${name}"]`).click();assert.ok(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),name+' overflows mobile');await page.screenshot({path:`python-mount-service/runtime/pointing-${name}-mobile.png`});}
 assert.deepEqual(errors,[]);assert.deepEqual(actions,['fit','keep']);await browser.close();console.log('Pointing UI passed: default landing, live camera guards, mocked fit/candidate review, tabs, desktop and mobile. No motor or exposure commands sent.');
})().catch(e=>{console.error(e);process.exit(1)});
