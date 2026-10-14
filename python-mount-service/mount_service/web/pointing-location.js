import {loadBlindSpots,saveBlindSpots} from './blind-spots.js';
import {notify} from './notifications.js';

const $=s=>document.querySelector(s);

const stages=['pointing-intro','pointing-location','pointing-ntp','pointing-telescopes','pointing-blind-spots','pointing-telescope-form','pointing-imaging','pointing-location-ready','pointing-existing-form'];

let current='pointing-intro',transitioning=false,siteLoaded=false,map=null,pin=null,provider=null,mapLoading=null;

async function request(path,body){
 const names={'/api/pointing/image-session':'Starting New Model','/api/pointing/location':'Loading observatory location'};
 try{
  const response=await fetch(path,{...(body===undefined?{}:{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(body)}),...(names[path]?{signal:AbortSignal.timeout(15000)}:{})});
  const data=await response.json();if(!response.ok)throw Error(typeof data.detail==='string'?data.detail:'Check the location values');return data;
 }catch(e){if(e.name==='TimeoutError'){console.warn('Pointing request timed out:',path);throw Error((names[path]||'Pointing setup request')+' timed out after 15 seconds. Please try again.');}throw e;}
}


async function show(id){
 if(transitioning||id===current)return;transitioning=true;
 const old=$('#'+current),next=$('#'+id),reduced=matchMedia('(prefers-reduced-motion: reduce)').matches;
 const finish=async animation=>{try{await Promise.race([animation.finished,new Promise(resolve=>setTimeout(resolve,600))]);}catch(e){if(e.name!=='AbortError')throw e;}finally{animation.cancel();}};
 try{
  old.classList.add('is-leaving');if(!reduced)await finish(old.animate([{opacity:1,transform:'translateY(0)'},{opacity:0,transform:'translateY(-8px)'}],{duration:200,easing:'ease-in',fill:'forwards'}));
  for(const name of stages)$('#'+name).hidden=name!==id;
  current=id;old.classList.remove('is-leaving');
  if(!reduced)await finish(next.animate([{opacity:0,transform:'translateY(10px)'},{opacity:1,transform:'translateY(0)'}],{duration:280,easing:'ease-out'}));
 }finally{old.classList.remove('is-leaving');transitioning=false;}
 if(id==='pointing-ntp')void loadNtp();
 if(id==='pointing-location'){if(['osm','esri'].includes(provider))map?.invalidateSize();else if(provider==='google'&&map)google.maps.event.trigger(map,'resize');}
}

function coordinates(){return {latitude:Number($('#pointing-latitude').value),longitude:Number($('#pointing-longitude').value),elevation:Number($('#pointing-elevation').value)};}

function validCoordinates(){const s=coordinates();return $('#pointing-latitude').value!==''&&$('#pointing-longitude').value!==''&&Number.isFinite(s.latitude)&&Number.isFinite(s.longitude)&&Math.abs(s.latitude)<=90&&Math.abs(s.longitude)<=180;}

function select(latitude,longitude){$('#pointing-latitude').value=latitude.toFixed(6);$('#pointing-longitude').value=longitude.toFixed(6);movePin(false);document.dispatchEvent(new CustomEvent('pointing-location-picked',{detail:{latitude,longitude}}));}

function movePin(center){if(!map||!validCoordinates())return;const s=coordinates();if(['osm','esri'].includes(provider)){const ll=[s.latitude,s.longitude];pin.setLatLng(ll);if(center)map.panTo(ll);}else {const ll={lat:s.latitude,lng:s.longitude};pin.setCenter(ll);if(center)map.panTo(ll);}}

function script(url){return new Promise((resolve,reject)=>{const el=document.createElement('script');el.src=url;el.onload=resolve;el.onerror=()=>reject(Error('Map could not be loaded. You can enter coordinates manually.'));document.head.append(el);});}

