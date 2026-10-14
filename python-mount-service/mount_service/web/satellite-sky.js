import {drawRegions,drawMountActual} from './sky-region-overlay.js';
import {showInterestingMenu} from './interesting-objects.js';
import {projectSky} from './sky-projection.js';

const $=s=>document.querySelector(s),canvas=$('#satellite-sky-canvas'),ctx=canvas.getContext('2d');

let skyMode='satellite';
let data=null,points=[],selected='',track=[],horizonSeconds=null,yaw=0,tilt=0,zoom=1,offsetX=0,offsetY=0,lastPointer=null,drag=null;

export function clearSatelliteSky(){selected='';track=[];canvas.dataset.trackPoints=0;$('#satellite-track-info').textContent='Drag to rotate · Click to select';draw();}

export function showSatelliteSky(d,name){if(!data){data={objects:[],calculation_ms:0,cpu_ms:0};}const id=$('#moving-line1').value.slice(2,7).trim();selected=id;const old=data.objects.find(o=>o.id===id);if(old)Object.assign(old,d,{id,name});else data.objects.push({...d,id,name});if(data.objects.length<=1)draw();}

export function setSkyMode(mode){skyMode=mode;data=null;yaw=tilt=offsetX=offsetY=0;zoom=1;lastPointer=null;clearSatelliteSky();const title=mode==='solar'?'Solar system':mode==='asteroid'?'Asteroid':mode==='comet'?'Comet':'Satellite';$('#satellite-sky h3').textContent=title+' sky · 3D';$('#satellite-sky .eyebrow').textContent=title.toUpperCase()+' SKY MAP';canvas.setAttribute('aria-label','Interactive 3D '+title.toLowerCase()+' sky sphere; drag to rotate and scroll to zoom');}

export function showSmallBodySky(d,id,name){selected=id;if(!data)data={objects:[],calculation_ms:0,cpu_ms:0};const old=data.objects.find(o=>o.id===id);if(old)Object.assign(old,d,{id,name});else data.objects.push({...d,id,name});draw();}

export function setSatelliteCatalog(d){data=d;canvas.dataset.frameUtc=d.utc||'';draw();}

export function setSatelliteLoading(message){const el=$('#satellite-sky-loading');el.textContent=message;el.hidden=!message;}

export function setSatelliteTrack(id,points,horizon=null){if($('#moving-line1').value.slice(2,7).trim()===id){selected=id;track=points;horizonSeconds=horizon;canvas.dataset.trackPoints=points.length;canvas.dataset.horizonSeconds=horizon??'';$('#satellite-track-info').textContent=`Selected track · +${(points.at(-1).seconds/3600).toFixed(1)} h`;draw();}}

function pick(e){const box=canvas.getBoundingClientRect(),x=e.clientX-box.left,y=e.clientY-box.top;const nearest=points.reduce((best,p)=>{const d=Math.hypot(p.x-x,p.y-y);return !best||d<best.d?{...p,d}:best;},null);if(nearest&&nearest.d<12){selected=nearest.o.id;track=[];document.dispatchEvent(new CustomEvent(skyMode==='satellite'?'satellite-picked':'small-body-picked',{detail:nearest.o}));draw();}}

