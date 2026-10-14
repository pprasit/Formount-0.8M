const fields=[['VL[3]','CCW limit','°','position'],['VH[3]','CW limit','°','position'],['VH[2]','Velocity limit','°/s','velocity'],['AC[1]','Acceleration','°/s²','acceleration'],['DC','Deceleration','°/s²','acceleration'],['CL[1]','Current limit','A','current']];

let state, savingToFlash=false;

const panels=new Map();

let automaticReads=Promise.resolve();

function publishLimits(axis,panel){

 const values=Object.fromEntries(fields.map(([c,,unit,type])=>[c,panel.values.has(c)?panel.values.get(c)/factor(axis,type)+(type==='position'?positionOffset(axis):0):null]));

 const signature=JSON.stringify({generation:state.drive.generation,values});

 if(panel.lastPublished===signature)return;

 panel.lastPublished=signature;

 const speed=document.querySelector(axis.id==='azimuth'?'#combined-speed-az':axis.id==='altitude'?'#combined-speed-alt':'#no-speed');

 if(speed&&Number.isFinite(values['VH[2]']))speed.value=Number(values['VH[2]'].toPrecision(8));

 const target=document.querySelector(`#target-${axis.id}`);

 if(target&&Number.isFinite(values['VL[3]'])&&Number.isFinite(values['VH[3]'])){

  target.min=values['VL[3]'];target.max=values['VH[3]'];target.step='any';

  target.title=`Drive limits: ${values['VL[3]']} to ${values['VH[3]']} degrees`;

 }

 document.dispatchEvent(new CustomEvent('drive-limits',{detail:{axis:axis.id,values,generation:state.drive.generation}}));

}

function automaticRead(panel){

 const generation=state.drive.generation;

 if(!state.drive.connected||panel.busy||Date.now()<(panel.autoRetryAt||0)||panel.autoGeneration===generation)return;

 panel.autoGeneration=generation;

 automaticReads=automaticReads.catch(()=>{}).then(async()=>{

  if(state.drive.generation!==generation||!state.drive.connected)return;

  await panel.querySelector('[data-read]').onclick();
  if(state.drive.generation===generation&&(!Number.isFinite(panel.values.get('VL[3]'))||!Number.isFinite(panel.values.get('VH[3]')))){panel.autoGeneration=null;panel.autoRetryAt=Date.now()+10000;}

 });

}

async function api(url,body){const r=await fetch(url,body===undefined?{}:{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(body)});const d=await r.json();if(!r.ok)throw Error(typeof d.detail==='string'?d.detail:'Check the entered value');return d;}

function positionOffset(axis){return axis.offset_deg+(axis.id==='azimuth'?(state.drive.azimuth_position?.offset_deg||0):0);}

function factor(axis,type){const f=state.drive.scaling?.[axis.id]||Array(12).fill(1),counts=axis.counts_per_rev/360,p=(f[6]/f[7])*(f[5]/f[4])*(f[1]/f[0]);return type==='current'?1:counts*p*(type==='position'?1:f[8]/f[9])*(type==='acceleration'?f[10]/f[11]:1);}

function controls(panel,axis){

 const unavailable=savingToFlash||panel.busy||!state.drive.connected||!axis.valid||!!state.observatory?.tracking;

 const reason=savingToFlash?'Saving to drive Flash…':panel.busy?'Reading / writing drive values…':!state.drive.connected?'Drive disconnected':!axis.valid?'Waiting for fresh telemetry':state.observatory?.tracking?'Stop tracking before applying limits':axis.enabled?'Enter values here; disable this motor before pressing Set.':'Motor disabled · enter a value and press Set';

 panel.querySelector('[data-read]').disabled=savingToFlash||panel.busy||!state.drive.connected;

 panel.querySelector('[data-save]').disabled=savingToFlash||[...panels.values()].some(p=>p.busy)||!state.drive.connected||state.drive.mode!=='hardware'||state.axes.some(a=>a.enabled||!a.valid)||!!state.observatory?.tracking;

 panel.querySelectorAll('[data-write]').forEach(b=>{b.disabled=unavailable||axis.enabled||panel.querySelector(`[data-limit="${b.dataset.write}"]`).value==='';b.title=axis.enabled?'Disable this motor to apply the entered value':reason;});

 panel.querySelectorAll('input').forEach(i=>{i.disabled=unavailable;i.title=reason;});

 panel.querySelector('.limit-state').textContent=reason;

}

function fieldsFor(axis){return fields.map(f=>{

 const copy=[...f];

 if(f[0]==='VL[3]')copy[1]=axis.id==='azimuth'?'CCW limit':'Lower limit';

 if(f[0]==='VH[3]')copy[1]=axis.id==='azimuth'?'CW limit':'Upper limit';

 return copy;

});}