async function initializeMap(){if(map){if(['osm','esri'].includes(provider))map.invalidateSize();return;}if(mapLoading)return mapLoading;

 mapLoading=(async()=>{const config=await request('/api/pointing/map-config'),s=coordinates();provider=config.provider;

 if(provider==='google'&&config.key){await new Promise((resolve,reject)=>{window.__pointingGoogleReady=()=>{delete window.__pointingGoogleReady;resolve();};window.gm_authFailure=()=>reject(Error('Google Maps authentication failed.'));script('https://maps.googleapis.com/maps/api/js?'+new URLSearchParams({key:config.key,callback:'__pointingGoogleReady',loading:'async',v:'weekly'})).catch(reject);});

   map=new google.maps.Map($('#pointing-location-map'),{center:{lat:s.latitude,lng:s.longitude},zoom:12,streetViewControl:false});pin=new google.maps.Circle({map,center:{lat:s.latitude,lng:s.longitude},radius:60,fillColor:'#00aa88',fillOpacity:.8,strokeColor:'#ffffff',strokeWeight:2});map.addListener('click',e=>{if(e.latLng)select(e.latLng.lat(),e.latLng.lng());});$('#pointing-map-status').textContent='Google Maps · Click to set latitude and longitude';

 }else if(['osm','esri'].includes(provider)){

   const link=document.createElement('link');link.rel='stylesheet';link.href='/assets/vendor/leaflet.css';const stylesReady=new Promise((resolve,reject)=>{link.onload=resolve;link.onerror=()=>reject(Error('Map styles could not be loaded'));});document.head.append(link);await Promise.all([stylesReady,script('/assets/vendor/leaflet.js')]);

   map=L.map('pointing-location-map',{zoomControl:true}).setView([s.latitude,s.longitude],12);

   const attribution='Tiles © Esri and contributors';

   const imagery=L.tileLayer('https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}',{maxZoom:18,attribution});

   const terrain=L.tileLayer('https://server.arcgisonline.com/ArcGIS/rest/services/World_Topo_Map/MapServer/tile/{z}/{y}/{x}',{maxZoom:18,attribution});

   const osm=L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png',{maxZoom:19,attribution:'&copy; <a href="https://www.openstreetmap.org/copyright" target="_blank" rel="noopener">OpenStreetMap</a> contributors'});

   const caption=()=>$('#pointing-map-status').textContent=(provider==='esri'?'Esri · '+(map.hasLayer(imagery)?'Satellite':'Map'):'OpenStreetMap')+' · Click to set latitude and longitude';

   const tiles=provider==='esri'?imagery:osm;tiles.addTo(map);

   if(provider==='esri')L.control.layers({'Satellite':imagery,'Map':terrain},null,{collapsed:false,position:'topright'}).addTo(map);

   pin=L.circleMarker([s.latitude,s.longitude],{radius:8,weight:3,color:'#ffffff',fillColor:'#00aa88',fillOpacity:1}).addTo(map);map.on('click',e=>select(e.latlng.lat,e.latlng.lng));

   for(const layer of [tiles,terrain]){layer.on('loading',()=>$('#pointing-map-status').textContent='Loading map…');layer.on('load',caption);}

   imagery.on('tileerror',()=>{if(map.hasLayer(imagery)){map.removeLayer(imagery);terrain.addTo(map);$('#pointing-map-status').textContent='Satellite imagery unavailable · Showing map instead';}});

   for(const layer of [osm,terrain])layer.on('tileerror',()=>$('#pointing-map-status').textContent='Some map tiles could not load. You can still enter coordinates manually.');

   map.on('baselayerchange',caption);caption();

 }else {$('#pointing-map-status').textContent='Map configuration is required. You can enter the coordinates manually.';}

 })().catch(e=>{$('#pointing-map-status').textContent=e.message;}).finally(()=>mapLoading=null);return mapLoading;

}

async function location(){if(!siteLoaded){try{const data=await request('/api/pointing/location');for(const key of ['latitude','longitude','elevation'])$('#pointing-'+key).value=data.location[key];siteLoaded=true;}catch(e){notify(e.message,true);return;}}await show('pointing-location');void initializeMap();}

