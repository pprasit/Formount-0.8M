import {addInteresting,showInterestingMenu,confirmSolar} from './interesting-objects.js';
import {hms,dms} from './coordinates.js';
import {LocalSky} from './sky-display.js';
import {clearSatelliteSky,showSatelliteSky,setSatelliteCatalog,setSatelliteTrack,setSatelliteLoading,setSkyMode,showSmallBodySky,focusSkyObject} from './satellite-sky.js?v=20261010-2';
import {updateVisibility,clearVisibility} from './visibility.js';
const $=s=>document.querySelector(s);
let category='solar',busy=false,tracking=false,socket,revision=0,retryTimer,refreshTimer,renderFrame,pending,lastSample=0;
const catalogs=new Map();
const catalogRequests=new Map(),catalogLifetime=new AbortController();
async function cachedCatalog(path,cache,key){
 if(cache.has(key))return cache.get(key);
 if(!catalogRequests.has(path)){
  const request=fetch(path,{signal:catalogLifetime.signal,priority:'low'}).then(async r=>{const d=await r.json();if(!r.ok)throw Error(d.detail||'Catalog unavailable');const value=cache===catalogs?d.objects:d;cache.set(key,value);return value;}).finally(()=>catalogRequests.delete(path));
  catalogRequests.set(path,request);
 }
 return catalogRequests.get(path);
}
window.addEventListener('pagehide',()=>catalogLifetime.abort());

let objectSelected=false;
function trackAppearance(){const b=$('#moving-goto');b.classList.toggle('awaiting-object',!objectSelected);b.setAttribute('aria-disabled',String(!objectSelected||tracking||busy));}
function selectionMessage(text='กรุณาเลือกวัตถุจาก Sky Chart หรือตารางวัตถุก่อน'){ $('#moving-select-message').textContent=text;$('#moving-select-dialog').showModal();}

const fmt=(n,d=4)=>Number.isFinite(n)?n.toFixed(d):'—';
const methods={solar:'PWLib Planets · apparent RA/Dec · geometric AltAz',satellite:'C# SatelliteTracker · PWLib SGP4 · topocentric apparent',asteroid:'C# SmallBodyEphemerisCalculator · approximate Kepler orbit',comet:'C# SmallBodyEphemerisCalculator · elliptical orbit (e < 1)'};
const selectedSky=new LocalSky(d=>{if(d.objects[0])show({...d.objects[0],utc:d.utc,site:d.site,sample_interval_ms:100,display_only:true});},e=>{$('#moving-error').textContent=e.message;});
function stopStream(){revision++;clearTimeout(retryTimer);selectedSky.stop();}
function clear(){trackVersion++;stopStream();clearSatelliteSky();clearVisibility();for(const id of ['az','alt','ra','dec','az-rate','alt-rate'])$('#moving-'+id).textContent='—';$('#moving-result').textContent='Select a target to start live position calculation.';$('#moving-error').textContent='';$('#moving-cadence').textContent='';$('#moving-target-name').textContent=category==='solar'?$('#moving-body').selectedOptions[0].textContent:(category==='satellite'?$('#moving-satellite-name').value:$('#moving-name').value).trim()||'Select a target';clearTimeout(refreshTimer);refreshTimer=setTimeout(startStream,0);}
async function startStream(){stopStream();if($('#non-celestial').hidden||document.hidden||!$('#moving-form').checkValidity())return;const body=payload(false);if(category==='solar')updateVisibility(body);const row=body.tle||{id:'selected',name:$('#moving-target-name').textContent,...body};await selectedSky.start(category==='satellite'?'satellite':'objects',[row]);if(body.tle)loadTrack(body.tle);}
new MutationObserver(()=>{$('#non-celestial').hidden?stopStream():startStream();}).observe($('#non-celestial'),{attributes:true,attributeFilter:['hidden']});
document.addEventListener('visibilitychange',()=>document.hidden?stopStream():startStream());


