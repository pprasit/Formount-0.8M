const $=s=>document.querySelector(s);
let state,view='astronomy',tab='combined',lastFrame=0,mapGeometry,lastLayout='';
const panels=['combined-panel','axis-grid','astronomy','non-celestial','pointing','camera','imaging','optics','events'];
const labels={overview:'Mount overview',telemetry:'Live telemetry',astronomy:'Astronomy',interesting:'Interesting Object','non-celestial':'Non-celestial tracking',pointing:'Pointing model',imaging:'Imaging',optics:'Instrument Control',camera:'Camera & calibration',events:'Service activity'};
let azimuthLimits=null;
const driveLimits=new Map();
let displayedRegion=null,regionGeneration=null;
function currentRegion(){
 const az=state?.axes?.[0];
 if(!az?.valid)return null;
 if(regionGeneration!==state.drive.generation){
  regionGeneration=state.drive.generation;
  displayedRegion=az.position_deg < -.05?'CCW':az.position_deg > .05?'CW':state.drive.azimuth_position?.boot_region==='CCW'?'CCW':'CW';
 }
 // Keep the last region through encoder noise around the zero boundary.
 if(az.position_deg > .05)displayedRegion='CW';
 else if(az.position_deg < -.05)displayedRegion='CCW';
 return displayedRegion;
}
function regionInfo(){
 if(!state)return;
 const az=state.axes[0],alt=state.axes[1],a=driveLimits.get('azimuth'),b=driveLimits.get('altitude');
 const ready=a?.generation===state.drive.generation&&Number.isFinite(a.values['VL[3]'])&&Number.isFinite(a.values['VH[3]']);
 const inside=ready&&az.valid&&az.position_deg>=a.values['VL[3]']&&az.position_deg<=a.values['VH[3]'];
 const region=currentRegion();
 const name=region?`${region} REGION`:'UNKNOWN REGION';
 $('#region-pill').textContent=ready?(inside?name:'OUTSIDE DRIVE LIMITS'):'READING LIMITS';
 $('#region-pill').classList.toggle('warning',ready&&!inside);
 $('#combined-region').textContent=ready?`${name} · ${inside?'within drive limits':'outside drive limits'}`:'Reading drive limits';
 const note=$('.travel-note p');
 if(note)note.textContent=`Drive limits · Azimuth ${ready?`${fmt(a.values['VL[3]'],2)} to ${fmt(a.values['VH[3]'],2)}°`:'reading…'} · Altitude ${b?.generation===state.drive.generation?`${fmt(b.values['VL[3]'],2)} to ${fmt(b.values['VH[3]'],2)}°`:'reading…'}`;
 return inside;
}
document.addEventListener('drive-limits',e=>{const target=e.detail.axis==='azimuth'?$('#combined-target-az'):e.detail.axis==='altitude'?$('#combined-target-alt'):null;if(target&&Number.isFinite(e.detail.values['VL[3]'])&&Number.isFinite(e.detail.values['VH[3]'])){target.min=e.detail.values['VL[3]'];target.max=e.detail.values['VH[3]'];target.step='any';target.placeholder=`${fmt(Number(target.min),2)} to ${fmt(Number(target.max),2)}`;}driveLimits.set(e.detail.axis,e.detail);if(e.detail.axis==='azimuth')azimuthLimits=e.detail;regionInfo();drawSky();});
const fmt=(n,d=4)=>Number.isFinite(n)?n.toFixed(d):'—';
function layout(){
 const key=`${view}:${tab}:${document.querySelectorAll('.axis-card').length}`;if(key===lastLayout)return;lastLayout=key;
 const telemetry=['overview','telemetry'].includes(view);
 for(const id of panels)$('#'+id).hidden=!(telemetry?(id===(tab==='combined'?'combined-panel':'axis-grid')):id===(view==='interesting'?'astronomy':view));
 $('.section-toolbar').hidden=!telemetry;$('.telemetry-tabs').hidden=view==='overview';$('#chart-tools').hidden=tab==='combined'||!telemetry;
 document.querySelectorAll('[data-axis-tab]').forEach(b=>{const active=b.dataset.axisTab===tab;b.classList.toggle('active',active);b.setAttribute('aria-selected',String(active));b.tabIndex=active?0:-1;});
 document.querySelectorAll('.axis-card').forEach(c=>c.hidden=c.dataset.axis!==tab);
 $('.dashboard-scroll').scrollTop=0;$('.page-heading h1').textContent=labels[view];$('.page-heading p').textContent=telemetry?'Real Elmo telemetry · 5 ms acquisition target':'Observatory operations · live mount service';
 document.dispatchEvent(new Event('telemetry-resize'));drawSky();
}
document.querySelectorAll('[data-nav]').forEach(b=>b.onclick=()=>{if(b.dataset.nav==='drive'){$('#connection-dialog').showModal();return;}view=b.dataset.nav;document.dispatchEvent(new CustomEvent('sky-page-changed',{detail:{view}}));if(['telemetry','overview'].includes(view))tab='combined';document.querySelectorAll('[data-nav]').forEach(n=>n.classList.toggle('active',n===b));layout();});
document.querySelectorAll('[data-axis-tab]').forEach(b=>{b.onclick=()=>{tab=b.dataset.axisTab;layout();};b.onkeydown=e=>{if(!['ArrowLeft','ArrowRight','Home','End'].includes(e.key))return;e.preventDefault();const tabs=[...document.querySelectorAll('[data-axis-tab]')];let i=tabs.indexOf(b);i=e.key==='Home'?0:e.key==='End'?tabs.length-1:(i+(e.key==='ArrowRight'?1:-1)+tabs.length)%tabs.length;tabs[i].click();tabs[i].focus();};});
$('#combined-current').onclick=()=>{if(!state)return;$('#combined-target-az').value=fmt(state.axes[0].position_deg);$('#combined-target-alt').value=fmt(state.axes[1].position_deg);drawSky();};
$('#combined-form').onsubmit=async e=>{e.preventDefault();$('#combined-error').textContent='';$('#combined-goto').disabled=true;try{const r=await fetch('/api/axes/combined/goto',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({azimuth:Number($('#combined-target-az').value),altitude:Number($('#combined-target-alt').value)})});const d=await r.json();if(!r.ok)throw Error(typeof d.detail==='string'?d.detail:'Check target and speed values');$('#combined-error').textContent='';}catch(err){$('#combined-error').textContent=err.message;}};
for(const id of ['combined-target-az','combined-target-alt'])$('#'+id).oninput=drawSky;
document.addEventListener('telemetry-state',e=>{state=e.detail;lastFrame=Date.now();layout();const [az,alt]=state.axes;
 $('#combined-az').textContent=`${fmt(az.position_deg)}°`;$('#combined-alt').textContent=`${fmt(alt.position_deg)}°`;
 $('#combined-az-velocity').textContent=`${fmt(az.velocity_deg_s)}°/s · ${fmt(az.current_a,3)} A · ${az.enabled?'enabled':'disabled'}`;
 $('#combined-alt-velocity').textContent=`${fmt(alt.velocity_deg_s)}°/s · ${fmt(alt.current_a,3)} A · ${alt.enabled?'enabled':'disabled'}`;
 $('#combined-turn').textContent=`${fmt(az.valid?az.position_deg:null,4)}°`;
 $('#combined-region').textContent=state.region?.reason||'Awaiting region';$('#region-pill').textContent=state.region?.azimuth_in_range?state.region.name.toUpperCase():'OUTSIDE TRAVEL';
 $('#region-pill').classList.toggle('warning',!state.region?.azimuth_in_range||!state.region?.altitude_in_range);
 $('#combined-goto').disabled=!state.drive.connected||!az.valid||!alt.valid||!az.enabled||!alt.enabled;
 const inputs=state.inputs,sensor=inputs?.azimuth;$('#input-quality').textContent=inputs?.valid?`${fmt(inputs.age_ms,0)} ms old`:'INPUT UNAVAILABLE';
 for(const direction of ['cw','ccw']){const value=inputs?.valid?sensor?.[direction]:null;$('#sensor-'+direction).classList.toggle('on',value===true);$('#sensor-'+direction+'-state').textContent=value===null||value===undefined?'UNKNOWN':value?'ACTIVE':'inactive';}
 $('#input-raw').textContent=inputs?.raw?Object.entries(inputs.raw).map(([i,v])=>`IP[${i}] ${v} · bits ${(inputs.active_bits[i]||[]).join(', ')||'none'}`).join('\n'):'IP[1] —';
 $('#sensor-mapping').textContent=inputs?.error||'Legacy CW bit 16 / CCW bit 17 · wiring unverified';regionInfo();drawSky();
});
setInterval(()=>{if(Date.now()-lastFrame>1500){$('#combined-goto').disabled=true;$('#input-quality').textContent='STALE';for(const dir of ['cw','ccw']){$('#sensor-'+dir).classList.remove('on');$('#sensor-'+dir+'-state').textContent='UNKNOWN';}}},500);
function drawSky(){
 const canvas=$('#sky-canvas'),box=canvas.getBoundingClientRect();if(!box.width||!box.height||$('#combined-panel').hidden)return;
 const dpr=devicePixelRatio||1,w=box.width,h=box.height;if(canvas.width!==Math.round(w*dpr)||canvas.height!==Math.round(h*dpr)){canvas.width=Math.round(w*dpr);canvas.height=Math.round(h*dpr);}const c=canvas.getContext('2d');c.setTransform(dpr,0,0,dpr,0,0);c.clearRect(0,0,w,h);
 const cardinalRadiusPadding=13+25; // Outer travel ring + 25 px label gap.
 const cx=w/2,cy=h/2-6,r=Math.max(20,Math.min(w/2-cardinalRadiusPadding-12,cy-cardinalRadiusPadding-12));mapGeometry={cx,cy,r};
 const point=(az,alt)=>{const a=az*Math.PI/180,rr=r*(90-alt)/90;return [cx+rr*Math.sin(a),cy-rr*Math.cos(a)];};
 const gradient=c.createRadialGradient(cx,cy,0,cx,cy,r);gradient.addColorStop(0,'#1e3441');gradient.addColorStop(1,'#101a26');c.fillStyle=gradient;c.beginPath();c.arc(cx,cy,r,0,Math.PI*2);c.fill();
 c.font='10px monospace';c.textAlign='center';c.textBaseline='middle';
 for(const alt of [0,30,60,90]){const rr=r*(90-alt)/90;c.strokeStyle=alt===0?'#47637c':'#2d4356';c.lineWidth=1;c.beginPath();c.arc(cx,cy,rr,0,Math.PI*2);c.stroke();if(alt<90){c.fillStyle='#8098b2';c.fillText(`${alt}°`,cx+8,cy-rr+10);}}
 for(let az=0;az<360;az+=30){const [x,y]=point(az,0);c.strokeStyle='#243b4e';c.beginPath();c.moveTo(cx,cy);c.lineTo(x,y);c.stroke();const angle=az*Math.PI/180,labelRadius=r+29;c.fillStyle='#91aac2';if(az%90!==0)c.fillText(`${az}°`,cx+labelRadius*Math.sin(angle),cy-labelRadius*Math.cos(angle));}
 c.font='bold 12px monospace';for(const [az,name] of [[0,'N'],[90,'E'],[180,'S'],[270,'W']]){const angle=az*Math.PI/180,labelRadius=r+cardinalRadiusPadding;c.fillStyle='#c6d9e9';c.fillText(name,cx+labelRadius*Math.sin(angle),cy-labelRadius*Math.cos(angle));}
 // Regions and endpoints use the limits read back from the actual drive.
 const limits=azimuthLimits&&azimuthLimits.generation===state?.drive.generation?azimuthLimits.values:null;
 const lower=limits?.['VL[3]'],upper=limits?.['VH[3]'];
 const sensor=state?.inputs?.valid?state.inputs.azimuth:null;
 if(Number.isFinite(lower)&&Number.isFinite(upper)){
  const ccwEnd=lower>0?lower-360:lower;
  const regions=[{name:'CW',end:upper,offset:13,color:'#a695ff',active:sensor?.cw,side:1},
                 {name:'CCW',end:ccwEnd,offset:7,color:'#65e5c4',active:sensor?.ccw,side:-1}];
  for(const region of regions){
   const {name,end,offset,color,active,side}=region;
   c.strokeStyle=color;c.lineWidth=3;c.globalAlpha=.85;c.beginPath();
   c.arc(cx,cy,r+offset,-Math.PI/2,(end-90)*Math.PI/180,end<0);c.stroke();c.globalAlpha=1;
   const currentAz=state?.axes?.[0];
   const activeRegion=currentRegion();
   if(name===activeRegion){
   const [ex,ey]=point(end,0);c.setLineDash([4,5]);c.strokeStyle=color;c.lineWidth=1;
   c.beginPath();c.moveTo(cx,cy);c.lineTo(ex,ey);c.stroke();c.setLineDash([]);
   c.fillStyle=color;c.beginPath();c.arc(ex,ey,active?5:3,0,Math.PI*2);c.fill();
   }
   const mid=(Math.min(Math.abs(end),360)/2)*(end<0?-1:1),angle=(mid-90)*Math.PI/180;
   const sx=cx+(r+offset)*Math.cos(angle),sy=cy+(r+offset)*Math.sin(angle);
   const tx=side>0?Math.min(w-16,cx+r+155):Math.max(16,cx-r-155),ty=Math.max(72,Math.min(h-65,sy));
   const bendX=side>0?cx+r+48:cx-r-48;
   c.strokeStyle=color;c.setLineDash([3,5]);c.beginPath();c.moveTo(sx,sy);c.lineTo(bendX,ty);c.lineTo(tx,ty);c.stroke();c.setLineDash([]);
   c.textAlign=side>0?'right':'left';c.fillStyle=color;c.font='bold 11px monospace';c.fillText(`${name} REGION`,tx,ty-12);
   if(name===activeRegion){c.font='10px monospace';c.fillText(`${name} limit ${fmt(name==='CCW'?lower:upper,2)}°`,tx,ty+14);}
  }
 }else{c.fillStyle='#8098af';c.textAlign='left';c.font='10px monospace';c.fillText('Reading drive region limits…',20,h-48);}
 c.fillStyle='#93afc6';c.font='9px monospace';c.fillText('ZENITH',cx,cy+12);
 const mark=(az,alt,color,label,filled)=>{if(!Number.isFinite(az)||!Number.isFinite(alt))return;const [x,y]=point(az,Math.max(0,Math.min(90,alt)));c.strokeStyle=color;c.lineWidth=1.5;c.beginPath();c.arc(x,y,6,0,Math.PI*2);if(filled){c.fillStyle=color;c.shadowColor=color;c.shadowBlur=14;c.fill();c.shadowBlur=0;}else c.stroke();c.beginPath();c.moveTo(x-11,y);c.lineTo(x+11,y);c.moveTo(x,y-11);c.lineTo(x,y+11);c.stroke();c.fillStyle=color;c.font='10px monospace';c.textAlign='center';c.fillText(label,x,y-18);};
 if(state){const [az,alt]=state.axes;if(az.valid&&alt.valid){const [x,y]=point(az.position_deg,Math.max(0,Math.min(90,alt.position_deg)));c.strokeStyle='#65e5c45c';c.beginPath();c.moveTo(cx,cy);c.lineTo(x,y);c.stroke();mark(az.position_deg,alt.position_deg,regionInfo()?'#65e5c4':'#f3bc78','MOUNT',true);}}
 const ta=$('#combined-target-az').value,tb=$('#combined-target-alt').value;if(ta!==''&&tb!=='')mark(Number(ta),Number(tb),'#f3bc78','SELECTED',false);
}
$('#sky-canvas').onclick=e=>{if(!mapGeometry||!state)return;const box=e.currentTarget.getBoundingClientRect(),{cx,cy,r}=mapGeometry,dx=e.clientX-box.left-cx,dy=e.clientY-box.top-cy;const distance=Math.hypot(dx,dy);if(distance>r)return;const skyAz=(Math.atan2(dx,-dy)*180/Math.PI+360)%360,candidates=Array.from({length:Math.max(0,Math.ceil(Number($('#combined-target-az').max)/360)-Math.floor(Number($('#combined-target-az').min)/360)+1)},(_,i)=>skyAz+360*(Math.floor(Number($('#combined-target-az').min)/360)+i)).filter(v=>v>=Number($('#combined-target-az').min)&&v<=Number($('#combined-target-az').max));if(!candidates.length)return;const az=candidates.sort((a,b)=>Math.abs(a-state.axes[0].position_deg)-Math.abs(b-state.axes[0].position_deg))[0];$('#combined-target-az').value=fmt(az);$('#combined-target-alt').value=fmt(90-distance/r*90);drawSky();};
new ResizeObserver(()=>{drawSky();document.dispatchEvent(new Event('telemetry-resize'));}).observe($('.dashboard-scroll'));
layout();
