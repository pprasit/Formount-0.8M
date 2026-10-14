import {currentSkyTarget} from './sky-catalog.js';
import {confirmSolar} from './interesting-objects.js';
import {hms,dms,parseCoordinate,setCoordinate} from './coordinates.js';
import {notify as notice} from './notifications.js';
const $=s=>document.querySelector(s);
let current, cameraKey='', pointsKey='', jobsKey='', imageKey='';
const completed=new Set();
let pointPage=0,pointData=[];
function cameraView(points){$('#camera .observing-grid').hidden=points;$('#camera .calibration-heading').hidden=!points;$('#camera .points-table-wrap').hidden=!points;$('#points-prev').hidden=!points;$('#points-next').hidden=!points;$('#show-camera').classList.toggle('active',!points);$('#show-points').classList.toggle('active',points);}
$('#show-camera').onclick=()=>cameraView(false);$('#show-points').onclick=()=>cameraView(true);cameraView(false);
$('#points-prev').onclick=()=>{pointPage=Math.max(0,pointPage-1);renderPoints();};$('#points-next').onclick=()=>{pointPage=Math.min(Math.max(0,Math.ceil(pointData.length/6)-1),pointPage+1);renderPoints();};
async function post(path,body={}){const r=await fetch(path,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(body)});const d=await r.json();if(!r.ok)throw Error(typeof d.detail==='string'?d.detail:'Check the input values');return d;}
const target=()=>currentSkyTarget();
const fmt=(n,d=4)=>Number.isFinite(n)?n.toFixed(d):'—';
function bind(id,path,body,done){$(id).onclick=async()=>{const b=$(id);b.disabled=true;try{const request=body?body():{};if(path.endsWith('/tracking/start')&&!await confirmSolar(request))return;const d=await post(path,request);if(done)done(d);else notice(d.id?'Operation queued; status appears below.':'Command acknowledged');}catch(e){notice(e.message,true);}finally{b.disabled=false;}};}
function showTarget(d){$('#target-preview').textContent=`Az ${fmt(d.azimuth_deg)}° · Alt ${fmt(d.altitude_deg)}°\nEncoder ${fmt(d.encoder_azimuth_deg)}° / ${fmt(d.encoder_altitude_deg)}°\nParallactic ${fmt(d.parallactic_deg,2)}° · ${d.model_applied?'PointXP applied':'encoder geometry'} · ${d.slew_allowed?'within travel':'outside travel'}`;}