function selectCategory(value){
stopSmallBodies();solarCatalogSky.stop();objectSelected=false;trackAppearance();resetObjectTable();category=value;$('#non-celestial').dataset.category=value;const small=value==='asteroid'||value==='comet';smallSelection='';smallRows=[];
if(small)for(const id of ['epoch','a','peri-epoch','q','e','i','node','peri','anomaly','name'])$('#moving-'+id).value='';
clear();setSkyMode(value);
document.querySelectorAll('[data-moving]').forEach(b=>{const active=b.dataset.moving===value;b.classList.toggle('active',active);b.setAttribute('aria-selected',String(active));b.tabIndex=active?0:-1;});
document.querySelectorAll('[data-moving-panel]').forEach(p=>p.hidden=small?!['elements','small-body'].includes(p.dataset.movingPanel):p.dataset.movingPanel!==value);
document.querySelectorAll('[data-orbit]').forEach(p=>p.hidden=p.dataset.orbit!==value);
$('#moving-name-field').hidden=!small;$('#satellite-sky').hidden=false;$('#non-celestial').classList.add('satellite-view');$('#non-celestial .visibility-card').hidden=value!=='solar';$('#moving-method').textContent=methods[value];
for(const input of [...$('#moving-form').elements].filter(el=>el.matches('input,select,textarea'))){const visible=!input.closest('[data-moving-panel][hidden],[data-orbit][hidden],#moving-name-field[hidden]');input.disabled=!visible;input.required=visible&&input.type!=='file'&&!['moving-category','moving-satellite','moving-small-body'].includes(input.id);}
if(value==='solar')startSolarCatalog();if(value==='satellite')loadCatalog();else stopCatalogStream();if(small){$('#small-body-label').textContent=value==='asteroid'?'Asteroid':'Comet';$('#small-body-details').textContent='Select an object on the sphere or from the list.';$('#moving-small-body').replaceChildren(new Option('Loading objects…',''));loadSmallBodies(true);}
$('#moving-target-name').textContent=value==='solar'?$('#moving-body').selectedOptions[0].textContent:'Select a target';
}
document.querySelectorAll('[data-moving]').forEach(b=>{b.onclick=()=>selectCategory(b.dataset.moving);b.onkeydown=e=>{if(!['ArrowLeft','ArrowRight','Home','End'].includes(e.key))return;e.preventDefault();const tabs=[...document.querySelectorAll('[data-moving]')];const i=tabs.indexOf(b),next=e.key==='Home'?0:e.key==='End'?tabs.length-1:(i+(e.key==='ArrowRight'?1:-1)+tabs.length)%tabs.length;tabs[next].click();tabs[next].focus();};});
function payload(report=true){if(!(report?$('#moving-form').reportValidity():$('#moving-form').checkValidity()))throw Error('Complete the target fields.');const num=id=>Number($('#moving-'+id).value);if(category==='solar')return{body:$('#moving-body').value};const name=(category==='satellite'?$('#moving-satellite-name').value:$('#moving-name').value).trim();if(category==='satellite')return{tle:{name,line1:$('#moving-line1').value,line2:$('#moving-line2').value}};const orbit={name,eccentricity:num('e'),inclination_deg:num('i'),ascending_node_deg:num('node'),arg_perihelion_deg:num('peri')};if(category==='asteroid')Object.assign(orbit,{epoch_jd:num('epoch'),semi_major_axis_au:num('a'),mean_anomaly_deg:num('anomaly')});else Object.assign(orbit,{perihelion_epoch_jd:num('peri-epoch'),perihelion_distance_au:num('q')});return{orbit};}
function show(d){if(category==='solar'&&objectSelected)showSmallBodySky(d,$('#moving-body').value,$('#moving-body').selectedOptions[0].textContent);else if(category==='satellite')showSatelliteSky(d,$('#moving-satellite-name').value);else if(category==='asteroid'||category==='comet')showSmallBodySky(d,smallSelection||'manual',$('#moving-name').value);$('#moving-ra').textContent=hms(d.ra_hours);$('#moving-dec').textContent=dms(d.dec_deg);$('#moving-cadence').textContent=`Client display · ${fmt(d.sample_interval_ms,1)} ms`;$('#moving-az').textContent=fmt(d.azimuth_deg)+'°';$('#moving-alt').textContent=fmt(d.altitude_deg)+'°';$('#moving-az-rate').textContent=fmt(d.azimuth_rate_deg_s,5)+'°/s';$('#moving-alt-rate').textContent=fmt(d.altitude_rate_deg_s,5)+'°/s';const result=$('#moving-result');if(!$('#moving-time'))result.innerHTML='<div class="moving-detail-meta"><div class="moving-detail-time" id="moving-time"></div><div class="moving-detail-pills"><span id="moving-horizon"></span><span id="moving-travel"></span></div></div><div class="moving-detail-row"><span>Encoder Az / Alt</span><b id="moving-encoder"></b></div><div class="moving-detail-row"><span>Pointing</span><b id="moving-model"></b></div>';$('#moving-time').textContent=d.utc.replace('T',' ').slice(0,19)+' UTC';$('#moving-horizon').textContent=d.above_horizon?'Above horizon':'Below horizon';$('#moving-horizon').className=d.above_horizon?'good':'warning';$('#moving-travel').textContent=d.display_only?'Display only':d.slew_allowed?'Within travel':'Outside travel';$('#moving-travel').className=d.slew_allowed?'good':'warning';$('#moving-encoder').textContent=fmt(d.encoder_azimuth_deg,3)+'° / '+fmt(d.encoder_altitude_deg,3)+'°';$('#moving-model').textContent=d.display_only?'Server checks on Slew / Track':d.model_applied?'PointXP':'Encoder geometry';$('#moving-site').textContent=`Site ${fmt(d.site.latitude,4)}° / ${fmt(d.site.longitude,4)}° · ${d.site.elevation} m`;$('#moving-method').textContent='Apparent RA/Dec · geometric AltAz';}