$('#pointing-begin').onclick=async()=>{const button=$('#pointing-begin');button.disabled=true;button.textContent='Opening…';try{const state=await request('/api/pointing/image-session',{});document.dispatchEvent(new CustomEvent('pointing-workbench-state',{detail:state}));await location();}catch(e){notify(e.message,true);}finally{button.disabled=false;button.textContent='New Model';}};

$('#pointing-existing').onclick=()=>void show('pointing-existing-form');

$('#pointing-location-back').onclick=()=>void show('pointing-intro');$('#pointing-existing-back').onclick=()=>void show('pointing-intro');$('#pointing-location-edit').onclick=()=>void openImaging();

for(const id of ['latitude','longitude'])$('#pointing-'+id).addEventListener('change',()=>movePin(true));

$('#pointing-location').onsubmit=async e=>{e.preventDefault();if(!e.currentTarget.reportValidity())return;const next=$('#pointing-location-next');next.disabled=true;try{const {location:s}=await request('/api/pointing/location',coordinates());document.dispatchEvent(new Event('server-display-context-changed'));$('#pointing-location-summary').textContent=`Latitude ${s.latitude.toFixed(6)}° · Longitude ${s.longitude.toFixed(6)}° · Elevation ${s.elevation} m`;await show('pointing-ntp');}catch(error){notify(error.message,true);}finally{next.disabled=false;}};

$('#pointing-existing-form').onsubmit=async e=>{e.preventDefault();if(!e.currentTarget.reportValidity())return;const button=e.currentTarget.querySelector('[type=submit]');button.disabled=true;const status=$('#pointing-existing-status');try{status.textContent='Reading model…';await request('/api/pointing/review',{path:$('#pointing-existing-path').value.trim()});status.textContent='Model ready for review.';document.dispatchEvent(new CustomEvent('pointing-tab',{detail:'model'}));}catch(e){status.textContent='';notify(e.message,true);}finally{button.disabled=false;}};

new ResizeObserver(()=>{if(['osm','esri'].includes(provider)&&map&&!$('#pointing-location').hidden)map.invalidateSize();}).observe($('#pointing-location-map'));



let ntpResults=[],ntpBusy=false,ntpLoaded=false;

const ntpInput=$('#pointing-ntp-server');

function selectedNtp(){return ntpResults.find(r=>r.ok&&r.host===ntpInput.value.trim());}

function ntpControls(){for(const id of ['test','find'])$('#pointing-ntp-'+id).disabled=ntpBusy;$('#pointing-ntp-next').disabled=ntpBusy||!selectedNtp();ntpInput.disabled=ntpBusy;}

ntpInput.addEventListener('input',()=>{ntpControls();$('#pointing-ntp-status').textContent='Test the new address before continuing.';});

$('#pointing-ntp-back').onclick=()=>void location();

async function loadNtp(){if(ntpBusy)return;ntpBusy=true;ntpResults=[];renderNtp();ntpControls();$('#pointing-ntp-status').textContent='Loading saved time server…';try{if(!ntpLoaded){const data=await request('/api/pointing/ntp');if(data.ntp?.host)ntpInput.value=data.ntp.host;const clock=data.clock;if(clock)$('#pointing-clock-status').textContent=clock.state==='synchronized'?`Windows clock synchronized · ${clock.source}`:clock.source?`Windows clock source: ${clock.source} · Next applies the selected server.`:clock.message||'';ntpLoaded=true;}}catch(e){$('#pointing-ntp-status').textContent=e.message;ntpBusy=false;ntpControls();return;}ntpBusy=false;await measureNtp(false);}

function renderNtp(){const box=$('#pointing-ntp-results');box.replaceChildren();for(const r of ntpResults){const row=document.createElement('div');row.className='pointing-ntp-result';const info=document.createElement('div'),name=document.createElement('strong'),details=document.createElement('p');name.textContent=r.host;details.textContent=r.ok?`Estimated error ±${r.estimated_error_ms.toFixed(2)} ms · RTT ${r.rtt_ms.toFixed(2)} ms · Clock offset ${r.offset_ms.toFixed(2)} ms · Jitter ${r.jitter_ms.toFixed(2)} ms · Stratum ${r.stratum} · ${r.samples} samples`:r.error;info.append(name,details);row.append(info);if(r.ok){const use=document.createElement('button');use.type='button';use.className='button quiet';use.textContent=r.host===ntpInput.value?'Selected':'Select';use.onclick=()=>{ntpInput.value=r.host;renderNtp();ntpControls();$('#pointing-ntp-status').textContent='Server selected · Ready to continue.';};row.append(use);}box.append(row);}}

