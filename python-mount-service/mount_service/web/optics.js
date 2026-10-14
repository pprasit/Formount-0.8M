const $=s=>document.querySelector(s);
const roles={focuser1:['Focuser',1,'counts'],rotator:['Field Rotator',1,'deg'],focuser2:['Focuser',2,'counts'],rotator2:['Field Rotator',2,'counts']};
let state,busy=false,reading=false,last=0;
async function api(path,body){const r=await fetch(path,body===undefined?{}:{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(body)});const d=await r.json();if(!r.ok)throw Error(d.detail||'Command failed');return d;}
for(const [role,[name,port,baseUnit]] of Object.entries(roles)){
 const focus=role.startsWith('focuser'),unit=focus?'mm':baseUnit;
 const card=document.createElement('section');card.className='optics-axis';card.dataset.role=role;
 card.innerHTML=`<header><div class="optics-axis-name"><h4>${name}</h4><button type="button" class="optics-gear" data-settings aria-label="${name} Port ${port} settings"><svg viewBox="0 0 24 24" aria-hidden="true"><path d="M9 3h6l1 3 3 1 2 5-2 5-3 1-1 3H9l-1-3-3-1-2-5 2-5 3-1z"/><circle cx="12" cy="12" r="3"/></svg></button></div><button type="button" class="button quiet" data-optics-action="enable">Enable</button></header><div class="optics-readout"><strong data-position>—</strong><span>${unit==='deg'?'°':unit}</span></div><p data-binding class="muted"></p><p data-limits class="muted"></p><div class="optics-inputs"><label class="field">Step (${unit})<input data-step type="number" min="0.000001" step="any" value="${focus?.1:unit==='deg'?1:100}"></label><label class="field">Speed (${unit}/s)<input data-speed type="number" min="0.000001" step="any" value="${focus?.1:unit==='deg'?1:100}"></label></div><div class="optics-buttons"><button type="button" class="button quiet" data-optics-action="minus">− Step</button><button type="button" class="button quiet" data-optics-action="plus">+ Step</button><button type="button" class="button danger" data-optics-action="stop">Stop</button></div><form class="optics-target"><label class="field">Target (${unit})<input data-target type="number" step="any" required></label><button class="button primary" type="submit">Go to</button></form>`;
 if(focus){card.classList.add('optics-focuser');const slider=document.createElement('div');slider.className='focus-position-slider';slider.innerHTML='<label>Current position (mm)<input type="range" data-position-slider min="0" max="1" step="any" value="0" disabled aria-label="Focuser Port '+port+' current position in millimetres"></label><div class="focus-slider-limits"><span data-slider-min>Minimum — mm</span><span data-slider-max>Maximum — mm</span></div>';card.querySelector('[data-limits]').after(slider);}
 $('#optics-port'+port).append(card);
 card.querySelector('[data-settings]').onclick=()=>openAxisSettings(role);
 card.querySelector('form').onsubmit=e=>{e.preventDefault();void command(role,'goto',{value:toBase(role,Number(card.querySelector('[data-target]').value)),speed:Number(card.querySelector('[data-speed]').value)/factor(role)});};
 for(const button of card.querySelectorAll('[data-optics-action]'))button.onclick=()=>{let action=button.dataset.opticsAction,extra={};if(action==='enable')action=state?.axes[role]?.enabled?'disable':'enable';if(action==='plus'||action==='minus'){const step=Number(card.querySelector('[data-step]').value);if(!Number.isFinite(step)||step<=0){$('#optics-message').textContent='Enter a positive step';return;}extra={step:step*(action==='minus'?-1:1)/factor(role),speed:Number(card.querySelector('[data-speed]').value)/factor(role)};action='goto';}void command(role,action,extra);};
}
for(const port of [1,2]){
 const container=$('#optics-port'+port),cards=[...container.querySelectorAll('.optics-axis')],tabs=document.createElement('nav');tabs.className='uniform-tabs optics-port-tabs';tabs.setAttribute('role','tablist');tabs.setAttribute('aria-label','Port '+port+' equipment');
 function select(index){cards.forEach((card,i)=>{card.hidden=i!==index;card.setAttribute('role','tabpanel');card.id='optics-equipment-'+card.dataset.role;card.setAttribute('aria-labelledby','optics-tab-'+card.dataset.role);const button=tabs.children[i];button.classList.toggle('active',i===index);button.setAttribute('aria-selected',String(i===index));button.tabIndex=i===index?0:-1;});}
 cards.forEach((card,index)=>{const button=document.createElement('button');button.type='button';button.textContent=roles[card.dataset.role][0];button.id='optics-tab-'+card.dataset.role;button.setAttribute('role','tab');button.setAttribute('aria-controls','optics-equipment-'+card.dataset.role);button.onclick=()=>select(index);button.onkeydown=e=>{if(['ArrowLeft','ArrowRight','Home','End'].includes(e.key)){e.preventDefault();const next=e.key==='Home'?0:e.key==='End'?cards.length-1:(index+(e.key==='ArrowRight'?1:-1)+cards.length)%cards.length;select(next);tabs.children[next].focus();}};tabs.append(button);});
 container.before(tabs);select(0);
}
function fresh(){return Date.now()-last<3000;}
function render(){for(const role of Object.keys(roles)){const card=$(`[data-role="${role}"]`),a=state?.axes[role];const f=factor(role),c=displayCalibration(role),focus=role.startsWith('focuser'),unit=focus?'mm':c?'deg':roles[role][2];if(card.dataset.factor&&Number(card.dataset.factor)!==f){for(const key of focus?[]:['step','speed']){const input=card.querySelector('[data-'+key+']');input.value=Number(input.value)/Number(card.dataset.factor)*f;}card.querySelector('[data-target]').value='';}card.dataset.factor=f;card.querySelector('.optics-readout span').textContent=unit;for(const [key,name] of [['step','Step'],['speed','Speed'],['target','Target']])card.querySelector('[data-'+key+']').parentElement.firstChild.textContent=name+' ('+unit+(key==='speed'?'/s':'')+')';card.querySelector('[data-position]').textContent=Number.isFinite(a?.position)&&(!focus||c)?(a.position*f).toLocaleString(undefined,{maximumFractionDigits:c||unit==='deg'?4:0}):'—';card.querySelector('[data-binding]').textContent=a?.error||'';card.querySelector('[data-limits]').textContent=a?.ready?c?`Travel ${c.minimum} → ${c.maximum} ${unit}`:`Travel ${a.minimum.toLocaleString()} → ${a.maximum.toLocaleString()}`:'Configure axis mode and limits in Hardware Config';if(focus){card.querySelector('[data-limits]').textContent=!a?.ready?'Configure axis mode and limits in Hardware Config':'';renderFocusSlider(card,a,c,f);}
 for(const b of card.querySelectorAll('button:not([data-settings])'))b.disabled=busy||!fresh()||!a?.binding||(b.dataset.opticsAction==='stop'?!!a?.error:!a?.ready||(b.dataset.opticsAction!=='enable'&&(!a?.enabled||(focus&&!c))));setMotorButton(card.querySelector('[data-optics-action="enable"]'),a,roles[role][0]);}
 const m=state?.axes.m3;$('#optics-m3-position').textContent=Number.isFinite(m?.position)?`M3 ${m.position.toFixed(2)}°`:'M3 unavailable';setMotorButton($('#optics-m3-enable'),m,'M3');$('#optics-m3-enable').disabled=busy||!fresh()||!m?.ready;$('#optics-m3-enable').title=busy?'Command in progress':!fresh()?'M3 telemetry unavailable':!m?.ready?(m?.error||'M3 is not ready; check drive connection and telemetry'):'';$('#optics-m3-stop').disabled=!fresh()||!m?.ready;for(const id of ['cw','ccw'])$('#optics-m3-'+id).disabled=busy||!fresh()||!m?.ready||!m.enabled||(!!m3Jog&&m3Jog.button!==$('#optics-m3-'+id));$('#optics-m3-note').textContent=state?.m3.port1===null?'Set Port 1 first. Port 2 is opposite by 180°.':`Pos 1: ${Number.isFinite(state?.m3.port1)?state.m3.port1.toFixed(2):'—'}° · Pos 2: ${Number.isFinite(state?.m3.port2)?state.m3.port2.toFixed(2):'—'}°`;$('#optics-state').textContent=fresh()?`Live positions · ${state?.interval_ms?.toFixed(0)??'—'} ms · target 5 ms`:'Telemetry unavailable';renderPath();}