async function command(path,body){const r=await fetch(path,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(body)});const d=await r.json();if(!r.ok)throw Error(typeof d.detail==='string'?d.detail:Array.isArray(d.detail)?d.detail.map(e=>`${e.loc.slice(1).join('.')}: ${e.msg}`).join(' · '):'Request failed');return d;}
async function run(action){if(busy)return;if(action==='track'&&!objectSelected){selectionMessage();return;}let body;try{body=payload();}catch(e){$('#moving-error').textContent=e.message;return;}if(!await confirmSolar(body))return;busy=true;$('#moving-error').textContent='';for(const id of ['goto','track'])$('#moving-'+id).disabled=true;try{const paths={goto:'/api/astronomy/goto',track:'/api/astronomy/tracking/start'};await command(paths[action],body);}catch(e){$('#moving-error').textContent=e.message;}finally{busy=false;$('#moving-goto').disabled=tracking;trackAppearance();$('#moving-track').disabled=tracking;}}
$('#moving-form').onsubmit=e=>e.preventDefault();$('#moving-goto').onclick=()=>run('track');$('#moving-track').onclick=startDemo;
$('#moving-stop').onclick=async()=>{try{await command('/api/astronomy/tracking/stop',{});$('#moving-result').textContent='Tracking stopped.';}catch(e){$('#moving-error').textContent=e.message;}};
$('#non-celestial').addEventListener('input',e=>{if(e.target.id!=='moving-window')clear();});$('#non-celestial').addEventListener('change',e=>{if(e.target.id!=='moving-window')clear();});
function catalog(){const rows=catalogs.get($('#moving-category').value)||[];$('#moving-satellite').replaceChildren(new Option('Choose an imported satellite',''));for(let i=0;i<rows.length;i++)$('#moving-satellite').add(new Option(rows[i].name,String(i)));$('#moving-line1').value='';$('#moving-line2').value='';$('#moving-satellite-name').value='';$('#moving-tle').value='';$('#moving-catalog-note').textContent=`${rows.length} objects · ${$('#moving-category').value}`;clear();startCatalogStream(rows);}
$('#moving-category').onchange=()=>{objectSelected=false;trackAppearance();resetObjectTable();$('#moving-tle-file').value='';loadCatalog();};
$('#moving-tle-file').onchange=async()=>{try{const file=$('#moving-tle-file').files[0];if(!file)return;loadVersion++;if(file.size>5*1024*1024)throw Error('TLE file must be at most 5 MB.');const lines=(await file.text()).split(/\r?\n/).map(s=>s.trimEnd()).filter(s=>s.trim());const rows=[];for(let i=0;i<lines.length;){let name=lines[i].startsWith('1 ')?`Satellite ${lines[i].slice(2,7)}`:lines[i++].replace(/^0 /,'');const line1=lines[i++],line2=lines[i++];if(!line1?.startsWith('1 ')||!line2?.startsWith('2 '))throw Error('Expected a satellite name followed by TLE lines 1 and 2.');rows.push({name,line1,line2});}catalogs.set($('#moving-category').value,rows);catalog();}catch(e){$('#moving-error').textContent=e.message;}};
$('#moving-satellite').onchange=()=>{const row=(catalogs.get($('#moving-category').value)||[])[Number($('#moving-satellite').value)];if($('#moving-satellite').value===''||!row){objectSelected=false;trackAppearance();return;}objectSelected=true;trackAppearance();$('#moving-satellite-name').value=row.name;$('#moving-line1').value=row.line1;$('#moving-line2').value=row.line2;$('#moving-tle').value=`${row.name}\n${row.line1}\n${row.line2}`;$('#moving-target-name').textContent=row.name;clear();};
document.addEventListener('mount-state',e=>{const o=e.detail.observatory;if(!o)return;tracking=!!o.tracking;$('#moving-status').textContent=tracking?'TRACKING':'IDLE';if(!busy){$('#moving-track').disabled=tracking;$('#moving-goto').disabled=tracking;trackAppearance();}});
$('#moving-tle').addEventListener('input',()=>{loadVersion++;const lines=$('#moving-tle').value.split(/\r?\n/).map(s=>s.trimEnd()).filter(s=>s.trim());const named=lines.length>0&&!lines[0].startsWith('1 ');$('#moving-satellite-name').value=named?lines.shift().replace(/^0 /,''):`Satellite ${lines[0]?.slice(2,7)||''}`;$('#moving-line1').value=lines[0]||'';$('#moving-line2').value=lines[1]||'';});