async function measureNtp(find){if(ntpBusy)return;if(!find&&!ntpInput.reportValidity())return;ntpBusy=true;ntpControls();$('#pointing-ntp-status').textContent=find?'Finding a time server… Testing nearby public services.':'Measuring server…';try{const data=await request('/api/pointing/ntp/'+(find?'find':'test'),find?{}:{server:ntpInput.value.trim()});ntpResults=data.results;if(data.best){ntpInput.value=data.best.host;$('#pointing-ntp-status').textContent=find?'Selected the lowest estimated error among responding servers.':'Server responded · Ready to continue.';}else{$('#pointing-ntp-status').textContent='No synchronized NTP reply. Check the address and network access to UDP 123.';}renderNtp();}catch(e){ntpResults=[];renderNtp();$('#pointing-ntp-status').textContent=e.message;}finally{ntpBusy=false;ntpControls();}}

$('#pointing-ntp-test').onclick=()=>void measureNtp(false);$('#pointing-ntp-find').onclick=()=>void measureNtp(true);

$('#pointing-ntp').onsubmit=async e=>{e.preventDefault();if(ntpBusy||!selectedNtp())return;ntpBusy=true;ntpControls();$('#pointing-ntp-status').textContent='Setting Windows time server and synchronizing…';try{const data=await request('/api/pointing/ntp',{server:ntpInput.value});document.dispatchEvent(new Event('server-display-context-changed'));$('#pointing-setup-summary').textContent=$('#pointing-location-summary').textContent+` · NTP ${data.ntp.host} · Estimated error ±${data.ntp.estimated_error_ms.toFixed(2)} ms`;await loadTelescopes();await show('pointing-telescopes');}catch(e){$('#pointing-ntp-status').textContent=e.message;}finally{ntpBusy=false;ntpControls();}};



const telescopeFields=[

 ['name','Telescope name','text',true,'e.g. Main telescope'],

 ['focal_length_mm','Focal length (mm)','number',true,'Native focal length'],

 ['aperture_mm','Aperture (mm)','number',false,'Clear diameter of the main lens or mirror'],

 ['optical_design','Optical design','text',false,'e.g. Refractor, Newtonian, Ritchey–Chrétien, SCT'],

 ['backfocus_mm','Back focus (mm)','number',false,'Use the reference surface specified by the manufacturer'],

 ['obstruction_mm','Central obstruction diameter (mm)','number',false,'0 for an unobstructed telescope'],

 ['tube_length_mm','Tube length (mm)','number',false,''],

 ['weight_kg','Optical tube weight (kg)','number',false,''],

 ['mounting_position','Mount position / role','text',false,'e.g. Main, guide, left or right'],

 ['notes','Notes','text',false,'']

];

let telescopes=[],editingTelescope=null;

for(const [key,label,type,required,hint] of telescopeFields){const field=document.createElement('label');field.className='field';field.textContent=label+(required?' *':'');const input=document.createElement(key==='notes'?'textarea':'input');if(key==='notes'){input.rows=2;field.classList.add('ota-notes');}input.id='ota-'+key;input.name=key;if(key!=='notes')input.type=type;input.required=required;input.placeholder=hint;if(type==='number'){input.step='any';input.min=['obstruction_mm','backfocus_mm'].includes(key)?0:0.000001;}else input.maxLength=key==='notes'?2000:key==='manufacturer_model'?150:100;field.append(input);$('#pointing-telescope-fields').append(field);input.addEventListener('input',opticalSummary);}