function renderFocusSlider(card,a,c,f){
 const slider=card.querySelector('[data-position-slider]'),valid=!!c&&Number.isFinite(a?.position)&&fresh()&&c.maximum>c.minimum,position=valid?a.position*f:null;
 slider.min=c?.minimum??0;slider.max=c&&c.maximum>c.minimum?c.maximum:Number(slider.min)+1;slider.value=valid?Math.min(c.maximum,Math.max(c.minimum,position)):slider.min;
 card.querySelector('[data-slider-min]').textContent='Minimum '+(c?c.minimum:'—')+' mm';card.querySelector('[data-slider-max]').textContent='Maximum '+(c?c.maximum:'—')+' mm';
 card.querySelector('.focus-position-slider').style.setProperty('--focus-progress',valid?((Math.min(c.maximum,Math.max(c.minimum,position))-c.minimum)/(c.maximum-c.minimum)*100)+'%':'0%');
 slider.setAttribute('aria-valuetext',valid?position.toFixed(4)+' mm':'Position mapping unavailable');card.querySelector('.focus-position-slider').classList.toggle('unmapped',!valid);
 const outside=valid&&(position<c.minimum||position>c.maximum);card.querySelector('.focus-position-slider').classList.toggle('outside',outside);if(outside)card.querySelector('[data-limits]').textContent='Current position is outside the configured range';
}
function renderPath(){
 const m=state?.axes.m3,port1=state?.m3.port1,indicator=$('#optics-path-indicator');
 const known=Number.isFinite(m?.position)&&Number.isFinite(port1);
 const valid=fresh()&&m?.valid!==false&&known;
 indicator.classList.toggle('unavailable',!known);indicator.classList.toggle('stale',known&&!valid);indicator.title=known&&!valid?'Last known M3 position · telemetry unavailable':'';
 let label='Position unavailable',active=0,destination=0;
 if(known){const delta=((m.position-port1+540)%360)-180;indicator.style.setProperty('--m3-angle',delta+'deg');
 const distance1=Math.abs(delta),distance2=180-distance1;
 destination=distance1<=distance2?2:1;
 const window=state?.m3.in_position_window??0.01;
 if(distance1<=window+1e-9){active=1;label='Pointing to Port 1';}else if(distance2<=window+1e-9){active=2;label='Pointing to Port 2';}else label='Between ports';
 if(!valid){active=0;label='Last known position · telemetry unavailable';}
 indicator.setAttribute('aria-label',`M3 ${m.position.toFixed(2)} degrees · ${label}`);
 }else indicator.setAttribute('aria-label',label);
 const toggle=$('#optics-m3-switch');toggle.dataset.destination=destination;toggle.setAttribute('aria-checked',String(active===2));toggle.disabled=busy||!!m3Jog||!valid||!m?.ready||!m.enabled;toggle.textContent=destination?`Switch to Port ${destination}`:'Switch position';
 $('#optics-path-state').textContent=label;
 for(const n of [1,2]){const panel=$('#optics-port'+n).closest('.optics-port');panel.classList.toggle('optical-path-active',active===n);}
}
async function refresh(){if(reading||$('#optics').hidden)return;reading=true;try{state=await api('/api/optics/status');last=Date.now();render();}catch(e){$('#optics-state').textContent=e.message;last=0;render();}finally{reading=false;}}
async function run(fn){if(busy)return;busy=true;render();$('#optics-message').textContent='Sending command…';try{await fn();$('#optics-message').textContent='Command accepted';}catch(e){$('#optics-message').textContent='';$('#optics-message-detail').textContent=e.message;$('#optics-message-dialog').showModal();}finally{busy=false;await refresh();render();}}
async function command(role,action,extra={}){if(role==='m3'&&m3Jog)await stopM3Jog();if(!fresh())return;const binding=state?.axes[role]?.binding;await run(()=>api('/api/optics/'+role+'/command',{action,target:binding,...extra}));}
$('#optics-m3-enable').onclick=()=>command('m3',state.axes.m3.enabled?'disable':'enable');$('#optics-m3-stop').onclick=()=>{void stopM3Jog();};
$('#optics-m3-switch').onclick=()=>{const n=Number($('#optics-m3-switch').dataset.destination);if(n===1||n===2)run(()=>api('/api/optics/m3/port/'+n,{action:'goto',target:state.axes.m3.binding}));};
let m3Read=null;
function updateM3PositionBounds(){
 const angle=$('#optics-m3-angle');
 for(const [attribute,id] of [['min','minimum'],['max','maximum']]){
  const field=$('#optics-m3-'+id);
  if(field.value!==''&&Number.isFinite(Number(field.value)))angle.setAttribute(attribute,field.value);
  else angle.removeAttribute(attribute);
 }
}
for(const id of ['minimum','maximum'])$('#optics-m3-'+id).addEventListener('input',updateM3PositionBounds);
$('#optics-m3-setup').onclick=async()=>{if(m3Jog)await stopM3Jog();try{m3Read=await api('/api/optics/m3/preset');state.m3=m3Read;$('#optics-m3-angle').value=state?.m3.port1??'';$('#optics-m3-minimum').value=state?.m3.minimum??0;$('#optics-m3-maximum').value=state?.m3.maximum??359;$('#optics-m3-window').value=state?.m3.in_position_window??0.01;$('#optics-m3-speed').value=m3Read.jog_speed??1;$('#optics-m3-speed').max=m3Read.max_jog_speed??state.axes.m3.max_velocity;$('#optics-m3-error').textContent='';updateM3PositionBounds();$('#optics-m3-dialog').showModal();}catch(e){$('#optics-message-detail').textContent=e.message;$('#optics-message-dialog').showModal();}};$('#optics-m3-close').onclick=()=>$('#optics-m3-dialog').close();
$('#optics-m3-form').onsubmit=async e=>{e.preventDefault();try{await api('/api/optics/m3/preset',{port1:Number($('#optics-m3-angle').value),minimum:Number($('#optics-m3-minimum').value),maximum:Number($('#optics-m3-maximum').value),in_position_window:Number($('#optics-m3-window').value),jog_speed:Number($('#optics-m3-speed').value),target:m3Read.target,expected:m3Read.native});$('#optics-m3-dialog').close();await refresh();}catch(e){$('#optics-m3-error').textContent=e.message;}};
let opticsSocket=null,opticsRetry,opticsFrame=0;
function scheduleOpticsRender(){if(!opticsFrame)opticsFrame=requestAnimationFrame(()=>{opticsFrame=0;render();});}
function stopOpticsStream(){clearTimeout(opticsRetry);if(opticsSocket){opticsSocket.onclose=null;opticsSocket.close();opticsSocket=null;}last=0;if(m3Jog)void stopM3Jog();}
function startOpticsStream(){stopOpticsStream();if($('#optics').hidden||document.hidden)return;const ws=new WebSocket(`${location.protocol==='https:'?'wss:':'ws:'}//${location.host}/ws/optics`);opticsSocket=ws;ws.onmessage=e=>{if(ws!==opticsSocket)return;state=JSON.parse(e.data);last=Date.now();ws.send('ack');scheduleOpticsRender();};ws.onclose=()=>{if(ws!==opticsSocket)return;last=0;render();if(m3Jog)void stopM3Jog();opticsRetry=setTimeout(startOpticsStream,1000);};}
new MutationObserver(()=>$('#optics').hidden?stopOpticsStream():startOpticsStream()).observe($('#optics'),{attributes:true,attributeFilter:['hidden']});document.addEventListener('visibilitychange',()=>document.hidden?stopOpticsStream():startOpticsStream());setInterval(()=>{if(!$('#optics').hidden&&!fresh()){render();if(m3Jog)void stopM3Jog();}},250);
document.addEventListener('telemetry-state',e=>{if(!state||$('#optics').hidden)return;for(const role of ['m3','rotator']){const a=e.detail.axes?.find(a=>a.id===role);if(a&&state.axes[role])Object.assign(state.axes[role],a,{position:a.position_deg,ready:a.valid&&e.detail.drive.connected&&e.detail.drive.motion_available});}scheduleOpticsRender();});