let catalogVersion=0,loadVersion=0,trackVersion=0;
let demoRows=[];
const catalogSky=new LocalSky(d=>{demoRows=d.objects;updateObjectTable(d.objects);setSatelliteCatalog(d);setSatelliteLoading('');$('#moving-catalog-note').textContent=`${d.objects.length} objects · ${$('#moving-category').value} · Client 100 ms`;},e=>{setSatelliteLoading('Display calculation unavailable');$('#moving-catalog-note').textContent=e.message;});
function stopCatalogStream(){catalogVersion++;catalogSky.stop();}
function startCatalogStream(rows){stopCatalogStream();if(!rows.length||category!=='satellite'||$('#non-celestial').hidden||document.hidden)return;setSatelliteLoading('Loading…');void catalogSky.start('satellite',rows);}
async function loadCatalog(){objectSelected=false;trackAppearance();trackVersion++;$('#moving-line1').value='';$('#moving-line2').value='';$('#moving-satellite-name').value='';$('#moving-tle').value='';clear();const key=$('#moving-category').value,version=++loadVersion;stopCatalogStream();setSatelliteCatalog({objects:[],calculation_ms:0,cpu_ms:0});setSatelliteLoading('Loading…');if(catalogs.has(key)){catalog();void warmSatelliteCatalogs();return;}$('#moving-catalog-note').textContent=`Loading ${key} from NARIT…`;try{await cachedCatalog('/api/astronomy/satellites/catalog/'+encodeURIComponent(key),catalogs,key);if(version!==loadVersion||category!=='satellite')return;catalog();void warmSatelliteCatalogs();}catch(e){if(version===loadVersion){setSatelliteLoading('Unable to load satellites');$('#moving-catalog-note').textContent=e.message;}}}
function resumeSatelliteCatalog(){if($('#non-celestial').hidden||document.hidden)return;const rows=catalogs.get($('#moving-category').value);if(rows)startCatalogStream(rows);else void loadCatalog();}
function loadTrack(row){const version=++trackVersion;selectedSky.trajectory(row,d=>{if(version===trackVersion)setSatelliteTrack(d.id,d.points,d.horizon_seconds);});}
document.addEventListener('satellite-picked',e=>{const rows=catalogs.get($('#moving-category').value)||[];const i=rows.findIndex(row=>row.line1.slice(2,7).trim()===e.detail.id);if(i<0)return;$('#moving-satellite').value=String(i);$('#moving-satellite').dispatchEvent(new Event('change',{bubbles:true}));});
new MutationObserver(()=>{if($('#non-celestial').hidden){stopCatalogStream();stopSmallBodies();}else if(category==='satellite')resumeSatelliteCatalog();else if(category==='asteroid'||category==='comet')loadSmallBodies(true);}).observe($('#non-celestial'),{attributes:true,attributeFilter:['hidden']});
document.addEventListener('visibilitychange',()=>{if(document.hidden){stopCatalogStream();stopSmallBodies();}else if(category==='satellite')resumeSatelliteCatalog();else if(category==='asteroid'||category==='comet')loadSmallBodies(true);});