function draw(){const box=canvas.getBoundingClientRect();if(!box.width||!box.height)return;const dpr=devicePixelRatio||1;canvas.width=box.width*dpr;canvas.height=box.height*dpr;ctx.setTransform(dpr,0,0,dpr,0,0);const w=box.width,h=box.height,gutter=Math.min(175,w*.27),cx=(w+gutter)/2+offsetX,cy=h/2+offsetY,r=Math.max(10,(Math.min(w-gutter,h)-(innerWidth>900?56:64))/2-5)*zoom;ctx.clearRect(0,0,w,h);canvas.dataset.tilt=tilt.toFixed(3);canvas.dataset.zoom=zoom.toFixed(2);canvas.dataset.centerX=String(cx);canvas.dataset.centerY=String(cy);canvas.dataset.radius=r.toFixed(2);ctx.font='10px "IBM Plex Mono",monospace';const gradient=ctx.createRadialGradient(cx-r*.25,cy-r*.3,0,cx,cy,r);gradient.addColorStop(0,'#203e4b');gradient.addColorStop(1,'#0d1924');ctx.fillStyle=gradient;ctx.beginPath();ctx.arc(cx,cy,r,0,Math.PI*2);ctx.fill();

const project=(az,alt)=>{const v=projectSky(az,alt,yaw,tilt);return {x:cx+r*v.x,y:cy-r*v.y,depth:v.depth};};
drawRegions(ctx,canvas,project,cx,cy,r);

function line(coords,color,back=false,width=1,dashed=back){ctx.strokeStyle=color;ctx.lineWidth=width;ctx.setLineDash(dashed?[3,5]:[]);ctx.beginPath();let active=false;for(const [az,alt] of coords){const p=project(az,alt),visible=back?p.depth<-.001:p.depth>=-.001;if(!visible){active=false;continue;}if(active)ctx.lineTo(p.x,p.y);else ctx.moveTo(p.x,p.y);active=true;}ctx.stroke();ctx.setLineDash([]);}

for(const alt of [-60,-30,30,60]){const coords=Array.from({length:181},(_,i)=>[i*2,alt]);line(coords,'#243e4c',true);line(coords,'#345667');}

for(let az=0;az<360;az+=30){const coords=Array.from({length:181},(_,i)=>[az,i-90]);line(coords,'#233c4b',true);line(coords,'#2e4c5d');}

const horizon=Array.from({length:361},(_,i)=>[i,0]);line(horizon,'#62766b',true,1.2);line(horizon,'#dfbb7e',false,1.7);ctx.strokeStyle='#496779';ctx.lineWidth=1;ctx.beginPath();ctx.arc(cx,cy,r,0,Math.PI*2);ctx.stroke();ctx.textAlign='center';ctx.font='600 14px "IBM Plex Mono",monospace';for(const [az,name] of [[0,'N'],[90,'E'],[180,'S'],[270,'W']]){const p=project(az,0);ctx.fillStyle=p.depth<-.001?'#6e8391':'#e0bd87';const dx=p.x-cx,dy=p.y-cy,length=Math.hypot(dx,dy)||1;ctx.fillText(name,p.x+dx/length*23,p.y+dy/length*23+5);}

ctx.font='10px "IBM Plex Mono",monospace';for(const [alt,name] of [[90,'ZENITH · 90°'],[-90,'NADIR · −90°']]){const p=project(0,alt);if(p.depth>.1){ctx.fillStyle='#849eaf';ctx.fillText(name,p.x,p.y+18);}}

drawMountActual(ctx,canvas,project);
points=[];for(const o of data?.objects||[]){const p=project(o.azimuth_deg,o.altitude_deg);const persistent=skyMode==='satellite'&&o.id===selected;if((!persistent&&(p.depth<0||Math.hypot(p.x-cx,p.y-cy)>r-12))||p.x<8||p.x>w-8||p.y<8||p.y>h-8)continue;ctx.fillStyle=o.id===selected?'#f5bc75':'#65e5c4';ctx.globalAlpha=o.id===selected?1:o.altitude_deg<0?.35:.85;ctx.beginPath();ctx.arc(p.x,p.y,o.id===selected?4:skyMode==='satellite'?1.8:3,0,Math.PI*2);ctx.fill();points.push({...p,o});}ctx.globalAlpha=1;

for(const back of [true,false]){
  for(let i=1;i<track.length;i++){
    const a=track[i-1],b=track[i];
    let segments=[[a,b]];
    if((a.altitude_deg<0)!==(b.altitude_deg<0)){
      const f=a.altitude_deg/(a.altitude_deg-b.altitude_deg),delta=(b.azimuth_deg-a.azimuth_deg+540)%360-180;
      const crossing={azimuth_deg:(a.azimuth_deg+f*delta+360)%360,altitude_deg:0};
      segments=[[a,crossing],[crossing,b]];
    }
    for(const segment of segments){
      const below=segment.some(p=>p.altitude_deg<0);
      line(segment.map(p=>[p.azimuth_deg,p.altitude_deg]),back?'#3d625e':'#f5bc75',back,1.4,back||below||(horizonSeconds!==null&&a.seconds>=horizonSeconds));
    }
  }
}

const chosen=points.find(p=>p.o.id===selected);if(chosen){ctx.strokeStyle='#f5bc75';ctx.setLineDash(chosen.depth<0?[3,3]:[]);ctx.beginPath();ctx.arc(chosen.x,chosen.y,9,0,Math.PI*2);ctx.stroke();ctx.setLineDash([]);ctx.fillStyle='#f5bc75';ctx.textAlign='left';ctx.fillText(chosen.o.name+(skyMode==='satellite'&&chosen.o.altitude_deg<0?' · Below horizon':''),chosen.x+12,chosen.y-8);}

// Altitude values follow their true latitude circles as the sphere rotates.

ctx.font='11px "IBM Plex Mono",monospace';ctx.textAlign='center';

for(const alt of [-60,-30,0,30,60]){const candidates=Array.from({length:72},(_,i)=>project(i*5,alt)).filter(p=>p.depth>.12&&p.x>cx-r*.65&&p.x<cx+r*.65&&p.y>20&&p.y<h-20);if(!candidates.length)continue;const p=candidates.reduce((a,b)=>b.y<a.y?b:a);const label=alt===0?'HORIZON · 0°':`Alt ${alt>0?'+':''}${alt}°`;const width=ctx.measureText(label).width;ctx.fillStyle='#10202de8';ctx.fillRect(p.x-width/2-5,p.y-8,width+10,16);ctx.fillStyle=alt===0?'#dfbb7e':'#abc4d5';ctx.fillText(label,p.x,p.y+4);}

canvas.dataset.selected=selected;canvas.dataset.selectedVisible=String(!!chosen);canvas.dataset.belowHorizon=points.filter(p=>p.o.altitude_deg<0).length;canvas.dataset.plotted=points.length;if(data){$('#satellite-sky-status').textContent=`${data.objects.length} OBJECTS`;$('#satellite-sky-position').textContent=skyMode==='satellite'?`${data.sample_interval_ms?.toFixed(0)||'—'} ms · SGP4 ${data.calculation_ms.toFixed(1)} ms · CPU ${data.cpu_ms.toFixed(1)} ms/frame`:`NASA/JPL SBDB · ${data.utc?.replace('T',' ').slice(0,19)||'—'} UTC`;canvas.dataset.category=skyMode;}}