bind('#sky-goto','/api/astronomy/tracking/start',target,()=>notice('Target tracking started'));
let trackingActive=false;$('#sky-track').onclick=async()=>{const b=$('#sky-track');b.disabled=true;try{const request=trackingActive?{}:target();if(!trackingActive&&!await confirmSolar(request))return;await post('/api/astronomy/tracking/'+(trackingActive?'stop':'start'),request);}catch(e){notice(e.message,true);}finally{b.disabled=false;}};
bind('#camera-scan','/api/camera/scan');
bind('#camera-connect','/api/camera/connect',()=>({path:$('#camera-device').value}));
bind('#camera-disconnect','/api/camera/disconnect');
bind('#camera-capture','/api/camera/capture',()=>({exposure_s:Number($('#camera-exposure').value),binning:Number($('#camera-bin').value),gain:Number($('#camera-gain').value),dark:false}));
bind('#camera-abort','/api/camera/abort');
bind('#camera-solve','/api/calibration/solve',()=>({...target(),pixel_scale:Number($('#solve-scale').value)}));
async function loadPoints(){try{const r=await fetch('/api/calibration/points');if(!r.ok)throw Error('Points unavailable');const d=await r.json();pointData=d.points;renderPoints();}catch(e){notice(e.message,true);}}
function renderPoints(){pointPage=Math.min(pointPage,Math.max(0,Math.ceil(pointData.length/6)-1));$('#points-prev').disabled=pointPage===0;$('#points-next').disabled=(pointPage+1)*6>=pointData.length;$('#calibration-points').replaceChildren();for(const p of pointData.slice(pointPage*6,pointPage*6+6)){const row=document.createElement('tr');const cell=document.createElement('td');const check=document.createElement('input');check.type='checkbox';check.checked=p.included!==false;check.onchange=async()=>{try{await post(`/api/calibration/points/${p.id}`,{included:check.checked});p.included=check.checked;}catch(e){check.checked=!check.checked;notice(e.message,true);}};cell.append(check);row.append(cell);for(const value of [p.name,hms(p.solved_ra_hours),dms(p.solved_dec_deg),fmt(p.encoder_azimuth_deg),fmt(p.encoder_altitude_deg),p.capture_utc]){const td=document.createElement('td');td.textContent=value;row.append(td);}$('#calibration-points').append(row);}}
document.addEventListener('mount-state',e=>{current=e.detail;const o=current.observatory;if(!o)return;const sky=o.sky,m=o.pointing,c=o.camera;
 trackingActive=Boolean(o.tracking);$('#sky-track').textContent=trackingActive?'Stop tracking':'Start tracking';$('#sky-track').setAttribute('aria-pressed',String(trackingActive));$('#sky-track').classList.toggle('tracking-active',trackingActive);$('#tracking-status').textContent=o.tracking?(o.tracking_quality?.state||'acquiring').toUpperCase():'IDLE';if(o.tracking&&o.target?.rates_deg_s){const rates=o.target.rates_deg_s;$('#target-preview').textContent=`Tracking rates · Az ${fmt(rates.azimuth,6)}°/s · Alt ${fmt(rates.altitude,6)}°/s`;}$('#sky-site').textContent=sky.site?`${sky.site.latitude}° N · ${sky.site.longitude}° E · ${sky.site.elevation} m`:'';
 $('#model-metrics').textContent=m.active?`${m.used} points · ${m.terms} terms\nRMS ${fmt(m.rms_arcsec,2)} arcsec · max ${fmt(m.max_error_arcsec,2)} arcsec\n${m.backend}\n${m.path}`:m.error||'No fitted model active. Fit at least 9 solved points or load an existing .pxp.';
 $('#camera-status').textContent=c.exposing?'EXPOSING':c.connected?'CONNECTED':'NO CAMERA';$('#camera-error').textContent=Object.values(c.errors||{}).join(' · ');
 const key=JSON.stringify(c.devices);if(key!==cameraKey){cameraKey=key;const s=$('#camera-device'),old=s.value;s.replaceChildren(new Option(c.devices?.length?'Choose camera':'No cameras detected',''));for(const d of c.devices||[])s.add(new Option(d.name,d.key));if([...s.options].some(v=>v.value===old))s.value=old;else if(c.devices?.length===1)s.value=c.devices[0].key;}
 $('#camera-capture').disabled=!c.connected||c.exposing;$('#camera-solve').disabled=!c.last||c.exposing;$('#camera-abort').disabled=!c.exposing;
 if(c.last?.preview_url&&imageKey!==c.last.preview_url){imageKey=c.last.preview_url;$('#capture-image').src=imageKey;$('#capture-image').hidden=false;$('#capture-placeholder').hidden=true;$('#capture-info').textContent=`${c.last.width} × ${c.last.height} · ${c.last.exposure_s}s · ${c.last.capture_utc}`;}
 $('#point-count').textContent=o.calibration_points;const pk=`${o.calibration_points}:${o.jobs.filter(j=>j.kind==='plate solve'&&j.status==='complete').map(j=>j.id)}`;if(pk!==pointsKey){pointsKey=pk;loadPoints();}
 const jk=JSON.stringify(o.jobs.map(j=>[j.id,j.status,j.error]));if(jk!==jobsKey){jobsKey=jk;$('#observatory-jobs').replaceChildren();for(const j of o.jobs.slice(-5).reverse()){const row=document.createElement('div');row.className=`job-row ${j.status}`;row.textContent=`${j.kind} · ${j.status}${j.error?' · '+j.error:''}`;$('#observatory-jobs').append(row);if(j.status!=='running'&&!completed.has(j.id)){completed.add(j.id);if(j.status==='failed')notice(`${j.kind}: ${j.error}`,true);}}}
});