const smallCatalogs=new Map();
const smallSky=new LocalSky(d=>{demoRows=d.objects;updateObjectTable(d.objects);setSatelliteCatalog(d);setSatelliteLoading('');for(const row of d.objects){const saved=smallRows.find(r=>r.id===row.id);if(saved)Object.assign(saved,row);}if(smallSelection){const row=d.objects.find(r=>r.id===smallSelection);if(row)showSmallBodySky(row,row.id,row.name);}},e=>{$('#small-body-catalog-note').textContent=e.message;});
let smallRows=[],smallSelection='',smallVersion=0,smallTimer,smallController;
function stopSmallBodies(){smallSky.stop();smallVersion++;clearTimeout(smallTimer);smallController?.abort();smallController=null;}
async function loadSmallBodies(initial=false){
stopSmallBodies();if(!['asteroid','comet'].includes(category)||$('#non-celestial').hidden||document.hidden)return;
const kind=category,version=smallVersion;smallController=new AbortController();if(initial)setSatelliteLoading('Loading…');
try{const d=await cachedCatalog('/api/astronomy/display-catalog/'+kind,smallCatalogs,kind);if(version!==smallVersion||category!==kind)return;
smallRows=d.objects;const selector=$('#moving-small-body');if(initial||selector.options.length<=1){selector.replaceChildren(new Option('Choose an object',''));for(const row of smallRows)selector.add(new Option(row.name,row.id));selector.value=smallSelection;}
void smallSky.start('objects',d.objects);setSatelliteLoading('');$('#small-body-catalog-note').textContent=`${smallRows.length} objects · ${d.label} · NASA/JPL SBDB${d.stale?' · cached data':''}`;
if(smallSelection){const row=smallRows.find(r=>r.id===smallSelection);if(row)showSmallBodySky(row,row.id,row.name);}
}catch(error){if(error.name!=='AbortError'&&version===smallVersion){setSatelliteLoading('Unable to load objects · Retry');$('#small-body-catalog-note').textContent=error.message;}}
finally{if(version===smallVersion){smallController=null;}}
}
function applySmallBody(row){objectSelected=true;trackAppearance();smallSelection=row.id;$('#moving-small-body').value=row.id;const orbit=row.orbit;
const keys={name:'name',epoch:'epoch_jd',a:'semi_major_axis_au','peri-epoch':'perihelion_epoch_jd',q:'perihelion_distance_au',e:'eccentricity',i:'inclination_deg',node:'ascending_node_deg',peri:'arg_perihelion_deg',anomaly:'mean_anomaly_deg'};
for(const [id,key] of Object.entries(keys))$('#moving-'+id).value=orbit[key]??'';
const info=$('#small-body-details');info.replaceChildren();for(const [label,value] of [['Designation',row.name],['Epoch (JD / TDB)',row.epoch_jd],['Eccentricity',Number(orbit.eccentricity).toFixed(6)],['Inclination',Number(orbit.inclination_deg).toFixed(3)+'°'],['Diameter',row.diameter_km?row.diameter_km+' km':'Unknown']]){const line=document.createElement('div'),title=document.createElement('span'),text=document.createElement('b');title.textContent=label;text.textContent=value;line.append(title,text);info.append(line);}
clear();showSmallBodySky(row,row.id,row.name);$('#moving-target-name').textContent=row.name;
}
$('#moving-small-body').onchange=()=>{const row=smallRows.find(r=>r.id===$('#moving-small-body').value);if(row)applySmallBody(row);else{objectSelected=false;trackAppearance();}};
$('#small-body-reload').onclick=()=>loadSmallBodies(true);
document.addEventListener('small-body-picked',e=>{const row=smallRows.find(r=>r.id===e.detail.id);if(row){applySmallBody(row);window.engineeringAudit?.('ui.small_body_selected',{category,id:row.id,name:row.name});}});
$('#non-celestial').addEventListener('input',e=>{if(['asteroid','comet'].includes(category)&&e.target.closest('.small-body-advanced')){smallSelection='';$('#moving-small-body').value='';$('#small-body-details').textContent='Manual orbital elements';}});


