const {chromium}=require('C:/Users/Narit/AppData/Local/OpenAI/Codex/runtimes/cua_node/3dd31cfff853001c/bin/node_modules/playwright');
const assert=require('node:assert/strict');
(async()=>{const browser=await chromium.launch({channel:'msedge',headless:true});try{
const page=await browser.newPage({viewport:{width:1680,height:900}});
let release;const gate=new Promise(r=>release=r);
await page.route('**/api/astronomy/satellites/catalog/**',async route=>{await gate;await route.fulfill({json:{objects:[]}})});
await page.goto('http://localhost:8765');await page.locator('[data-nav="non-celestial"]').click();await page.locator('[data-moving="satellite"]').click();
await page.locator('#satellite-sky-loading').waitFor({state:'visible'});assert.match(await page.locator('#satellite-sky-loading').textContent(),/Loading/);
await page.screenshot({path:'python-mount-service/runtime/satellite-loading-new.png'});release();await page.locator('#satellite-sky-loading').waitFor({state:'hidden'});
const result=await page.evaluate(async()=>{const sky=await import('/assets/satellite-sky.js?v=20261010-2');document.querySelector('#moving-line1').value='1 12345';sky.setSatelliteCatalog({objects:[],calculation_ms:0,cpu_ms:0});const ctx=document.querySelector('#satellite-sky-canvas').getContext('2d');const orig=ctx.stroke.bind(ctx);let strokes=[];ctx.stroke=function(){if(this.strokeStyle==='#f5bc75')strokes.push(this.getLineDash());orig();};sky.setSatelliteTrack('12345',[{azimuth_deg:0,altitude_deg:30,seconds:0},{azimuth_deg:20,altitude_deg:0,seconds:100},{azimuth_deg:40,altitude_deg:-30,seconds:200},{azimuth_deg:60,altitude_deg:30,seconds:300},{azimuth_deg:80,altitude_deg:45,seconds:400}],100);return strokes;});
assert(result.some(a=>a.length===0));assert(result.at(-1).length>0,'Above-horizon return after first setting must remain dashed');console.log('Passed: Loading during fetch, clears on completion, future orbit above horizon remains dashed.');
}finally{await browser.close();}})().catch(e=>{console.error(e);process.exitCode=1});