let settingsRole,settingsBinding;
function countsPerBase(role){return role==='rotator'?(state?.axes.rotator.counts_per_rev??4294967296)/360:1;}
function displayCalibration(role){const a=state?.axes[role];return a?.calibration??(role.startsWith('focuser')&&a?{minimum:a.minimum??0,maximum:a.maximum??0,unit_per_count:1}:null);}
function setMotorButton(button,axis,name){const known=typeof axis?.enabled==='boolean';button.textContent=known?(axis.enabled?'Disable':'Enable'):'Unavailable';button.classList.toggle('motor-enabled',known&&!axis.enabled);button.classList.toggle('motor-disabled',known&&axis.enabled);button.setAttribute('aria-pressed',String(known&&axis.enabled));button.setAttribute('aria-label',name+' '+(known?(axis.enabled?'enabled; click to disable':'disabled; click to enable'):'state unavailable'));}
function factor(role){return state?.axes[role]?.calibration?state.axes[role].calibration.unit_per_count*countsPerBase(role):1;}
function toBase(role,value){return value/factor(role);}
function openAxisSettings(role){settingsRole=role;const a=state?.axes[role],c=displayCalibration(role);settingsBinding=a?.binding;$('#optics-axis-title').textContent=roles[role][0]+' · Port '+roles[role][1];$('#optics-axis-binding').textContent=settingsBinding?`Drive ${settingsBinding.drive} · AX${settingsBinding.channel} · SN ${settingsBinding.serial_number}`:'Drive unavailable';document.querySelectorAll('[data-config-unit]').forEach(e=>e.textContent=role.startsWith('focuser')?'(mm)':'(degrees)');$('#optics-axis-min').value=c?.minimum??'';$('#optics-axis-max').value=c?.maximum??'';$('#optics-axis-scale').value=c?.unit_per_count??(role==='rotator'?1/countsPerBase(role):'');$('#optics-axis-error').textContent='';$('#optics-axis-dialog').showModal();}
$('#optics-axis-close').onclick=()=>$('#optics-axis-dialog').close();
$('#optics-axis-form').onsubmit=async e=>{e.preventDefault();try{await api('/api/optics/'+settingsRole+'/settings',{minimum:Number($('#optics-axis-min').value),maximum:Number($('#optics-axis-max').value),unit_per_count:Number($('#optics-axis-scale').value),target:settingsBinding});$('#optics-axis-dialog').close();await refresh();}catch(e){$('#optics-axis-error').textContent=e.message;}};