function create(card,axis){const axisFields=fieldsFor(axis);const plot=document.createElement('div');plot.className='axis-plot';plot.append(...card.childNodes);card.append(plot);const panel=document.createElement('aside');panel.className='axis-limits';panel.values=new Map();panel.busy=false;panel.innerHTML=`<div class="limit-heading"><span class="limit-mark">↔</span><div><small>AX${axis.number} · PROTECTION</small><h3>${axis.name} limits</h3></div></div><p class="limit-state"></p><div class="limit-fields">${axisFields.map(([c,label,unit])=>`<label class="limit-field"><span>${label}<small>${unit}</small></span><div><input type="number" step="any" aria-label="${axis.name} ${label}" data-limit="${c}" placeholder="—"><button type="button" data-write="${c}" disabled aria-label="Write ${axis.name} ${label}">Set</button></div><small data-value="${c}">Not read</small></label>`).join('')}</div><button type="button" class="limit-read" data-read>Read from drive</button><button type="button" class="limit-read limit-save" data-save>Flash to Drive</button><p class="limit-message" role="status">Enter a value and press Set. Current drive values are checked automatically.</p><p class="limit-note">Position limits are enforced by the drive for Go and Jog in position mode. Set writes to RAM only; it does not write Flash. Flash to Drive saves all four axes and current drive settings to flash. All motors must be disabled.</p>`;card.append(panel);panels.set(axis.id,panel);

 const message=(text,error=false)=>{panel.querySelector('.limit-message').textContent=text;panel.querySelector('.limit-message').classList.toggle('error',error);};

 panel.querySelector('[data-read]').onclick=async()=>{if(panel.busy)return;panel.busy=true;controls(panel,axis);message('Reading drive limits…');try{const generation=state.drive.generation;const d=await api(`/api/hardware/${axis.id}/parameters?group=limits`);if(generation!==state.drive.generation)return;panel.values.clear();for(const [c,,unit,type] of axisFields){const v=d.parameters.find(p=>p.command===c),input=panel.querySelector(`[data-limit="${c}"]`),readback=panel.querySelector(`[data-value="${c}"]`);if(v?.value!=null&&!v.error){const raw=Number(v.value);panel.values.set(c,raw);const angular=raw/factor(axis,type)+(type==='position'?positionOffset(axis):0);input.value=Number(angular.toPrecision(10));readback.textContent=`Drive: ${input.value} ${unit}`;}else {input.value='';readback.textContent='Unavailable';}}publishLimits(axis,panel);message('Drive values loaded.');}catch(e){message(e.message,true);}finally{panel.busy=false;controls(panel,state.axes.find(a=>a.id===axis.id));}};

 panel.querySelectorAll('[data-write]').forEach(b=>b.onclick=async()=>{const c=b.dataset.write,input=panel.querySelector(`[data-limit="${c}"]`),field=axisFields.find(f=>f[0]===c);if(input.value===''||!input.reportValidity())return;const type=field[3],value=(Number(input.value)-(type==='position'?positionOffset(axis):0))*factor(axis,type);if(!Number.isFinite(value))return;panel.busy=true;controls(panel,axis);message(`Setting ${field[1]}…`);try{const generation=state.drive.generation;

 const read=await api(`/api/hardware/${axis.id}/parameters?group=limits`);

 if(generation!==state.drive.generation)throw Error('Drive connection changed. Please retry.');

 const previous=read.parameters.find(p=>p.command===c);

 if(previous?.error||previous?.value==null||!Number.isFinite(Number(previous.value)))throw Error('Unable to read the current drive value.');

 const d=await api(`/api/hardware/${axis.id}/parameter`,{command:c,value,expected:Number(previous.value)});panel.values.set(c,d.value);panel.querySelector(`[data-value="${c}"]`).textContent=`Drive: ${Number((d.value/factor(axis,type)+(type==='position'?positionOffset(axis):0)).toPrecision(10))} ${field[2]}`;publishLimits(axis,panel);message(`${field[1]} updated · readback verified.`);}catch(e){panel.values.delete(c);message(e.message,true);}finally{panel.busy=false;controls(panel,state.axes.find(a=>a.id===axis.id));}});

 panel.querySelector('[data-save]').onclick=async()=>{

  if(savingToFlash||panel.busy)return;

  savingToFlash=true;

  for(const [id,p] of panels)controls(p,state.axes.find(a=>a.id===id));

  message('Saving current drive settings to flash…');

  try{

   const result=await api('/api/hardware/save',{});

   if(!result.acknowledged)throw Error('Drive did not confirm the save.');

   message('Saved to drive flash · all four axes.');

  }catch(e){message(e.message+' Save result is unconfirmed; do not retry automatically.',true);}

  finally{savingToFlash=false;for(const [id,p] of panels)controls(p,state.axes.find(a=>a.id===id));}

 };

 panel.querySelectorAll('input').forEach(i=>i.addEventListener('input',()=>controls(panel,state.axes.find(a=>a.id===axis.id))));

 controls(panel,axis);

 automaticRead(panel);

}

document.addEventListener('telemetry-state',e=>{const old=state;state=e.detail;for(const axis of state.axes){const card=document.querySelector(`.axis-card[data-axis="${axis.id}"]`);if(card&&!panels.has(axis.id))create(card,axis);const panel=panels.get(axis.id);if(panel){if(old&&old.drive.generation!==state.drive.generation){panel.values.clear();panel.querySelectorAll('input').forEach(i=>i.value='');panel.querySelectorAll('[data-value]').forEach(i=>i.textContent='Not read');}controls(panel,axis);automaticRead(panel);publishLimits(axis,panel);}}

 const signal=state.inputs?.azimuth,valid=state.inputs?.valid;for(const dir of ['cw','ccw']){const lamp=document.querySelector(`[data-region="${dir}"]`);lamp.classList.toggle('on',valid&&signal?.[dir]===true);lamp.classList.toggle('unknown',!valid||signal?.[dir]==null);lamp.setAttribute('aria-label',`${dir.toUpperCase()} ${valid?(signal?.[dir]?'active':'inactive'):'unknown'}`);}

});

setInterval(()=>{if(!state||!state.inputs?.valid||Date.now()-Date.parse(state.utc)>1500)document.querySelectorAll('[data-region]').forEach(l=>{l.classList.remove('on');l.classList.add('unknown');});},500);