let demoToken=null,demoHeartbeat=null;
async function startDemo(){
 if(busy||tracking)return;
 busy=true;
 try{
  let objects;
  if(category==='solar')objects=demoRows.filter(r=>r.altitude_deg>0).map(r=>({body:r.body}));
  else objects=demoRows.filter(r=>r.altitude_deg>0).slice(0,500).map(r=>category==='satellite'?{tle:{name:r.name,line1:r.line1,line2:r.line2}}:{orbit:r.orbit});
  if(!objects.length)throw Error('No above-horizon objects on this chart.');
  if(objects.some(o=>o.body==='sun')&&!await confirmSolar({body:'sun'}))return;
  const d=await command('/api/astronomy/moving/demo/start',{objects,in_position_arcsec:Number($('#moving-window').value)});
  demoToken=d.token;clearInterval(demoHeartbeat);
  demoHeartbeat=setInterval(()=>{if(document.hidden||$('#non-celestial').hidden){stopDemo();return;}command('/api/astronomy/moving/demo/heartbeat',{token:demoToken}).catch(()=>{clearInterval(demoHeartbeat);demoToken=null;});},2000);
 }catch(e){$('#moving-error').textContent=e.message;}finally{busy=false;}
}
function stopDemo(){clearInterval(demoHeartbeat);if(demoToken){demoToken=null;void command('/api/astronomy/moving/demo/stop',{}).catch(()=>{});}}
new MutationObserver(()=>{if($('#non-celestial').hidden)stopDemo();}).observe($('#non-celestial'),{attributes:true,attributeFilter:['hidden']});
document.addEventListener('visibilitychange',()=>{if(document.hidden)stopDemo();});
document.addEventListener('mount-state',e=>{
 const o=e.detail.observatory;if(!o)return;
 const q=o.position_rms||{},d=o.moving_demo||{};
 $('#moving-rms-state').textContent=d.active?d.state.replace('_',' ').toUpperCase():o.tracking?'TRACKING':'IDLE';
 for(const [suffix,key] of [['az','azimuth_arcsec'],['alt','altitude_arcsec']])$('#moving-rms-'+suffix).textContent=Number.isFinite(q[key])?q[key].toFixed(2):o.tracking?'Acquiring':'Idle';
 $('#moving-demo-note').textContent=d.active||d.state==='error'?d.message:'LEO 15 s · Other objects 5 s';
 $('#moving-window').disabled=!!d.active;
 if(demoToken&&!d.active){clearInterval(demoHeartbeat);demoToken=null;}
});

