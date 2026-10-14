// Direction velocity mode from C# UI/ManualControlWindow.xaml.cs.
const $=s=>document.querySelector(s),panel=$('#direction-window'),velocity=$('#direction-velocity');
const buttons=[...panel.querySelectorAll('[data-manual-axis]')];
velocity.addEventListener('input',()=>$('#direction-error').textContent='');
let state,last=0,motion=null,inFlight=false,stopping=false,drag=null;
async function post(path,body){const r=await fetch(path,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(body)});const data=await r.json();if(!r.ok)throw Error(data.detail||'Command failed');return data;}
function error(e){$('#direction-error').textContent=e.message;}
function axis(id){return state?.axes?.find(a=>a.id===id);}
function ready(id){const a=axis(id);return Date.now()-last<1800&&state?.drive?.connected&&state.drive.motion_available&&a?.valid&&a.enabled&&!state.observatory?.pointing_workbench?.busy;}
function directions(button){const targets=[{axis:button.dataset.manualAxis,sign:Number(button.dataset.sign)}];if(button.dataset.secondaryAxis)targets.push({axis:button.dataset.secondaryAxis,sign:Number(button.dataset.secondarySign)});return targets;}
async function stopAxes(targets){stopping=true;try{const results=await Promise.allSettled(targets.map(t=>post(`/api/axes/${t.axis}/command`,{action:'stop'})));for(const r of results)if(r.status==='rejected')error(r.reason);}finally{stopping=false;}}
function release(){if(!motion)return;const old=motion;motion=null;clearInterval(old.timer);old.button.classList.remove('held');velocity.disabled=false;if(!inFlight)void stopAxes(old.targets);}
async function renew(session){if(inFlight||motion!==session)return;if(!session.targets.every(t=>ready(t.axis))){release();return;}inFlight=true;
 try{const results=await Promise.allSettled(session.targets.map(t=>post(`/api/axes/${t.axis}/command`,{action:'jog',value:t.value,lease:.8})));const failed=results.find(r=>r.status==='rejected');if(failed)throw failed.reason;}
 catch(e){error(e);release();}
 finally{inFlight=false;if(motion!==session)await stopAxes(session.targets);}
}
function start(button){if(motion||inFlight||stopping||button.disabled)return;$('#direction-error').textContent='';
 const value=Number(velocity.value),targets=directions(button);
 if(!velocity.reportValidity()||!Number.isFinite(value)||value<=0){error(Error('Enter a positive velocity in deg/s'));return;}
 for(const t of targets){const a=axis(t.axis);if(!ready(t.axis))return;if(value>a.max_velocity){error(Error(`Maximum ${a.name||t.axis} velocity is ${a.max_velocity} deg/s`));return;}t.value=value*t.sign;}
 const session={targets,button,timer:null};motion=session;velocity.disabled=true;button.classList.add('held');void renew(session);session.timer=setInterval(()=>void renew(session),250);
}
for(const button of buttons){button.addEventListener('pointerdown',e=>{if(e.button!==0||button.disabled)return;e.preventDefault();button.setPointerCapture(e.pointerId);start(button);});
 for(const event of ['pointerup','pointercancel','lostpointercapture','blur'])button.addEventListener(event,release);
 button.addEventListener('keydown',e=>{if([' ','Enter'].includes(e.key)){e.preventDefault();if(!e.repeat)start(button);}});
 button.addEventListener('keyup',e=>{if([' ','Enter'].includes(e.key)){e.preventDefault();release();}});
}
function close(){release();panel.close();$('#direction-open').setAttribute('aria-expanded','false');}
$('#direction-open').onclick=()=>{if(!panel.open){panel.show();constrain();$('#direction-open').setAttribute('aria-expanded','true');render();}else panel.focus();};
$('#direction-close').onclick=close;panel.addEventListener('close',release);
panel.addEventListener('keydown',e=>{if(e.key==='Escape'){e.preventDefault();close();}});
$('#direction-stop').onclick=async()=>{release();try{await post('/api/stop',{});}catch(e){error(e);}};
window.addEventListener('blur',release);document.addEventListener('visibilitychange',()=>{if(document.hidden)release();});
document.addEventListener('click',e=>{if(e.target.closest('#stop-all,#disable-all,[data-nav]'))release();},true);
function render(){for(const button of buttons)button.disabled=!directions(button).every(t=>ready(t.axis));
 $('#direction-stop').disabled=!state?.drive?.connected;
 for(const [id,target] of [['azimuth','#direction-az'],['altitude','#direction-alt']]){const a=axis(id);$(target).textContent=a?.valid&&Number.isFinite(a.position_deg)?a.position_deg.toFixed(3)+'°':'—';}
 $('#direction-state').textContent=Date.now()-last>=1800?'Telemetry unavailable':state?.observatory?.pointing_workbench?.busy?'Calibration or analysis is running':!state?.drive?.connected?'Drive disconnected':!ready('azimuth')&&!ready('altitude')?'Enable the mount axes to move':'Azimuth ← / → · Altitude ↑ / ↓';
 if(motion&&!motion.targets.every(t=>ready(t.axis)))release();
}
document.addEventListener('mount-state',e=>{state=e.detail;last=Date.now();if(panel.open||motion)render();});setInterval(()=>{if(panel.open||motion)render();},300);
// Non-modal floating window; dragging stays inside the viewport.
const handle=$('#direction-drag');
handle.addEventListener('pointerdown',e=>{if(e.button!==0||e.target.closest('button'))return;const box=panel.getBoundingClientRect();drag={x:e.clientX-box.left,y:e.clientY-box.top};handle.setPointerCapture(e.pointerId);});
handle.addEventListener('pointermove',e=>{if(!drag)return;const box=panel.getBoundingClientRect();panel.style.right='auto';panel.style.left=Math.max(6,Math.min(innerWidth-box.width-6,e.clientX-drag.x))+'px';panel.style.top=Math.max(6,Math.min(innerHeight-box.height-6,e.clientY-drag.y))+'px';});
for(const event of ['pointerup','pointercancel','lostpointercapture'])handle.addEventListener(event,()=>drag=null);
function constrain(){if(!panel.open)return;const box=panel.getBoundingClientRect();panel.style.right='auto';panel.style.left=Math.max(6,Math.min(innerWidth-box.width-6,box.left))+'px';panel.style.top=Math.max(6,Math.min(innerHeight-box.height-6,box.top))+'px';}
window.addEventListener('resize',constrain);