function opticalSummary(){const focal=Number($('#ota-focal_length_mm').value),aperture=Number($('#ota-aperture_mm').value);$('#pointing-telescope-optics').textContent=focal>0?(aperture>0?`Focal ratio: f/${(focal/aperture).toFixed(2)}`:'Enter aperture to calculate f-ratio'):'';}

async function loadTelescopes(){const data=await request('/api/pointing/telescopes');telescopes=data.telescopes;renderTelescopes();}

function renderTelescopes(){const list=$('#pointing-telescope-list');list.replaceChildren();for(const t of telescopes){const row=document.createElement('div');row.className='pointing-ntp-result';const info=document.createElement('div'),title=document.createElement('strong'),details=document.createElement('p');title.textContent=t.name;details.textContent=`Focal length ${t.focal_length_mm} mm`+(t.aperture_mm?` · Aperture ${t.aperture_mm} mm · f/${(t.focal_length_mm/t.aperture_mm).toFixed(2)}`:'')+(t.mounting_position?' · '+t.mounting_position:'');info.append(title,details);const edit=document.createElement('button');edit.type='button';edit.className='button quiet';edit.textContent='Edit';edit.onclick=()=>void openTelescope(t);row.append(info,edit);list.append(row);}$('#pointing-telescope-empty').hidden=telescopes.length>0;$('#pointing-telescope-add').hidden=telescopes.length===0;$('#pointing-telescopes-next').disabled=!telescopes.length;}

async function openTelescope(t=null){editingTelescope=t?.id||null;$('#pointing-telescope-form').reset();for(const [key] of telescopeFields)$('#ota-'+key).value=t?.[key]??(key==='optical_multiplier'?1:'');$('#pointing-telescope-title').textContent=t?'Edit Telescope':'Create Telescope';opticalSummary();await show('pointing-telescope-form');}

$('#pointing-telescope-first').onclick=()=>void openTelescope();$('#pointing-telescope-add').onclick=()=>void openTelescope();$('#pointing-telescope-cancel').onclick=()=>void show('pointing-telescopes');$('#pointing-telescopes-back').onclick=()=>void show('pointing-ntp');

function telescopeMessage(message){$('#telescope-message-text').textContent=message;const dialog=$('#telescope-message-dialog');if(!dialog.open)dialog.showModal();}