canvas.onpointerdown=e=>{if(e.button!==0)return;drag={id:e.pointerId,x:e.clientX,y:e.clientY,yaw,tilt,moved:false};canvas.setPointerCapture(e.pointerId);canvas.style.cursor='grabbing';};

canvas.onpointermove=e=>{const pointerBox=canvas.getBoundingClientRect();lastPointer={x:e.clientX-pointerBox.left,y:e.clientY-pointerBox.top};if(drag){const dx=e.clientX-drag.x,dy=e.clientY-drag.y;if(Math.hypot(dx,dy)>4)drag.moved=true;if(drag.moved){yaw=drag.yaw+dx*.006;tilt=Math.max(0,Math.min(Math.PI,drag.tilt-dy*.008));draw();}return;}const b=canvas.getBoundingClientRect(),p=points.find(p=>Math.hypot(p.x-e.clientX+b.left,p.y-e.clientY+b.top)<10);canvas.style.cursor=p?'pointer':'grab';canvas.title=p?`${p.o.name||p.o.id} · Alt ${p.o.altitude_deg.toFixed(1)}°${p.o.altitude_deg<0?' · Below horizon':''}`:'';};

canvas.onpointerup=e=>{if(!drag)return;const moved=drag.moved;drag=null;canvas.releasePointerCapture(e.pointerId);canvas.style.cursor='grab';if(!moved)pick(e);};canvas.onpointercancel=()=>{drag=null;canvas.style.cursor='grab';};

function setZoom(value,anchor=lastPointer){const next=Math.max(.65,Math.min(3,value)),ratio=next/zoom;if(anchor){const box=canvas.getBoundingClientRect(),cx=(box.width+Math.min(175,box.width*.27))/2+offsetX,cy=box.height/2+offsetY;offsetX+=(anchor.x-cx)*(1-ratio);offsetY+=(anchor.y-cy)*(1-ratio);}zoom=next;draw();}canvas.addEventListener('wheel',e=>{e.preventDefault();const box=canvas.getBoundingClientRect();lastPointer={x:e.clientX-box.left,y:e.clientY-box.top};setZoom(zoom*Math.exp(-e.deltaY*.001),lastPointer);},{passive:false});new ResizeObserver(draw).observe(canvas);


$('#satellite-zoom-in').onclick=()=>setZoom(zoom*1.2);$('#satellite-zoom-out').onclick=()=>setZoom(zoom/1.2);$('#satellite-reset-view').onclick=()=>{yaw=tilt=offsetX=offsetY=0;zoom=1;lastPointer=null;draw();};

export function focusSkyObject(row){selected=row.id;track=[];offsetX=offsetY=0;if(row.altitude_deg<0){yaw=-row.azimuth_deg*Math.PI/180;tilt=Math.max(0,Math.min(Math.PI,(70-row.altitude_deg)*Math.PI/180));}else{yaw=tilt=0;}draw();}

canvas.addEventListener('contextmenu',e=>{const box=canvas.getBoundingClientRect(),x=e.clientX-box.left,y=e.clientY-box.top;const near=points.reduce((best,p)=>{const d=Math.hypot(p.x-x,p.y-y);return !best||d<best.d?{...p,d}:best;},null);showInterestingMenu(e,near&&near.d<16?{...near.o,category:skyMode==='solar'?'planet':skyMode}:null);});

document.addEventListener('sky-region-overlay-changed',()=>requestAnimationFrame(draw));
