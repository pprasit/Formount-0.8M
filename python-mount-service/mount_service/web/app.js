let mountEnablePending=false;
import {notify as toast} from './notifications.js';
import {mountStatus} from './mount-status.js';
import {openZeroPosition} from './zero-position.js';
const $ = s => document.querySelector(s);
const paths = {grid:'M3 3h7v7H3zM14 3h7v7h-7zM3 14h7v7H3zM14 14h7v7h-7z',activity:'M2 12h4l3-8 5 16 3-8h5',list:'M8 6h13M8 12h13M8 18h13M3 6h1M3 12h1M3 18h1',sliders:'M4 3v18M12 3v18M20 3v18M1 8h6M9 16h6M17 7h6',code:'m8 6-6 6 6 6m8-12 6 6-6 6m-3-15-2 18',chip:'M6 6h12v12H6zM9 1v5M15 1v5M9 18v5M15 18v5M1 9h5M1 15h5M18 9h5M18 15h5',aperture:'M12 3a9 9 0 1 0 0 18 9 9 0 0 0 0-18ZM12 3l4 7M20 8l-4 7M19 19h-8M10 21l-4-7M3 16l4-7M5 5h8',stop:'M5 5h14v14H5z',power:'M12 2v10M6 5a9 9 0 1 0 12 0',clock:'M12 3a9 9 0 1 0 0 18 9 9 0 0 0 0-18ZM12 7v5l3 2',pause:'M8 5v14M16 5v14',play:'m8 4 12 8-12 8z',download:'M12 3v12m-5-5 5 5 5-5M4 16v5h16v-5',chevron:'m6 9 6 6 6-6',x:'m6 6 12 12M18 6 6 18',info:'M12 3a9 9 0 1 0 0 18 9 9 0 0 0 0-18ZM12 11v6M12 7v1',plug:'M8 2v5M16 2v5M6 7h12v4a6 6 0 0 1-12 0ZM12 17v5',plus:'M12 5v14M5 12h14',minus:'M5 12h14',arrow:'M4 12h16m-6-6 6 6-6 6',circle:'M12 3a9 9 0 1 0 0 18 9 9 0 0 0 0-18ZM12 8v8M8 12h8'};
const icon = name => `<svg viewBox="0 0 24 24" aria-hidden="true"><path d="${paths[name] || paths.circle}"/></svg>`;
function icons(root=document) { root.querySelectorAll('[data-icon]').forEach(e=>e.innerHTML=icon(e.dataset.icon)); }
icons();
let historyStarted=false;
let state, samples=[], frozen=null, lastSequence=-1, socket, socketLive=false, lastMessage=0, reconnectDelay=500;
const cards = new Map();
const jogReleases = new Set();
const motionNotices = new Map();
function releaseJogs(){jogReleases.forEach(fn=>fn());}
const metrics = {position:{key:'position_deg',label:'Position',unit:'°',decimals:4},velocity:{key:'velocity_deg_s',label:'Velocity',unit:'°/s',decimals:4},current:{key:'current_a',label:'Current',unit:'A',decimals:3}};
async function request(path,body) { const r=await fetch(path,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(body)});const data=await r.json();if(!r.ok)throw Error(typeof data.detail==='string'?data.detail:'Invalid command parameters'); if(data.state)update(data.state);else if(data.axes)update(data);return data; }
async function command(id,action,value) { try{return await request(`/api/axes/${id}/command`,{action,...(value!==undefined?{value}:{}),lease:.8});}catch(e){toast(e.message,true);throw e;} }
function buildCards(axes) {
 $('#axis-grid').replaceChildren();
 for(const a of axes) {
  const card=document.createElement('article');card.className='axis-card';card.style.setProperty('--axis',a.color);card.dataset.axis=a.id;
  card.innerHTML=`<div class="axis-header"><div class="axis-title"><span class="axis-symbol">${icon(a.id==='rotator'?'aperture':'circle')}</span><div><h3>${a.name}</h3><small>AX${a.number} · ELMO DRIVE</small></div></div><div class="axis-header-actions"><span class="axis-mode">DEMO</span><button class="icon-button details" aria-label="${a.name} axis details">${icon('sliders')}</button></div></div><div class="axis-values"><div class="value-item primary"><span>POSITION</span><div><strong data-value="position_deg">—</strong><small>deg</small></div></div><div class="value-item"><span>VELOCITY</span><div><strong data-value="velocity_deg_s">—</strong><small>deg/s</small></div></div><div class="value-item"><span>CURRENT</span><div><strong data-value="current_a">—</strong><small>A</small></div></div></div><div class="chart-head"><select class="metric-select" aria-label="${a.name} chart metric"><option value="position">Position</option><option value="velocity">Velocity</option><option value="current">Current</option></select><div class="chart-legend"><i class="legend-line"></i>Actual <span class="command-legend" hidden><i class="legend-line command"></i>Command</span></div></div><div class="chart-wrap"><canvas aria-label="${a.name} live telemetry chart"></canvas><div class="chart-tooltip" hidden></div></div><div class="chart-foot"><span class="sample-tag">LIVE · 10 Hz stream</span><span class="chart-range">60s window</span></div><div class="axis-controls"><button class="motor-button">${icon('power')}<span>Enabled</span></button><span class="control-divider"></span><form class="goto-group"><span class="motion-group-title">Go to Target</span><label class="sr-only" for="target-${a.id}">${a.name} target degrees</label><input id="target-${a.id}" type="number" step="0.01" min="${a.minimum_deg}" max="${a.maximum_deg}" placeholder="Target °" required><button class="goto-button" type="submit">Go ${icon('arrow')}</button></form><span class="control-divider velocity-divider"></span><div class="move-velocity"><label for="velocity-${a.id}">Move Velocity</label><input id="velocity-${a.id}" class="move-velocity-input" type="number" min="0.001" max="${a.max_velocity}" step="any" value="0.5" aria-label="${a.name} move velocity degrees per second"><span class="velocity-unit">°/s</span><button type="button" class="jog-button" data-direction="-1" aria-label="Move ${a.name} CCW">CCW</button><button type="button" class="jog-button" data-direction="1" aria-label="Move ${a.name} CW">CW</button></div><span class="control-divider zero-divider"></span><div class="zero-position-group"><span class="motion-group-title">Zero Position</span><button type="button" class="zero-position-button">Set Zero</button></div><button class="axis-stop" aria-label="Stop ${a.name}">${icon('stop')}</button></div>`;
  $('#axis-grid').append(card);cards.set(a.id,card);
  for(const [name,input] of [['target',card.querySelector('.goto-group input')],['velocity',card.querySelector('.move-velocity-input')]]){
   const key=`mount-control-${a.id}-${name}`;
   try{const saved=localStorage.getItem(key);if(saved!==null&&(saved===''||Number.isFinite(Number(saved))))input.value=saved;}catch{}
   input.addEventListener('input',()=>{try{localStorage.setItem(key,input.value);}catch{}});
  }
  const select=card.querySelector('select');select.value=localStorage.getItem(`metric-${a.id}`)||'position';if(!metrics[select.value])select.value='position';select.onchange=()=>{localStorage.setItem(`metric-${a.id}`,select.value);draw(card,a.id);};
  card.querySelector('.details').onclick=()=>detail(a.id);
  card.querySelector('.motor-button').onclick=()=>{releaseJogs();return command(a.id,state.axes.find(x=>x.id===a.id).enabled?'disable':'enable').catch(()=>{});};
  card.querySelector('form').onsubmit=e=>{e.preventDefault();releaseJogs();command(a.id,'goto',Number(card.querySelector('input').value)).catch(()=>{});};
  card.querySelector('.axis-stop').onclick=()=>{releaseJogs();command(a.id,'stop').catch(()=>{});};
  let activeMove=null,moveChain=Promise.resolve();
  const velocityInput=card.querySelector('.move-velocity-input');
  function queueMove(action,value,token){moveChain=moveChain.catch(()=>{}).then(()=>action==='jog'&&activeMove!==token?undefined:command(a.id,action,value));return moveChain;}
  function release(){const active=activeMove;if(!active)return;activeMove=null;clearTimeout(active.timer);card.querySelectorAll('.jog-button').forEach(button=>{button.classList.remove('holding');button.setAttribute('aria-pressed','false');});void queueMove('stop').catch(()=>{});}
  jogReleases.add(release);velocityInput.addEventListener('input',release);
  for(const button of card.querySelectorAll('.jog-button')){button.setAttribute('aria-pressed','false');button.title='Hold to move; release to stop';const start=()=>{
   if(button.disabled||activeMove?.button===button)return;release();
   if(!velocityInput.reportValidity())return;const speed=Number(velocityInput.value);if(!Number.isFinite(speed)||speed<=0)return;
   const active={button,speed,direction:Number(button.dataset.direction),timer:null};activeMove=active;button.classList.add('holding');button.setAttribute('aria-pressed','true');
   async function renew(){if(activeMove!==active)return;if(document.hidden||!socketLive||Date.now()-lastMessage>2000||button.disabled){release();return;}try{await queueMove('jog',active.speed*active.direction,active);if(activeMove===active)active.timer=setTimeout(renew,250);}catch{release();}}
   void renew();
  };
   button.onpointerdown=e=>{if(e.button!==0||button.disabled)return;e.preventDefault();button.setPointerCapture(e.pointerId);start();};
   button.onpointerup=button.onpointercancel=button.onlostpointercapture=()=>{if(activeMove?.button===button)release();};
   button.onkeydown=e=>{if(![' ','Enter'].includes(e.key))return;e.preventDefault();if(!e.repeat)start();};
   button.onkeyup=e=>{if(![' ','Enter'].includes(e.key))return;e.preventDefault();if(activeMove?.button===button)release();};
   button.onblur=()=>{if(activeMove?.button===button)release();};
   button.onclick=e=>e.preventDefault();
   button.oncontextmenu=e=>e.preventDefault();
  }
  window.addEventListener('blur',release);document.addEventListener('visibilitychange',()=>{if(document.hidden)release();});
  card.querySelector('.zero-position-button').onclick=()=>{releaseJogs();void openZeroPosition(a);};
  const canvas=card.querySelector('canvas');canvas.onpointermove=e=>{const r=canvas.getBoundingClientRect();card.hover=(e.clientX-r.left)/r.width;draw(card,a.id);};canvas.onpointerleave=()=>{card.hover=null;card.querySelector('.chart-tooltip').hidden=true;draw(card,a.id);};
 }
 new ResizeObserver(()=>drawAll()).observe($('#axis-grid'));
}
function update(s) {
 if(state && state.drive.generation!==s.drive.generation){samples=[];frozen=null;$('#pause').innerHTML=icon('pause')+'<span>Pause graphs</span>';}
 let alert=document.querySelector('#drive-watchdog-alert');if(!alert){alert=document.createElement('div');alert.id='drive-watchdog-alert';alert.setAttribute('role','alert');document.body.append(alert);}const recovery=Object.entries(s.drive_watchdog||{}).filter(([,v])=>['lost','reconnecting'].includes(v.status));alert.hidden=!recovery.length;alert.textContent=recovery.map(([role,v])=>v.status==='lost'?`${role==='mount'?'Drive 1':'Drive 2'} lost connection — Check the cable and drive power. Automatic reconnect failed after 5 attempts.`:`${role==='mount'?'Drive 1':'Drive 2'}: Reconnecting (${v.attempt}/5)…`).join(' · ');
 state=s;lastMessage=Date.now();if(!cards.size)buildCards(s.axes);
 if(s.samples?.length){if(s.sequence<lastSequence){samples=[];lastSequence=-1;}samples.push(...s.samples.filter(p=>p.sequence>lastSequence));lastSequence=s.samples.at(-1).sequence;samples=samples.filter(p=>p.t>=s.elapsed_s-120).slice(-24000);}
 const connected=s.drive.connected;
 $('#connection-label').textContent=connected?(s.drive.mode==='simulation'?'Simulation':'Connected'):'Disconnected';$('#connection-dot').style.background=connected?'var(--accent)':'var(--red)';$('#connection-detail').textContent=connected?`${s.drive.port||'Virtual'} · ${s.drive.baud||'—'} baud · SN ${s.drive.identity}`:'Open Drive connection to reconnect';
 $('#enabled-count').textContent=s.axes.filter(a=>a.enabled&&a.valid).length;$('#enabled-detail').textContent=s.axes.every(a=>a.valid)?'All channels reporting':`${s.axes.filter(a=>a.valid).length} / 4 valid channels`;$('#poll-rate').textContent=connected?s.poll_hz.toFixed(1):'—';$('#cycle-detail').textContent=connected?`${s.cycle_ms.toFixed(2)} ms read · target ${s.drive.target_interval_ms} ms`:'Polling paused';$('#cycle-detail').title=`P95 ${s.drive.p95_cycle_ms} ms · overruns ${s.drive.overruns}`;$('#uptime').textContent=new Date(s.elapsed_s*1000).toISOString().slice(11,19);$('#footer-state').textContent=connected?'online':'disconnected';$('#footer-profile').textContent=s.drive.mode==='simulation'?'FORK-MOUNT · SIMULATED TELEMETRY':'FORK-MOUNT · REAL ELMO TELEMETRY';$('#stop-all').disabled=!connected||!s.drive.motion_available;const allEnabled=s.axes.every(a=>a.enabled);$('#enable-all').textContent=allEnabled?'Disable all':s.axes.some(a=>a.enabled)?'Enable remaining':'Enable all';$('#enable-all').classList.toggle('disable-action',allEnabled);$('#enable-all').disabled=!connected||(!allEnabled&&!s.axes.every(a=>a.valid));const mountAxes=s.axes.filter(a=>['azimuth','altitude','m3'].includes(a.id));$('#enable-mount').disabled=mountEnablePending||!connected||mountAxes.length!==3||!mountAxes.every(a=>a.valid)||mountAxes.every(a=>a.enabled);const enabledAxes=s.axes.filter(a=>a.enabled).length;$('#disable-all').hidden=enabledAxes===0||allEnabled;$('#disable-all').disabled=!connected;const status=mountStatus(s);$('#mount-status').textContent=status.text;$('#mount-status').dataset.state=status.state;
 for(const a of s.axes){if(a.motion_notice&&motionNotices.get(a.id)!==a.motion_notice)toast(a.motion_notice,true,8000);motionNotices.set(a.id,a.motion_notice||null);const c=cards.get(a.id);c.classList.toggle('invalid',!a.valid);c.querySelector('.axis-mode').textContent=a.valid?a.mode.toUpperCase():'NO DATA';c.querySelector('.axis-mode').classList.toggle('demo',a.mode==='demo');for(const [key,digits] of [['position_deg',4],['velocity_deg_s',4],['current_a',3]])c.querySelector(`[data-value="${key}"]`).textContent=a[key]===null?'—':a[key].toFixed(digits);c.querySelector('.move-velocity-input').max=a.max_velocity;const motor=c.querySelector('.motor-button');motor.classList.toggle('off',!a.enabled);motor.querySelector('span').textContent=a.enabled?'Disable':'Enable';motor.classList.toggle('disable-action',!!a.enabled);motor.setAttribute('aria-label',`${a.enabled?'Disable':'Enable'} ${a.name}`);c.querySelectorAll('.axis-controls button,.axis-controls input').forEach(b=>b.disabled=!connected||!s.drive.motion_available||!a.valid);c.querySelectorAll('.goto-group button,.goto-group input,.jog-button').forEach(b=>b.disabled=b.disabled||!a.enabled);c.querySelector('.axis-stop').disabled=!connected;motor.disabled=!connected||(!a.enabled&&!a.valid);if(s.drive.mode==='hardware'&&s.drive.unit_modes?.[a.id]!==5)c.querySelectorAll('.goto-group button,.goto-group input').forEach(b=>b.disabled=true);c.querySelector('.sample-tag').textContent=!a.valid?'STALE · last known values':frozen?'PAUSED · telemetry stays live':`LIVE · ${s.poll_hz.toFixed(0)} Hz · every acquired sample`;}
 const key=JSON.stringify(s.events);if($('#event-list').dataset.key!==key){$('#event-list').dataset.key=key;$('#event-list').replaceChildren();for(const ev of s.events.slice(0,12)){const row=document.createElement('div');row.className=`event-row ${ev.level}`;for(const [cl,txt] of [['event-time',new Date(ev.time).toISOString().slice(11,19)],['event-dot',''],['event-title',ev.title],['event-detail',ev.detail]]){const e=document.createElement('span');e.className=cl;e.textContent=txt;row.append(e);}$('#event-list').append(row);}$('#event-count').textContent=s.events.length;}
 if(s.observatory)document.dispatchEvent(new CustomEvent('mount-state',{detail:s}));
 document.dispatchEvent(new CustomEvent('telemetry-state',{detail:s}));
 drawAll();
}
let drawPending=false;
function drawAll(){
 if(drawPending)return;
 drawPending=true;
 requestAnimationFrame(()=>{drawPending=false;cards.forEach((c,id)=>{if(!c.hidden&&c.getBoundingClientRect().height)draw(c,id);});});
}
function draw(card,id){
 const canvas=card.querySelector('canvas'),r=canvas.getBoundingClientRect();if(!r.width||!state)return;const ratio=window.devicePixelRatio||1;if(canvas.width!==Math.round(r.width*ratio)||canvas.height!==Math.round(r.height*ratio)){canvas.width=Math.round(r.width*ratio);canvas.height=Math.round(r.height*ratio);}const ctx=canvas.getContext('2d');ctx.setTransform(ratio,0,0,ratio,0,0);ctx.clearRect(0,0,r.width,r.height);
 const metric=metrics[card.querySelector('.metric-select').value],span=Number($('#window').value),source=frozen||samples,end=frozen?frozen.at(-1)?.t:state.elapsed_s;if(end===undefined)return;const raw=source.filter(p=>p.t>=end-span&&p.t<=end&&p.axes[id]?.valid&&Number.isFinite(p.axes[id][metric.key]));const command=metric.key==='velocity_deg_s'&&raw.some(p=>p.axes[id].command_velocity!==null);card.querySelector('.command-legend').hidden=!command;card.querySelector('.chart-range').textContent=`${span}s window · ${metric.unit}`;
 const data=raw;
 card.dataset.plottedSamples=String(data.length);
 const values=raw.flatMap(p=>command?[p.axes[id][metric.key],p.axes[id].command_velocity]:[p.axes[id][metric.key]]).filter(Number.isFinite);let low=values.length?Math.min(...values):0,high=values.length?Math.max(...values):1;const pad=Math.max((high-low)*.18,metric.key==='position_deg'?.002:.01);low-=pad;high+=pad;
 const left=58,right=r.width-12,top=12,bottom=r.height-25,x=t=>left+(t-(end-span))/span*(right-left),y=v=>bottom-(v-low)/(high-low)*(bottom-top);ctx.font='10px "IBM Plex Mono", monospace';ctx.lineWidth=1;ctx.fillStyle='#8492a6';ctx.textAlign='right';
 for(let i=0;i<4;i++){const yy=top+(bottom-top)*i/3;ctx.strokeStyle='#29313d';ctx.beginPath();ctx.moveTo(left,yy);ctx.lineTo(right,yy);ctx.stroke();const val=high-(high-low)*i/3;ctx.fillText(val.toFixed(metric.key==='position_deg'?3:2),left-8,yy+3);}
 ctx.textAlign='center';for(let i=0;i<5;i++){const xx=left+(right-left)*i/4;ctx.fillText(i===4?'now':`−${Math.round(span*(1-i/4))}s`,xx,r.height-6);}
 const color=getComputedStyle(card).getPropertyValue('--axis').trim();
 if(data.length>1){ctx.beginPath();data.forEach((p,i)=>{const xx=x(p.t),yy=y(p.axes[id][metric.key]);i?ctx.lineTo(xx,yy):ctx.moveTo(xx,yy);});ctx.lineTo(x(data.at(-1).t),bottom);ctx.lineTo(x(data[0].t),bottom);ctx.closePath();const gradient=ctx.createLinearGradient(0,top,0,bottom);gradient.addColorStop(0,color+'28');gradient.addColorStop(1,color+'00');ctx.fillStyle=gradient;ctx.fill();
 const line=key=>{ctx.beginPath();let previous=null;for(const p of data){const v=p.axes[id][key];if(v===null||!Number.isFinite(v)){previous=null;continue;}if(previous===null||p.t-previous>.6)ctx.moveTo(x(p.t),y(v));else ctx.lineTo(x(p.t),y(v));previous=p.t;}ctx.stroke();};ctx.strokeStyle=color;ctx.lineWidth=1.7;line(metric.key);if(command){ctx.setLineDash([4,4]);ctx.strokeStyle='#99a8ba';ctx.lineWidth=1;line('command_velocity');ctx.setLineDash([]);}}
 if(!data.length){ctx.fillStyle='#8290a4';ctx.textAlign='center';ctx.fillText('Waiting for valid telemetry',r.width/2,r.height/2);}
 if(card.hover!=null&&data.length){const desired=end-span+Math.max(0,Math.min(1,(card.hover*r.width-left)/(right-left)))*span;const nearest=data.reduce((p,c)=>Math.abs(c.t-desired)<Math.abs(p.t-desired)?c:p);const xx=x(nearest.t);ctx.setLineDash([3,4]);ctx.strokeStyle='#617086';ctx.beginPath();ctx.moveTo(xx,top);ctx.lineTo(xx,bottom);ctx.stroke();ctx.setLineDash([]);const tip=card.querySelector('.chart-tooltip');tip.textContent=`${nearest.axes[id][metric.key].toFixed(metric.decimals)} ${metric.unit} · ${(end-nearest.t).toFixed(1)}s ago`;tip.style.left=`${Math.max(8,Math.min(r.width-195,xx+10))}px`;tip.hidden=false;}
}
function detail(id){const a=state.axes.find(a=>a.id===id);$('#detail-name').textContent=a.name;$('#detail-address').textContent=`AX${a.number} · AXIS CONFIGURATION`;$('#detail-content').replaceChildren();const list=document.createElement('div');list.className='detail-list';for(const [name,value] of [['Address',`AX${a.number}`],['Encoder',`${a.counts_per_rev.toLocaleString()} counts/revolution`],['Travel range',`${a.minimum_deg}° → ${a.maximum_deg}°`],['Maximum velocity',`${a.max_velocity}°/s`],...(id==='azimuth'?[['Boot sensor region',state.drive.azimuth_position?.boot_region??'Awaiting sensors'],['Absolute angle',`${state.drive.azimuth_position?.absolute_deg?.toFixed(4)??'—'}°`]]:[]),['Raw position',`${a.position_counts??'—'} counts`],['Telemetry age',`${a.age_ms??'—'} ms`],['Data quality',a.valid?'Valid':a.error||'Stale / disconnected']]){const row=document.createElement('div');row.className='detail-item';const label=document.createElement('span'),v=document.createElement('strong');label.textContent=name;v.textContent=value;row.append(label,v);list.append(row);}$('#detail-content').append(list);const note=document.createElement('p');note.className='notice';note.textContent=`Configured travel: ${a.minimum_deg}–${a.maximum_deg}°. Drive unit mode: ${state.drive.unit_modes?.[id]??'—'}; Goto requires UM=5. Hold Jog to move; release to stop.`;$('#detail-content').append(note);$('#axis-dialog').showModal();}
$('#pause').onclick=()=>{frozen=frozen?null:samples.slice();$('#pause').innerHTML=icon(frozen?'play':'pause')+`<span>${frozen?'Resume live':'Pause graphs'}</span>`;if(state)update(state);};$('#window').onchange=drawAll;
$('#stop-all').onclick=()=>{releaseJogs();request('/api/stop',{}).then(()=>toast('All four axes stopped')).catch(e=>toast(e.message,true));};
async function disableAll(){
 await request('/api/astronomy/tracking/stop',{});
 const failures=[];
 for(const a of state.axes){try{await request(`/api/axes/${a.id}/command`,{action:'disable'});}catch(e){failures.push(e.message);}}
 if(failures.length)throw Error(failures.join(' · '));
 toast('All four axes disabled');
}
$('#disable-all').onclick=async()=>{try{await disableAll();}catch(e){toast(e.message,true);}};
$('#enable-mount').onclick=async()=>{if(mountEnablePending)return;const axes=state?.axes.filter(a=>['azimuth','altitude','m3'].includes(a.id))||[];if(axes.length!==3||!axes.every(a=>a.valid))return;mountEnablePending=true;$('#enable-mount').disabled=true;try{for(const axis of axes){if(!axis.enabled)await request('/api/axes/'+axis.id+'/command',{action:'enable'});}toast('Mount enabled · Azimuth, Altitude and M3');}catch(e){toast(e.message,true);}finally{mountEnablePending=false;}};
$('#enable-all').onclick=async()=>{try{
 if(state?.axes.every(a=>a.enabled))await disableAll();
 else{await request('/api/enable-all',{});toast('All four axes enabled');}
}catch(e){toast(e.message,true);}};
document.querySelectorAll('.close-dialog').forEach(b=>b.onclick=()=>b.closest('dialog').close());document.querySelectorAll('dialog').forEach(d=>d.onclick=e=>{if(e.target===d&&e.offsetX>=0&&e.offsetY>=0){const r=d.getBoundingClientRect();if(e.clientX<r.left||e.clientX>r.right||e.clientY<r.top||e.clientY>r.bottom)d.close();}});
$('#toggle-events').onclick=()=>{const collapsed=$('#events').classList.toggle('collapsed');$('#toggle-events').innerHTML=icon('chevron')+`<span>${collapsed?'Expand':'Collapse'}</span>`;};
$('#transport-mode').onchange=()=>{const hardware=$('#transport-mode').value==='hardware';$('#hardware-fields').hidden=!hardware;$('#com-port').required=hardware;$('#drive-serial').required=hardware;};
$('#connection-form').onsubmit=async e=>{e.preventDefault();$('#connect-submit').disabled=true;$('#connection-error').textContent='';try{await request('/api/connect',{mode:$('#transport-mode').value,port:$('#com-port').value.trim(),baud:Number($('#baud').value),poll_interval_ms:Number($('#poll-interval').value),serial_number:$('#drive-serial').value.trim()});samples=[];lastSequence=-1;frozen=null;$('#pause').innerHTML=icon('pause')+'<span>Pause graphs</span>';$('#connection-dialog').close();toast('Drive connected');}catch(err){$('#connection-error').textContent=err.message;}finally{$('#connect-submit').disabled=false;}};
$('#disconnect').onclick=()=>request('/api/disconnect',{}).then(()=>{$('#connection-dialog').close();toast('Drive disconnected');}).catch(e=>toast(e.message,true));
document.addEventListener('telemetry-resize',drawAll);
function stream(){socket=new WebSocket(`${location.protocol==='https:'?'wss':'ws'}://${location.host}/ws/telemetry`);socket.onopen=()=>{socketLive=true;reconnectDelay=500;};socket.onmessage=e=>{try{update(JSON.parse(e.data));if(!historyStarted){historyStarted=true;loadHistory();}}catch(err){console.error(err);}};socket.onclose=()=>{socketLive=false;setTimeout(stream,reconnectDelay);reconnectDelay=Math.min(10000,reconnectDelay*2);};socket.onerror=()=>socket.close();}
setInterval(()=>{const live=socketLive&&Date.now()-lastMessage<2000;$('#stream-status').classList.toggle('offline',!live);$('#stream-status').innerHTML=`<i></i>${live?'Live stream':'Reconnecting'}`;$('#utc-clock').textContent=new Date().toISOString().slice(11,19)+' UTC';if(!live){$('#enable-mount').disabled=true;$('#enable-all').disabled=true;$('#disable-all').disabled=true;$('#mount-status').textContent='Telemetry unavailable';$('#mount-status').dataset.state='unknown';releaseJogs();cards.forEach(c=>{c.classList.add('invalid');c.querySelectorAll('.axis-controls button,.axis-controls input').forEach(b=>b.disabled=true);c.querySelector('.axis-stop').disabled=!state?.drive.connected;c.querySelector('.sample-tag').textContent='STALE · connection unavailable';});}},500);
async function loadHistory(){
 const generation=state.drive.generation;
 try{
  const response=await fetch('/api/history?seconds=120',{signal:AbortSignal.timeout(30000)});
  if(!response.ok)throw Error('History unavailable');
  const h=await response.json();
  if(!state||state.drive.generation!==generation)return;
  const history=h.samples||[];
  if(history.at(-1)?.t>state.elapsed_s+.1)return;
  // Keep frames received over WebSocket while the older data was downloading.
  const merged=new Map([...history,...samples].map(p=>[p.sequence,p]));
  samples=[...merged.values()].filter(p=>p.t>=state.elapsed_s-120).sort((a,b)=>a.sequence-b.sequence).slice(-24000);
  drawAll();
 }catch(e){console.warn('Earlier history unavailable; live telemetry remains connected.',e.message);}
}
// Open the session immediately. History is optional and never gates live data.
stream();

document.addEventListener('click',e=>{if(e.target.closest('[data-nav], [role="tab"]'))releaseJogs();},{capture:true});