for(const id of ["optics-message-close","optics-message-ok"])$("#"+id).onclick=()=>$("#optics-message-dialog").close();

// Short leases stop M3 if the page loses its connection; serialize renewals and stop.
let m3Jog=null,m3JogChain=Promise.resolve();
function queueM3Jog(body,active=null){m3JogChain=m3JogChain.catch(()=>{}).then(()=>body.action==='jog'&&m3Jog!==active?undefined:api('/api/optics/m3/command',body));return m3JogChain;}
async function stopM3Jog(){const active=m3Jog;m3Jog=null;if(active){clearTimeout(active.timer);active.button.classList.remove('jog-active');try{await queueM3Jog({action:'stop',target:active.target});}catch(e){$('#optics-message').textContent=e.message;}void refresh();}else if(fresh()){await command('m3','stop');}}
function beginM3Jog(button,direction){if(m3Jog||button.disabled||busy||!fresh())return;const active={button,target:state.axes.m3.binding,timer:null};m3Jog=active;button.classList.add('jog-active');render();async function renew(){if(m3Jog!==active)return;if(!fresh()||$('#optics').hidden||document.hidden||!state.axes.m3.enabled||JSON.stringify(active.target)!==JSON.stringify(state.axes.m3.binding)){void stopM3Jog();return;}try{await queueM3Jog({action:'jog',value:direction,lease:.8,target:active.target},active);if(m3Jog===active)active.timer=setTimeout(renew,250);}catch(e){$('#optics-message').textContent=e.message;if(m3Jog===active)void stopM3Jog();}}void renew();}
for(const [id,direction] of [['cw',1],['ccw',-1]]){const button=$('#optics-m3-'+id);button.onpointerdown=e=>{if(e.button!==0)return;e.preventDefault();button.setPointerCapture(e.pointerId);beginM3Jog(button,direction);};button.onpointerup=button.onpointercancel=button.onlostpointercapture=()=>{if(m3Jog?.button===button)void stopM3Jog();};button.onkeydown=e=>{if([' ','Enter'].includes(e.key)){e.preventDefault();if(!e.repeat)beginM3Jog(button,direction);}};button.onkeyup=e=>{if([' ','Enter'].includes(e.key)){e.preventDefault();if(m3Jog?.button===button)void stopM3Jog();}};button.onblur=()=>{if(m3Jog?.button===button)void stopM3Jog();};}
window.addEventListener('blur',()=>{if(m3Jog)void stopM3Jog();});window.addEventListener('pagehide',()=>{if(m3Jog){const active=m3Jog;m3Jog=null;clearTimeout(active.timer);navigator.sendBeacon('/api/optics/m3/command',new Blob([JSON.stringify({action:'stop',target:active.target})],{type:'application/json'}));}});document.addEventListener('visibilitychange',()=>{if(document.hidden&&m3Jog)void stopM3Jog();});new MutationObserver(()=>{if($('#optics').hidden&&m3Jog)void stopM3Jog();}).observe($('#optics'),{attributes:true,attributeFilter:['hidden']});

for(const id of ['stop-all','disable-all'])$('#'+id)?.addEventListener('click',()=>{if(m3Jog)void stopM3Jog();},{capture:true});

// Keep the painted arrow tip 5 CSS pixels inside the ring's inner edge.
const portArrowSvg=document.querySelector('#optics-path-indicator svg');
new ResizeObserver(()=>{
 const scale=portArrowSvg.getBoundingClientRect().width/200;if(!scale)return;
 const tip=100-77+5/scale+9*.7/2;
 const shift=(100+(34.3-100)*.7-tip)/.7;
 document.querySelector('#optics-arrow-head').setAttribute('transform',`translate(${-shift} 0)`);
 document.querySelector('.optics-path-link').setAttribute('d',`M100 100H${70-shift}`);
}).observe(portArrowSvg);