const tableViewport=$('#moving-object-scroll'),tableSpace=$('#moving-object-space');
let tableRows=[],tableNodes=[],tablePaint=0;
function resetObjectTable(){tableNodes=[];tableRows=[];demoRows=[];tableSpace.replaceChildren();tableViewport.scrollTop=0;tableSpace.style.height='0px';}
function updateObjectTable(rows){if(performance.now()-tablePaint>500){tableRows=[...rows].sort((a,b)=>Number(b.altitude_deg>0)-Number(a.altitude_deg>0)||b.altitude_deg-a.altitude_deg);paintObjectTable();tablePaint=performance.now();}}
function paintObjectTable(){
 const start=Math.max(0,Math.floor(tableViewport.scrollTop/30)-2),end=Math.min(tableRows.length,start+Math.ceil(tableViewport.clientHeight/30)+5);
 tableSpace.style.height=tableRows.length*30+'px';
 while(tableNodes.length<end-start){const button=document.createElement('button');button.type='button';button.className='moving-object-row';button.innerHTML='<span></span><small></small>';button.onclick=()=>pickTableObject(button.dataset.index);button.oncontextmenu=e=>{const row=tableRows[Number(button.dataset.index)];showInterestingMenu(e,row?{...row,category:category==='solar'?'planet':category}:null);};tableNodes.push(button);tableSpace.append(button);}
 tableNodes.forEach((b,n)=>{const i=start+n,r=tableRows[i];b.hidden=i>=end||!r;if(b.hidden)return;const visible=r.altitude_deg>0;b.dataset.index=i;b.style.top=i*30+'px';b.disabled=false;b.classList.toggle('below-horizon',!visible);b.classList.toggle('selected',category==='satellite'?r.line1?.slice(2,7).trim()===$('#moving-line1').value.slice(2,7).trim():category==='solar'?objectSelected&&r.body===$('#moving-body').value:r.id===smallSelection);b.querySelector('span').textContent=(r.name||r.body||r.id)+' ('+r.altitude_deg.toFixed(1)+'°)';b.querySelector('small').textContent=visible?'Above horizon':'Below horizon';b.title=(r.name||r.body)+' · Alt '+r.altitude_deg.toFixed(2)+'°';});
}
function pickTableObject(index){const r=tableRows[Number(index)];if(!r)return;if(category==='satellite')document.dispatchEvent(new CustomEvent('satellite-picked',{detail:r}));else if(category==='solar'){$('#moving-body').value=r.body;objectSelected=true;trackAppearance();clear();}else applySmallBody(r);focusSkyObject(r);paintObjectTable();}
tableViewport.addEventListener('scroll',paintObjectTable,{passive:true});
const solarCatalogSky=new LocalSky(d=>{demoRows=d.objects;updateObjectTable(d.objects);setSatelliteCatalog(d);setSatelliteLoading('');},e=>{$('#moving-error').textContent=e.message;});
function startSolarCatalog(){if(category!=='solar'||$('#non-celestial').hidden||document.hidden)return;void solarCatalogSky.start('objects',[...$('#moving-body').options].map(o=>({id:o.value,body:o.value,name:o.textContent})));}
$('#moving-body').addEventListener('change',()=>{objectSelected=true;trackAppearance();});
document.addEventListener('small-body-picked',e=>{if(category==='solar'){const r=tableRows.find(r=>r.id===e.detail.id);if(r)pickTableObject(tableRows.indexOf(r));}});
new MutationObserver(()=>{if($('#non-celestial').hidden)solarCatalogSky.stop();else startSolarCatalog();}).observe($('#non-celestial'),{attributes:true,attributeFilter:['hidden']});
document.addEventListener('visibilitychange',()=>{if(document.hidden)solarCatalogSky.stop();else startSolarCatalog();});
for(const id of ['moving-select-close','moving-select-ok'])$('#'+id).onclick=()=>$('#moving-select-dialog').close();
selectCategory('solar');

