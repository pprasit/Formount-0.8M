const {chromium}=require('playwright');const assert=require('node:assert/strict');
(async()=>{const browser=await chromium.launch({channel:'msedge',headless:true});try{
 const page=await browser.newPage({viewport:{width:1680,height:950}}),posts=[],errors=[];
 page.on('pageerror',e=>errors.push(e.message));
 await page.route('**/api/**',async route=>{if(route.request().method()==='POST'){posts.push({url:route.request().url(),body:route.request().postDataJSON()});return route.fulfill({json:{ok:true}});}return route.continue();});
 await page.routeWebSocket('**/ws/telemetry',()=>{});
 await page.goto('http://localhost:8765');await page.locator('[data-nav="astronomy"]').click();
 const fixture=await(await page.request.get('http://localhost:8765/api/state')).json();fixture.observatory.tracking_quality={state:'acquiring',error_arcsec_s:{azimuth:3.6,altitude:-2.4},tolerance_arcsec_s:1};await page.evaluate(detail=>document.dispatchEvent(new CustomEvent('mount-state',{detail})),fixture);
 await page.waitForTimeout(100);
 assert.equal(await page.locator('#tracking-error-az').textContent(),'+3.600');
 assert.equal(await page.locator('#tracking-quality-state').textContent(),'ACQUIRING');
 await page.locator('#tracking-tolerance').fill('5');await page.locator('#tracking-tolerance').press('Enter');await page.waitForTimeout(100);
 assert.equal(posts.length,1);assert.equal(posts[0].body.tolerance_arcsec_s,5);
 await page.screenshot({path:'runtime/tracking-quality-layout.png'});
 const panel=await page.locator('.tracking-quality').boundingBox(),parent=await page.locator('.object-sky-panel').boundingBox();assert.ok(panel.x>=parent.x&&panel.y+panel.height<=parent.y+parent.height);
 await page.setViewportSize({width:650,height:950});await page.screenshot({path:'runtime/tracking-quality-mobile.png'});
 assert.deepEqual(errors,[]);console.log('Tracking error values, acquiring state, tolerance save and panel bounds passed; every POST mocked.');
}finally{await browser.close();}})().catch(e=>{console.error(e);process.exitCode=1});
