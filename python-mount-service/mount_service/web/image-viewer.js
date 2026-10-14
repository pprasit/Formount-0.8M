import {ImageStretch} from './image-stretch.js';
window.viewerBooted=true;
const $=s=>document.querySelector(s),image=$('#viewer-image'),viewport=$('#viewer-viewport');
const stretch=new ImageStretch($('#viewer-stretch'),$('#viewer-stretch-tools'),text=>{$('#viewer-message').textContent=text;$('#viewer-retry').hidden=false;});
stretch.maximum=2560;stretch.onReady=info=>{$('#viewer-details').textContent=`Preview ${info.width} x ${info.height} px / ${(info.bytes/1024).toFixed(0)} KB local stretch source / Original ${record.metadata.width} x ${record.metadata.height} px`;$('#viewer-message').textContent=info.maximum===2560?'Preview ready':'Quick preview ready. Loading detailed preview...';$('#viewer-empty').hidden=true;};
let record,scale=1,x=0,y=0,drag,moved=false;
function apply(){image.style.transform=`translate(${x}px,${y}px) scale(${scale})`;$('#viewer-zoom-level').textContent=Math.round(scale*100)+'%';viewport.classList.toggle('zoomed',scale>1);}
function zoom(factor){scale=Math.max(1,Math.min(12,scale*factor));if(scale===1)x=y=0;apply();}
$('#viewer-zoom-in').onclick=()=>zoom(1.25);$('#viewer-zoom-out').onclick=()=>zoom(.8);$('#viewer-fit').onclick=()=>{scale=1;x=y=0;apply();};
$('#viewer-header').onclick=()=>{$('#viewer-header-panel').hidden=!$('#viewer-header-panel').hidden;$('.viewer-body').classList.toggle('header-hidden',$('#viewer-header-panel').hidden);};
image.addEventListener('pointerdown',event=>{moved=false;if(scale<=1||event.button!==0)return;image.setPointerCapture(event.pointerId);drag={id:event.pointerId,x:event.clientX,y:event.clientY,startX:x,startY:y};});
image.addEventListener('pointermove',event=>{if(!drag||event.pointerId!==drag.id)return;if(Math.hypot(event.clientX-drag.x,event.clientY-drag.y)>3)moved=true;x=drag.startX+event.clientX-drag.x;y=drag.startY+event.clientY-drag.y;apply();});
for(const name of ['pointerup','pointercancel','lostpointercapture'])image.addEventListener(name,()=>drag=null);

viewport.addEventListener('wheel',event=>{event.preventDefault();zoom(event.deltaY<0?1.1:1/1.1);},{passive:false});
let loadVersion=0,currentFilename;
async function load(filename=new URLSearchParams(location.search).get('image')){
  if(filename===currentFilename)return;currentFilename=filename;const version=++loadVersion;stretch.token++;stretch.setEnabled(false);scale=1;x=y=0;apply();if(!filename)throw Error('No image selected');
  image.src='/api/camera/images/'+encodeURIComponent(filename)+'/display.webp?maximum=640';image.onload=()=>{image.hidden=false;$('#viewer-empty').hidden=true;};image.onerror=()=>{$('#viewer-message').textContent='Preview could not load. Retry loading.';$('#viewer-retry').hidden=false;};
  const r=await fetch('/api/camera/images/'+encodeURIComponent(filename),{signal:AbortSignal.timeout(15000)});if(!r.ok)throw Error('Image not found');const nextRecord=await r.json();if(version!==loadVersion)return;record=nextRecord;stretch.setEnabled(true);const url=new URL(location.href);url.searchParams.set('image',filename);history.replaceState(null,'',url);
  document.title=record.filename+' · NARIT TCS';$('#viewer-filename').textContent=record.filename;
  $('#viewer-details').textContent=`${record.metadata.width} × ${record.metadata.height} px · ${record.metadata.exposure_s??'—'} s`;
  const rows=$('#viewer-header-rows');rows.replaceChildren();for(const card of record.metadata.fits_header||[]){const row=document.createElement('tr'),key=document.createElement('td'),value=document.createElement('td');key.textContent=card.key;value.textContent=String(card.value??'');if(card.comment){const comment=document.createElement('span');comment.className='viewer-header-comment';comment.textContent=card.comment;value.append(comment);}row.append(key,value);rows.append(row);}
  $('#viewer-download').href='/api/camera/images/'+encodeURIComponent(filename)+'/fits';$('#viewer-download').hidden=false;$('#viewer-header').disabled=false;
  await stretch.load(record,image);
}
load().catch(error=>{currentFilename=null;$('#viewer-empty').textContent=error.message;$('#viewer-message').textContent=error.message;$('#viewer-retry').hidden=false;});

const link=new URLSearchParams(location.search).get('link');
if(link){window.name='NARIT-ImageViewer-'+link;const channel=new BroadcastChannel('imaging-viewer-'+link);stretch.link(channel);channel.onmessage=event=>{if(event.data?.type==='focus'){window.focus();return;}if(event.data?.type==='select'&&typeof event.data.filename==='string')load(event.data.filename).catch(error=>{currentFilename=null;$('#viewer-message').textContent=error.message;});};channel.postMessage({type:'ready'});}