let catalogWarming=false;
async function warmSatelliteCatalogs(){
 if(catalogWarming)return;catalogWarming=true;
 try{
  // Prime the default satellite list first, then small bodies, then other TLE groups.
  const first=$('#moving-category').value;
  const jobs=[['/api/astronomy/satellites/catalog/'+encodeURIComponent(first),catalogs,first],
   ...['asteroid','comet'].map(k=>['/api/astronomy/display-catalog/'+k,smallCatalogs,k]),
   ...[...$('#moving-category').options].filter(o=>o.value!==first).map(o=>['/api/astronomy/satellites/catalog/'+encodeURIComponent(o.value),catalogs,o.value])];
  for(const [path,cache,key] of jobs){if(catalogLifetime.signal.aborted)break;try{await cachedCatalog(path,cache,key);}catch{}await new Promise(resolve=>setTimeout(resolve,150));}
 }finally{catalogWarming=false;}
}
// Let the visible Astronomy map load before background catalog transfers.
document.addEventListener('astronomy-display-ready',()=>void warmSatelliteCatalogs(),{once:true});
// Foreground category changes still load immediately if Astronomy has not started.

function focusPickedTableRow(e){const index=tableRows.findIndex(r=>String(r.id??r.line1?.slice(2,7).trim())===String(e.detail.id));if(index<0)return;tableViewport.scrollTop=Math.max(0,index*30-(tableViewport.clientHeight-30)/2);paintObjectTable();tableNodes.find(b=>!b.hidden&&Number(b.dataset.index)===index)?.focus({preventScroll:true});}
for(const type of ['satellite-picked','small-body-picked'])document.addEventListener(type,focusPickedTableRow);

$('#moving-add-interesting').onclick=()=>{if(!objectSelected){selectionMessage();return;}try{const t=payload();const row=t.body?{id:t.body,name:$('#moving-body').selectedOptions[0].textContent,body:t.body,category:'planet'}:t.tle?{...t.tle,id:t.tle.line1.slice(2,7).trim(),category:'satellite'}:{id:smallSelection||t.orbit.name,name:t.orbit.name,orbit:t.orbit,category};addInteresting(row);}catch(e){$('#moving-error').textContent=e.message;}};

const detachedObjectTable=document.querySelector('#non-celestial .moving-object-table');
const alignObjectTable=()=>{const grid=document.querySelector('#non-celestial .moving-grid'),coordinates=document.querySelector('#non-celestial .moving-display');if(!grid||!coordinates||document.querySelector('#non-celestial').hidden)return;detachedObjectTable.style.marginTop=Math.max(0,(category==='solar'?coordinates:document.querySelector('#satellite-sky')).getBoundingClientRect().top-grid.getBoundingClientRect().top)+'px';};
new ResizeObserver(alignObjectTable).observe(document.querySelector('#non-celestial .moving-right'));
new ResizeObserver(alignObjectTable).observe(document.querySelector('#non-celestial .moving-display'));

new MutationObserver(()=>requestAnimationFrame(alignObjectTable)).observe(document.querySelector('#non-celestial .moving-right'),{subtree:true,attributes:true,attributeFilter:['hidden']});
new MutationObserver(()=>requestAnimationFrame(alignObjectTable)).observe(document.querySelector('#non-celestial'),{attributes:true,attributeFilter:['hidden','class','data-category']});