$('#pointing-telescope-form').onsubmit=async e=>{e.preventDefault();if(!e.currentTarget.checkValidity()){const invalid=e.currentTarget.querySelector(':invalid');telescopeMessage((invalid?.closest('label')?.firstChild?.textContent||'Telescope specification')+': '+invalid.validationMessage);return;}const button=$('#pointing-telescope-save');button.disabled=true;const previous=telescopes.find(t=>t.id===editingTelescope);const specifications={manufacturer_model:previous?.manufacturer_model||'',optical_multiplier:previous?.optical_multiplier??1};for(const [key,,type] of telescopeFields){const v=$('#ota-'+key).value.trim();specifications[key]=type==='number'?(v?Number(v):key==='optical_multiplier'?1:null):v;}try{const response=await fetch('/api/pointing/telescopes'+(editingTelescope?'/'+editingTelescope:''),{method:editingTelescope?'PUT':'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(specifications)});const data=await response.json();if(!response.ok)throw Error(typeof data.detail==='string'?data.detail:Array.isArray(data.detail)?data.detail.map(d=>d.msg).join('; '):'Check the telescope specifications');await loadTelescopes();$('#pointing-telescope-status').textContent='Telescope saved.';if(editingTelescope)await show('pointing-telescopes');else await openImaging(data.telescope.id);}catch(e){telescopeMessage(e.message);}finally{button.disabled=false;}};

$('#pointing-telescopes-next').onclick=async()=>{try{await show('pointing-blind-spots');await loadBlindSpots();}catch(e){notify(e.message,true);}};
$('#blind-back').onclick=()=>void show('pointing-telescopes');
$('#blind-next').onclick=async()=>{const b=$('#blind-next');b.disabled=true;try{await saveBlindSpots();await openImaging();}catch(e){notify(e.message,true);}finally{b.disabled=false;}};



let imagingCamera=null,imagingBusy=false,imagingCapturing=false,imagingAborting=false,imagingSaved=null,imagingDeviceKey='',imagingNotes='',imagingTestFrame=null;

function imagingValues(){return {name:$('#imaging-name').value.trim(),telescope_id:$('#imaging-telescope').value,device_key:$('#imaging-device').value,pixel_um:Number($('#imaging-pixel').value),exposure_s:Number($('#imaging-exposure').value),binning:Number($('#imaging-bin').value),gain:Number($('#imaging-gain').value),notes:imagingNotes};}

function imagingScale(){const t=telescopes.find(t=>t.id===$('#imaging-telescope').value),v=imagingValues();$('#imaging-scale').textContent=t&&v.pixel_um>0?`Plate scale: ${(206.264806247*v.pixel_um*v.binning/(t.focal_length_mm*(t.optical_multiplier||1))).toFixed(3)} arcsec / pixel`:'';}

function imagingControls(){const chosen=imagingCamera?.devices?.find(d=>d.key===$('#imaging-device').value),pixel=$('#imaging-pixel');pixel.readOnly=!!chosen?.simulated;if(chosen?.simulated){pixel.value=chosen.pixel_um;pixel.title='Simulation preset - filled automatically';}else pixel.title='';const c=imagingCamera?.connected,selected=c&&c.key===$('#imaging-device').value,exposing=imagingCamera?.exposing;for(const id of ['scan','back'])$('#imaging-'+id).disabled=imagingBusy||exposing;$('#imaging-connect').disabled=imagingBusy||exposing||!$('#imaging-device').value||selected;$('#imaging-disconnect').disabled=imagingBusy||!c||exposing;const capture=imagingCapturing||exposing;const test=$('#imaging-test');test.textContent=imagingAborting?'Aborting-':capture?'Abort exposure':'Capture test frame';test.classList.toggle('danger',!!capture);test.classList.toggle('quiet',!capture);test.disabled=imagingAborting||(!capture&&(imagingBusy||!selected));$('#imaging-save').disabled=imagingBusy||exposing||!selected;for(const id of ['name','telescope','device','pixel','exposure','bin'])$('#imaging-'+id).disabled=imagingBusy||exposing;$('#imaging-gain').disabled=imagingBusy||exposing||!c?.gain_supported;}

function renderImaging(c){imagingCamera=c;if(!c.exposing&&!imagingCapturing)imagingAborting=false;const select=$('#imaging-device'),old=select.value;select.replaceChildren(new Option(c.devices?.length?'Choose a camera':'No cameras detected',''));for(const d of c.devices||[])select.add(new Option(`${d.name} · ${d.vendor.toUpperCase()}${d.serial?' · '+d.serial:''}`,d.key));if(old&&[...select.options].some(o=>o.value===old))select.value=old;else if(c.connected)select.value=c.connected.key;else if(c.devices?.length===1)select.value=c.devices[0].key;

 const diagnostics=$('#imaging-sdk-status');diagnostics.replaceChildren();for(const [vendor,error] of Object.entries(c.errors||{})){const row=document.createElement('p');row.textContent=vendor.toUpperCase()+': '+error;diagnostics.append(row);}

 const d=c.connected;$('#imaging-details').textContent=d?`${d.name}\n${d.width} × ${d.height} px · ${d.pixel_um?.toFixed(2)||'—'} µm`:'Connect a camera to preview.';

 $('#imaging-status').textContent=c.exposing?'Exposing / reading out…':d?'Connected · '+d.name:c.devices?.some(d=>d.simulated)?'No USB camera detected - simulation available':`${c.devices?.length||0} camera(s) detected on the mount computer`;

 if(d&&imagingDeviceKey!==d.key){imagingDeviceKey=d.key;const saved=imagingSaved&&(imagingSaved.serial?imagingSaved.serial===d.serial&&imagingSaved.vendor===d.vendor:imagingSaved.device_key===d.key)?imagingSaved:null;const detected=Number.isFinite(d.pixel_um)&&d.pixel_um>0&&d.pixel_um<=100;$('#imaging-pixel').value=detected?d.pixel_um:saved?.pixel_um??'';$('#imaging-pixel-note').textContent=detected?'Detected from camera - unbinned sensor pixel size':'Pixel size unavailable from camera - enter the sensor specification';$('#imaging-name').value=saved?.name||d.name;const bins=d.bins?.length?d.bins:Array.from({length:8},(_,i)=>i+1);$('#imaging-bin').replaceChildren(...Array.from(new Set([...Array.from({length:8},(_,i)=>i+1),...bins])).sort((a,b)=>a-b).map(n=>{const supported=bins.includes(n),o=new Option(`${n} \u00d7 ${n}${supported?'':' - unavailable on this camera'}`,n);o.disabled=!supported;return o;}));$('#imaging-bin').value=String(bins.includes(saved?.binning)?saved.binning:bins.includes(1)?1:bins[0]);$('#imaging-exposure').value=saved?.exposure_s??Math.min(600,d.exposure_max_s??600,Math.max(1,d.exposure_min_s??.001));$('#imaging-exposure').min=Math.max(.001,d.exposure_min_s??.001);$('#imaging-exposure').max=Math.min(600,d.exposure_max_s??600);const gain=d.controls?.['0'];$('#imaging-gain').min=gain?.minimum??0;$('#imaging-gain').max=gain?.maximum??1000;$('#imaging-gain').value=d.gain_supported?(saved?.gain??gain?.default??0):0;imagingNotes=saved?.notes||'';}

 if(!d){imagingDeviceKey='';$('#imaging-pixel-note').textContent='Filled automatically when the connected camera reports its pixel size';}$('#imaging-gain-note').textContent=d&&!d.gain_supported?'Numeric gain is unavailable for this camera. Native gain settings are preserved.':'';

 const preview=imagingTestFrame;if(preview?.preview_url){$('#imaging-preview').src=preview.preview_url;$('#imaging-preview').hidden=false;$('#imaging-preview-empty').hidden=true;$('#imaging-preview-info').textContent=`Auto Stretch · ${preview.exposure_s} s${preview.simulated?` · ${preview.simulated_stars||0} stars`:''}`;}else{$('#imaging-preview').hidden=true;$('#imaging-preview-empty').hidden=false;$('#imaging-preview-info').textContent='';}imagingControls();imagingScale();}

function renderCaptureProgress(p={phase:'Preparing'}){
 const box=$('#imaging-capture-progress'),bar=$('#imaging-capture-bar'),phase=$('#imaging-capture-phase'),time=$('#imaging-capture-time'),note=$('#imaging-capture-note');
 box.hidden=false;const name=p.phase||'Preparing';phase.textContent=({Exposing:'Expose',Readout:'Read Out',Processing:'Processing / Saving FITS',Complete:'Complete',Cancelled:'Cancelled',Error:'Failed'})[name]||name;
 time.textContent='';note.textContent='';
 if(name==='Exposing'){bar.value=Math.min(99,(p.fraction||0)*100);time.textContent=`${(p.elapsed_s||0).toFixed(1)} / ${p.exposure_s||0} s`;}
 else if(['Preparing','Readout','Processing','Loading preview','Aborting'].includes(name)){bar.removeAttribute('value');if(name==='Readout'){time.textContent=`${(p.readout_s??p.elapsed_s??0).toFixed(1)} s`;note.textContent=p.readout_estimated?'Readout timing estimated by the camera driver.':'';}}
 else{bar.value=name==='Complete'?100:0;time.textContent=name==='Complete'?'100%':'';note.textContent=p.error||'';}
}
async function cameraJob(path,body){const job=await request(path,body||{});for(;;){const result=await request('/api/camera/jobs/'+job.id);if(path==='/api/camera/capture'&&result.status==='running'){const c=await request('/api/camera/status');renderCaptureProgress(c.acquisition);}if(result.status==='failed')throw Error(result.error||'Camera operation failed');if(result.status==='cancelled')throw Error('Camera operation cancelled');if(result.status==='complete')return result.result;await new Promise(resolve=>setTimeout(resolve,400));}}

async function imagingOperation(fn){if(imagingBusy)return;imagingBusy=true;imagingControls();try{await fn();}catch(e){notify(e.message,true);}finally{try{renderImaging(await request('/api/camera/status'));}catch(e){notify(e.message,true);}imagingBusy=false;imagingControls();}}

async function openImaging(telescopeId){$('#imaging-capture-progress').hidden=true;imagingTestFrame=null;$('#imaging-preview').hidden=true;$('#imaging-preview').removeAttribute('src');$('#imaging-preview-empty').hidden=false;$('#imaging-preview-info').textContent='';try{if(!telescopes.length)return;const {imaging}=await request('/api/pointing/imaging');imagingSaved=imaging;imagingDeviceKey='';$('#imaging-telescope').replaceChildren(...telescopes.map(t=>new Option(t.name,t.id)));$('#imaging-telescope').value=telescopeId||imaging?.telescope_id||telescopes[0].id;if(!$('#imaging-telescope').value)$('#imaging-telescope').value=telescopes[0].id;await show('pointing-imaging');renderImaging(await request('/api/camera/status'));}catch(e){notify(e.message,true);}}

$('#imaging-back').onclick=()=>void show('pointing-blind-spots');

$('#imaging-device').addEventListener('change',()=>{imagingControls();imagingScale();});for(const id of ['telescope','pixel','bin'])$('#imaging-'+id).addEventListener('input',imagingScale);

$('#imaging-scan').onclick=()=>imagingOperation(async()=>{await cameraJob('/api/camera/scan');notify('USB camera scan completed');});

$('#imaging-connect').onclick=()=>imagingOperation(async()=>{await cameraJob('/api/camera/connect',{path:$('#imaging-device').value});notify('Camera connected');});

$('#imaging-disconnect').onclick=()=>imagingOperation(async()=>{await cameraJob('/api/camera/disconnect');imagingTestFrame=null;notify('Camera disconnected');});

$('#imaging-test').onclick=async()=>{

 if(imagingCapturing||imagingCamera?.exposing){if(imagingAborting)return;imagingAborting=true;imagingControls();try{await request('/api/camera/abort',{});notify('Exposure abort requested');}catch(e){imagingAborting=false;notify(e.message,true);imagingControls();}return;}

 if(imagingBusy||!$('#pointing-imaging').reportValidity())return;

 const v=imagingValues();imagingCapturing=true;renderCaptureProgress({phase:'Preparing'});

 await imagingOperation(async()=>{try{await cameraJob('/api/camera/capture',{exposure_s:v.exposure_s,binning:v.binning,gain:v.gain,dark:false,telescope_id:v.telescope_id});const completed=await request('/api/camera/status');imagingTestFrame=completed.last;renderCaptureProgress({phase:'Complete'});notify(imagingCamera?.connected?.simulated?'Simulated test frame saved as FITS':'Test frame captured and saved as FITS');}catch(e){renderCaptureProgress({phase:imagingAborting&&/cancel|abort/i.test(e.message)?'Cancelled':'Error',error:e.message});if(!imagingAborting||!/cancel|abort/i.test(e.message))throw e;}finally{imagingCapturing=false;imagingAborting=false;}});

};

$('#pointing-imaging').onsubmit=async e=>{e.preventDefault();if(imagingBusy||!e.currentTarget.reportValidity())return;await imagingOperation(async()=>{const {imaging}=await request('/api/pointing/imaging',imagingValues());imagingSaved=imaging;document.dispatchEvent(new CustomEvent('imaging-configured',{detail:imaging}));$('#pointing-setup-summary').textContent=$('#pointing-setup-summary').textContent.split(' · Telescopes:')[0]+' · Telescopes: '+telescopes.map(t=>t.name).join(', ')+` · Camera: ${imaging.name} · ${imaging.pixel_scale.toFixed(3)} arcsec/pixel`;notify('Imaging equipment saved');await show('pointing-location-ready');});};

document.addEventListener('mount-state',e=>{if(current==='pointing-imaging'&&!imagingBusy&&e.detail.observatory?.camera)renderImaging(e.detail.observatory.camera);});

