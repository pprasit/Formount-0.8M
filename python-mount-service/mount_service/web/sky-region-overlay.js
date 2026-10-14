let state=null,limits=null;
for(const type of ['mount-state','drive-limits'])document.addEventListener(type,e=>{if(type==='mount-state')state=e.detail;else if(e.detail.axis==='azimuth')limits=e.detail;document.dispatchEvent(new Event('sky-region-overlay-changed'));});
export function drawRegions(ctx,canvas,project,cx,cy,r){ctx.save();ctx.font='10px monospace';ctx.textAlign='center';ctx.textBaseline='middle';for(let az=0;az<360;az+=30){if(az%90===0)continue;const p=project(az,0),factor=(r+29)/r;ctx.fillStyle=p.depth<0?'#536b7d':'#91aac2';ctx.fillText(az+'°',cx+(p.x-cx)*factor,cy+(p.y-cy)*factor);}const values=limits&&limits.generation===state?.drive?.generation?limits.values:null;const lower=values?.['VL[3]'],upper=values?.['VH[3]'];canvas.dataset.regionLimits=Number.isFinite(lower)&&Number.isFinite(upper)?JSON.stringify([lower,upper]):'unavailable';if(!Number.isFinite(lower)||!Number.isFinite(upper)){ctx.restore();return;}
for(const [name,end,offset,color,raw] of [['CW',upper,13,'#a695ff',upper],['CCW',lower>0?lower-360:lower,7,'#65e5c4',lower]]){const factor=(r+offset)/r,count=Math.max(1,Math.ceil(Math.abs(end)));for(const back of [true,false]){ctx.beginPath();let started=false;for(let i=0;i<=count;i++){const p=project(end*i/count,0),visible=back?p.depth<0:p.depth>=0;if(!visible){started=false;continue;}const x=cx+(p.x-cx)*factor,y=cy+(p.y-cy)*factor;started?ctx.lineTo(x,y):ctx.moveTo(x,y);started=true;}ctx.strokeStyle=color;ctx.globalAlpha=back?.3:.85;ctx.lineWidth=2.5;ctx.setLineDash(back?[3,4]:[]);ctx.stroke();}ctx.globalAlpha=1;ctx.setLineDash([]);const p=project(end,0);ctx.strokeStyle=color;ctx.lineWidth=1;ctx.setLineDash([3,5]);ctx.beginPath();const dx=p.x-cx,dy=p.y-cy,len=Math.hypot(dx,dy)||1;ctx.moveTo(p.x-dx/len*7,p.y-dy/len*7);ctx.lineTo(p.x+dx/len*7,p.y+dy/len*7);ctx.stroke();ctx.setLineDash([]);ctx.fillStyle=color;ctx.beginPath();ctx.arc(p.x,p.y,3,0,Math.PI*2);ctx.fill();const side=name==='CW'?1:-1;
// Labels stay at the reset-view locations, independent of rotation and zoom.
const baseRadius=r/(Number(canvas.dataset.zoom)||1);
const labelCx=(canvas.clientWidth+(canvas.id==='object-sky'?240:Math.min(175,canvas.clientWidth*.27)))/2;
const labelCy=canvas.clientHeight/2-(canvas.id==='object-sky'?4:0);
// Match both text blocks to the left/right edges of the reset-view circle.
const blockWidth=116;
const x=side>0?Math.min(canvas.clientWidth-8,labelCx+baseRadius+22):Math.max(8,labelCx-baseRadius-22);
const y=Math.max(42,Math.min(canvas.clientHeight-32,labelCy+baseRadius*.9+16));
const textLeft=side>0?x-blockWidth:x,textRight=side>0?x:x+blockWidth;
// The callout lands outside the block at its top edge, before the text starts.
const leaderX=(textLeft+textRight)/2,leaderY=y-25;
// Mirror the callout angle (30 degrees) on both sides. Intersect the
// current projected region with that ray; use a direct segment to the nearest visible arc if no intersection exists.
const ux=-side*Math.cos(Math.PI/6),uy=-Math.sin(Math.PI/6);
let anchor=null,previous=null,nearest=null;
const elbow={x:leaderX+ux*48,y:leaderY+uy*48};
for(let i=0;i<=count;i++){
 const q=project(end*i/count,0),ax=cx+(q.x-cx)*factor,ay=cy+(q.y-cy)*factor;
 const cross=(ax-leaderX)*uy-(ay-leaderY)*ux;
 if(q.depth>=-0.001){const distance=(ax-elbow.x)**2+(ay-elbow.y)**2;if(!nearest||distance<nearest.distance)nearest={x:ax,y:ay,distance};}
 if(previous&&previous.depth>=-0.001&&q.depth>=-0.001&&previous.cross*cross<=0&&Math.abs(previous.cross-cross)>1e-9){
  const f=previous.cross/(previous.cross-cross),x=previous.x+(ax-previous.x)*f,y=previous.y+(ay-previous.y)*f,t=(x-leaderX)*ux+(y-leaderY)*uy;
  if(t>=3&&side*(x-cx)>=0&&(!anchor||t<anchor.length))anchor={x,y,length:t};
 }
 previous={x:ax,y:ay,cross,depth:q.depth};
}
const straight=!!anchor;anchor=anchor||nearest;
if(anchor){ctx.strokeStyle=color;ctx.globalAlpha=1;ctx.lineWidth=1.5;ctx.setLineDash([4,4]);ctx.beginPath();ctx.moveTo(anchor.x,anchor.y);ctx.lineTo(leaderX,leaderY);ctx.stroke();ctx.setLineDash([]);ctx.fillStyle=color;ctx.beginPath();ctx.arc(anchor.x,anchor.y,2.5,0,Math.PI*2);ctx.fill();canvas.dataset['regionLeader'+name]=JSON.stringify({from:[anchor.x,anchor.y],to:[leaderX,leaderY],straight,angle_deg:30});}
ctx.textAlign=side>0?'right':'left';ctx.fillStyle=color;ctx.font='bold 10px monospace';ctx.fillText(name+' REGION',x,y-12);ctx.font='9px monospace';ctx.fillText(name+' limit '+raw.toFixed(2)+'°',x,y+13);}
ctx.restore();}
export function drawMountActual(ctx,canvas,project){const az=state?.axes?.find(a=>a.id==='azimuth'),alt=state?.axes?.find(a=>a.id==='altitude');canvas.dataset.mountVisible='false';if(!az?.valid||!alt?.valid||!Number.isFinite(az.position_deg+alt.position_deg))return;canvas.dataset.mountAz=String(az.position_deg);canvas.dataset.mountAlt=String(alt.position_deg);const p=project(az.position_deg,alt.position_deg);if(p.depth<0||p.x<0||p.y<0||p.x>canvas.clientWidth||p.y>canvas.clientHeight)return;canvas.dataset.mountVisible='true';ctx.save();ctx.strokeStyle=ctx.fillStyle='#65e5c4';ctx.lineWidth=2;ctx.beginPath();ctx.arc(p.x,p.y,7,0,Math.PI*2);ctx.stroke();ctx.beginPath();ctx.arc(p.x,p.y,2.5,0,Math.PI*2);ctx.fill();ctx.beginPath();for(const [dx,dy] of [[-1,0],[1,0],[0,-1],[0,1]]){ctx.moveTo(p.x+dx*10,p.y+dy*10);ctx.lineTo(p.x+dx*17,p.y+dy*17);}ctx.stroke();ctx.font='600 11px monospace';ctx.textAlign='left';const label='MOUNT / ACTUAL',width=ctx.measureText(label).width,x=Math.max(5,Math.min(canvas.clientWidth-width-8,p.x+21)),y=Math.max(18,p.y-15);ctx.fillStyle='#10202deb';ctx.fillRect(x-4,y-12,width+8,19);ctx.fillStyle='#65e5c4';ctx.fillText(label,x,y+1);ctx.restore();}
