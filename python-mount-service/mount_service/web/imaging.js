import {ImageStretch} from './image-stretch.js';
const $=s=>document.querySelector(s);let camera,deviceKey='',busy=false,mode='exposure',imageRecord,videoGeneration=0,previewPending=false,displayFPS=0,videoSocket=null,videoTimes=[],videoClock=0,videoTransport='',telemetrySeenAt=performance.now(),previewYielding=false,deliveryFPS=3,videoByteBudget=8192,measuredBandwidth=0;
async function api(path,body){const r=await fetch(path,body===undefined?{}:{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(body)});const d=await r.json();if(!r.ok)throw Error(typeof d.detail==='string'?d.detail:'Invalid camera settings');return d;}
const viewerLink=sessionStorage.getItem('imaging-viewer-link')||crypto.randomUUID();sessionStorage.setItem('imaging-viewer-link',viewerLink);
const viewerChannel=new BroadcastChannel('imaging-viewer-'+viewerLink);
let selectedFilename=null,viewerWindow=null;
viewerChannel.onmessage=event=>{if(event.data?.type==='ready'&&selectedFilename)viewerChannel.postMessage({type:'select',filename:selectedFilename});};
function showFitsHeader(record){
  selectedFilename=record?.filename||null;if(selectedFilename)viewerChannel.postMessage({type:"select",filename:selectedFilename});
  const date=new Date(record?.metadata?.capture_utc||'');
  $('#acquisition-capture-time').textContent=Number.isNaN(date.getTime())?'':new Intl.DateTimeFormat('en-GB',{day:'2-digit',month:'2-digit',year:'numeric',hour:'2-digit',minute:'2-digit',second:'2-digit',hour12:false}).format(date);
  if(!Number.isNaN(date.getTime()))$('#acquisition-capture-time').dateTime=date.toISOString();
  $('#acquisition-header-filename').textContent=record?.filename||'No FITS header for live video';
  const body=$('#acquisition-header-rows');body.replaceChildren();
  for(const card of record?.metadata?.fits_header||[]){
    const row=document.createElement('tr'),key=document.createElement('td'),value=document.createElement('td');
    key.textContent=String(card.key??'');value.textContent=String(card.value??'');
    if(card.comment){const comment=document.createElement('span');comment.className='acquisition-fits-comment';comment.textContent=card.comment;value.append(comment);}
    row.append(key,value);body.append(row);
  }
}
function message(text){$('#acquisition-message').textContent=text;}
const stretch=new ImageStretch($('#acquisition-stretch'),$('.acquisition-preview-footer'),message);stretch.link(viewerChannel);
function controls(){const d=camera?.connected,working=busy||camera?.exposing;$('#acquisition-scan').disabled=working;$('#acquisition-connect').disabled=working||!$('#acquisition-device').value;$('#acquisition-connect').textContent=d?'Disconnect':'Connect';$('#acquisition-device').disabled=working||!!d;$('#acquisition-expose').disabled=working||!d||(mode==='video'&&!d.video_supported);$('#acquisition-abort').disabled=!working;$('#acquisition-gain').disabled=!d?.gain_supported||working;$('#acquisition-roi-enabled').disabled=working||!d?.roi_supported;for(const el of document.querySelectorAll('#acquisition-form input:not(#acquisition-gain):not(#acquisition-roi-enabled),#acquisition-form select:not(#acquisition-device)'))el.disabled=working;$('#acquisition-exposure').disabled=working||mode==='exposure'&&$('#acquisition-frame-type').value==='Bias';stretch.setEnabled(!!imageRecord&&mode!=='video');$('#acquisition-new-series').disabled=working;document.querySelector('[data-acquisition-mode=exposure]').closest('nav').hidden=!d?.video_supported;const video=document.querySelector('[data-acquisition-mode=video]');video.hidden=!d?.video_supported;video.disabled=working;if(mode==='video'&&!d?.video_supported){mode='exposure';document.querySelector('[data-acquisition-mode=exposure]').classList.add('active');video.classList.remove('active');$('#acquisition-frame-field').hidden=true;$('#acquisition-video-tools').hidden=true;$('#acquisition-dark-field').hidden=false;$('#acquisition-expose').textContent='Expose';}}
function refresh(value){camera=value;renderProgress();const devices=value.devices||[];const select=$('#acquisition-device'),old=select.value;const keys=JSON.stringify(devices.map(d=>[d.key,d.name]));if(select.dataset.keys!==keys){select.replaceChildren(new Option('Choose camera',''));devices.forEach(d=>select.add(new Option(d.name,d.key)));select.dataset.keys=keys;select.value=value.connected?.key||old||(devices.length===1?devices[0].key:'');}const d=value.connected;if(d&&deviceKey!==d.key){deviceKey=d.key;$('#acquisition-frame-type').replaceChildren(...(d.frame_types||['Light','Dark']).map(n=>new Option(n,n)));const readout=$('#acquisition-readout-fields');readout.replaceChildren();const labels={horizontal_speed:'Horizontal readout / ADC / output',vertical_speed:'Vertical shift speed',read_mode:'Readout mode',camera_mode:'Readout speed / mode',PixelReadoutRate:'Pixel readout rate',SimplePreAmpGainControl:'Preamp gain mode',ElectronicShutteringMode:'Electronic shutter mode'};for(const [key,choices] of Object.entries(d.readout_options||{})){if(!choices.length)continue;const label=document.createElement('label');label.className='field';label.textContent=labels[key]||key;const select=document.createElement('select');select.dataset.cameraReadout=key;select.add(new Option('Camera default',''));choices.forEach(o=>select.add(new Option(o.label,o.value)));label.append(select);readout.append(label);}readout.hidden=!readout.children.length;configureVideoTools(d);$('#acquisition-bin').replaceChildren(...(d.bins||[1,2,3,4]).map(n=>new Option(`${n} × ${n}`,n)));const caps=d.controls?.['0'];$('#acquisition-gain').min=caps?.minimum??0;$('#acquisition-gain').max=caps?.maximum??1000;$('#acquisition-gain').value=d.gain_supported?(caps?.default??100):0;$('#acquisition-exposure').min=d.exposure_min_s??.001;$('#acquisition-exposure').max=Math.min(600,d.exposure_max_s??600);$('#acquisition-roi-width').value=d.width;$('#acquisition-roi-height').value=d.height;$('#acquisition-roi-enabled').checked=false;$('#acquisition-roi-fields').hidden=true;}if(!d)deviceKey='';$('#acquisition-capabilities').textContent=d?`${d.name} · ${d.width} × ${d.height} px${d.pixel_um?' · '+d.pixel_um+' µm':''} · ${d.bits||16} bit${d.simulated?' · Simulation':''}`:'Connect a camera to configure acquisition.';controls();}
async function job(path,body){if(busy)return;busy=true;controls();message('Working…');try{let j=await api(path,body);if(path==='/api/camera/video/start')streamVideo(videoGeneration);while(j.status==='running'){await new Promise(r=>setTimeout(r,300));j=await api('/api/camera/jobs/'+j.id);await loadStatus();}if(j.status==='failed')throw Error(j.error);await loadStatus();message('Complete');return j.result;}catch(e){message(e.message);previewPending=false;$('#acquisition-phase').textContent=camera?.acquisition?.phase==='Cancelled'?'Cancelled':'Error';$('#acquisition-progress-bar').value=0;}finally{busy=false;controls();}}
async function loadStatus(){refresh(await api('/api/camera/status'));}
$('#acquisition-scan').onclick=()=>job('/api/camera/scan',{});$('#acquisition-connect').onclick=()=>job(camera?.connected?'/api/camera/disconnect':'/api/camera/connect',{path:$('#acquisition-device').value});$('#acquisition-device').onchange=controls;
$('#acquisition-roi-enabled').onchange=()=>$('#acquisition-roi-fields').hidden=!$('#acquisition-roi-enabled').checked;
$('#acquisition-abort').onclick=async()=>{try{await api('/api/camera/abort',{});message('Stopping acquisition…');}catch(e){message(e.message);}};
function settings(){return {exposure_s:Number($('#acquisition-exposure').value),gain:camera.connected.gain_supported?Number($('#acquisition-gain').value):0,binning:Number($('#acquisition-bin').value),dark:mode==='exposure'&&($('#acquisition-frame-type').value!=='Light'),...(mode==='exposure'?{frame_type:$('#acquisition-frame-type').value,readout:Object.fromEntries([...document.querySelectorAll('[data-camera-readout]')].filter(e=>e.value).map(e=>[e.dataset.cameraReadout,e.value]))}:{}),telescope_id:$('#acquisition-telescope').value||null,roi:$('#acquisition-roi-enabled').checked?Object.fromEntries(['x','y','width','height'].map(k=>[k,Number($('#acquisition-roi-'+k).value)])):null};}
$('#acquisition-form').onsubmit=async e=>{e.preventDefault();if(mode==='video'){++videoGeneration;displayFPS=0;videoTimes=[];videoClock=performance.now();archiveCurrent();delete $('#acquisition-image').closest('.acquisition-viewport').dataset.filename;activeImage=$('#acquisition-image');resetZoom();imageRecord=null;showFitsHeader(null);$('#acquisition-image-details').textContent='Live video';$('#acquisition-download').hidden=true;$('#acquisition-video-download').hidden=true;try{const result=await job('/api/camera/video/start',{...settings(),frames:Number($('#acquisition-frames').value),record:$('#acquisition-video-record').value==='record',image_format:$('#acquisition-video-format').value,preview_fps:Number($('#acquisition-preview-fps').value),auto_exposure:$('#acquisition-auto-exposure').checked,auto_gain:$('#acquisition-auto-gain').checked,video_controls:Object.fromEntries([...document.querySelectorAll('[data-video-control]')].filter(e=>e.value!=='').map(e=>[e.dataset.videoControl,Number(e.value)]))});if(result){$('#acquisition-video-download').hidden=!result.fits;$('#acquisition-image-details').textContent=`${result.frames||0} video frames · ${result.width||0} × ${result.height||0} px · ${result.format} ${result.fits?'FITS cube':'live preview'}`;}}finally{videoGeneration++;videoSocket?.close();videoSocket=null;renderProgress();}return;}previewPending=true;const result=await job('/api/camera/capture',settings());if(!result){previewPending=false;return;}archiveCurrent();activeImage=$('#acquisition-image');activeImage.closest('.acquisition-viewport').dataset.filename=result.filename;resetZoom();$('#acquisition-gallery').scrollTo({top:0,behavior:'smooth'});$('#acquisition-image').src=result.preview_url;try{await $('#acquisition-image').decode();}catch(e){previewPending=false;message('Preview could not load; FITS capture is available.');return;}previewPending=false;renderProgress();$('#acquisition-image').hidden=false;$('#acquisition-empty').hidden=true;$('#acquisition-download').hidden=false;$('#acquisition-download').href='/api/camera/images/'+result.filename+'/fits';$('#acquisition-image-details').textContent=`${result.width} × ${result.height} px · ${result.exposure_s} s · ${result.binning} × ${result.binning} · ${result.capture_utc}`;try{imageRecord=await api('/api/camera/images/'+result.filename);showFitsHeader(imageRecord);updateSeriesInfo(await api('/api/camera/series/latest'));await stretch.load(imageRecord,activeImage);controls();}catch(e){message(e.message);}};
for(const button of document.querySelectorAll('[data-acquisition-mode]'))button.onclick=()=>{if(busy||camera?.exposing)return;mode=button.dataset.acquisitionMode;updateVideoBudget();document.querySelectorAll('[data-acquisition-mode]').forEach(b=>b.classList.toggle('active',b===button));$('#acquisition-frame-field').hidden=mode!=='video'||$('#acquisition-video-record').value!=='record';$('#acquisition-video-tools').hidden=mode!=='video';if(mode==='video'&&camera.connected.simulated){$('#acquisition-exposure').value=.033;$('#acquisition-video-roi').value='640x480';applyVideoROI();}$('#acquisition-dark-field').hidden=mode==='video';$('#acquisition-expose').textContent=mode==='video'?'Start video':'Expose';$('#acquisition-mode-help').textContent=mode==='video'?'Live preview sends the newest frame. Recording saves original frames in a FITS cube (256 MiB limit).':'FITS retains the original sensor values. Preview uses automatic stretching.';controls();renderProgress();};
document.addEventListener('mount-state',e=>{telemetrySeenAt=performance.now();if(e.detail.observatory?.camera)refresh(e.detail.observatory.camera);});
api('/api/pointing/telescopes').then(d=>{d.telescopes.forEach(t=>$('#acquisition-telescope').add(new Option(t.name,t.id)));if(d.telescopes.length===1)$('#acquisition-telescope').value=d.telescopes[0].id;}).catch(e=>message(e.message));loadStatus().catch(e=>message(e.message));

function updateVideoBudget(){if(!camera?.connected)return;const s=settings(),d=camera.connected,w=Math.floor((s.roi?.width||d.width)/s.binning),h=Math.floor((s.roi?.height||d.height)/s.binning);const maximum=Math.max(1,Math.min(1000,Math.floor(256*1024*1024/(w*h*($('#acquisition-video-format').value==='RAW8'?1:2)))));$('#acquisition-frames').max=maximum;if(Number($('#acquisition-frames').value)>maximum)$('#acquisition-frames').value=maximum;}
for(const id of ['acquisition-bin','acquisition-roi-enabled','acquisition-roi-width','acquisition-roi-height'])$('#'+id).addEventListener('change',updateVideoBudget);

let activeImage=$('#acquisition-image');const transforms=new WeakMap();
function zoomState(){if(!transforms.has(activeImage))transforms.set(activeImage,{scale:1,x:0,y:0});return transforms.get(activeImage);}
function applyZoom(){activeImage.style.transform='none';activeImage.style.width='100%';activeImage.style.height='100%';}
function resetZoom(){applyZoom();}
function archiveCurrent(){const current=$('#acquisition-image');if(current.hidden||!current.closest('.acquisition-viewport').dataset.filename)return;const frame=current.closest('.acquisition-viewport').cloneNode(true);frame.removeAttribute('id');frame.querySelectorAll('[id]').forEach(e=>e.removeAttribute('id'));frame.querySelector('img').hidden=false;frame.querySelector('img').removeAttribute('style');frame.querySelector('.acquisition-image-surface')?.removeAttribute('style');frame.querySelector('img').src='/api/camera/preview/'+encodeURIComponent(frame.dataset.filename.replace(/\.fits$/,'.png'))+'?v='+Date.now();delete frame.querySelector('img').dataset.stretchBlob;frame.tabIndex=0;frame.setAttribute('aria-label','Captured image '+frame.dataset.filename);const empty=frame.querySelector('span');if(empty)empty.remove();$('#acquisition-gallery').insertBefore(frame,current.closest('.acquisition-viewport').nextSibling);}
async function selectImage(image){if(activeImage===image&&imageRecord?.filename===image.closest('.acquisition-viewport').dataset.filename)return;activeImage=image;document.querySelectorAll('#acquisition-gallery .acquisition-viewport').forEach(e=>e.classList.toggle('selected',e.contains(image)));applyZoom();const filename=image.closest('.acquisition-viewport').dataset.filename;if(!filename)return;try{const record=await api('/api/camera/images/'+encodeURIComponent(filename));if(activeImage!==image||image.closest('.acquisition-viewport').dataset.filename!==filename)return;imageRecord=record;showFitsHeader(record);await stretch.load(imageRecord,image);$('#acquisition-download').href='/api/camera/images/'+filename+'/fits';$('#acquisition-image-details').textContent=`${imageRecord.metadata.width} × ${imageRecord.metadata.height} px · ${imageRecord.metadata.capture_utc||filename}`;controls();}catch(e){message(e.message);}}
let drag=null,gestureMoved=false,clickTimer;
$("#acquisition-gallery").addEventListener("keydown",e=>{if(e.key==="Enter"&&e.target.classList.contains("acquisition-viewport")){selectImage(e.target.querySelector("img"));}});

$("#acquisition-frame-type").onchange=()=>{$("#acquisition-exposure").disabled=$("#acquisition-frame-type").value==="Bias";};

$("#acquisition-readout-fields").addEventListener("change",e=>{if(e.target.dataset.cameraReadout==="read_mode"&&e.target.value==="0"){$("#acquisition-roi-enabled").checked=false;$("#acquisition-roi-fields").hidden=true;}});

function configureVideoTools(d){
  for(const [name,key] of [['exposure','1'],['gain','0']]){$('#acquisition-auto-'+name+'-field').hidden=!d.controls?.[key]?.auto;$('#acquisition-auto-'+name).checked=false;}
  const formats=(d.formats||[]).filter(f=>f===0||f===2).map(f=>f===0?'RAW8':'RAW16');$('#acquisition-video-format').replaceChildren(...formats.map(f=>new Option(f,f)));
  const container=$('#acquisition-video-native');container.replaceChildren();
  const labels={2:'Gamma',3:'White balance red',4:'White balance blue',5:'Offset',6:'USB traffic',9:'Flip (0 none / 1 H / 2 V / 3 both)',10:'Auto gain maximum',11:'Auto exposure maximum (ms)',12:'Auto target brightness',13:'Hardware binning',14:'High-speed mode',18:'Mono binning'};
  for(const [key,labelText] of Object.entries(labels)){const cap=d.controls?.[key];if(!cap?.writable)continue;const label=document.createElement('label');label.className='field';label.textContent=labelText;label.title=cap.description||'';const input=document.createElement('input');input.type='number';input.min=cap.minimum;input.max=cap.maximum;input.step='1';input.placeholder='Camera default';input.dataset.videoControl=key;label.append(input);container.append(label);}
}
function applyVideoROI(){const value=$('#acquisition-video-roi').value;if(value==='custom'){$('#acquisition-roi-fields').hidden=!$('#acquisition-roi-enabled').checked;return;}const [w,h]=value.split('x').map(Number),d=camera.connected,bin=Number($('#acquisition-bin').value);$('#acquisition-roi-enabled').checked=true;$('#acquisition-roi-fields').hidden=true;$('#acquisition-roi-x').value=0;$('#acquisition-roi-y').value=0;$('#acquisition-roi-width').value=Math.floor(Math.min(w,d.width)/bin)*bin;$('#acquisition-roi-height').value=Math.floor(Math.min(h,d.height)/bin)*bin;updateVideoBudget();}
$('#acquisition-video-roi').onchange=applyVideoROI;
$('#acquisition-video-format').onchange=updateVideoBudget;
$('#acquisition-video-record').onchange=()=>{$('#acquisition-frame-field').hidden=$('#acquisition-video-record').value!=='record';updateVideoBudget();};
function countVideoFrame(){const now=performance.now();videoTimes.push(now);videoTimes=videoTimes.filter(t=>now-t<2000);displayFPS=videoTimes.length/Math.max(.5,Math.min(2,(now-videoClock)/1000));}
async function displayVideoFrame(blob,generation){
  const url=URL.createObjectURL(blob),image=$('#acquisition-image'),old=image.dataset.blob;
  image.src=url;
  try{await image.decode();}catch{URL.revokeObjectURL(url);return;}
  if(generation!==videoGeneration){URL.revokeObjectURL(url);return;}
  image.dataset.blob=url;if(old)URL.revokeObjectURL(old);
  image.hidden=false;$('#acquisition-empty').hidden=true;
  await new Promise(resolve=>requestAnimationFrame(resolve));
  if(generation===videoGeneration){countVideoFrame();renderProgress();}
}
async function measureVideoBandwidth(){
  const rates=[];
  // Measure remaining download capacity with telemetry already running. Reserve 65% of it.
  for(let i=0;i<2;i++){
    if(performance.now()-telemetrySeenAt>250)break;
    const began=performance.now();
    try{const r=await fetch('/api/camera/video/bandwidth-probe',{cache:'no-store',signal:AbortSignal.timeout(2000)});if(!r.ok)break;const bytes=(await r.arrayBuffer()).byteLength;const elapsed=performance.now()-began;if(bytes===65536&&performance.now()-telemetrySeenAt<250)rates.push(bytes*1000/Math.max(1,elapsed));}catch{break;}
  }
  measuredBandwidth=rates.length?Math.min(...rates):0;
  videoByteBudget=measuredBandwidth?Math.max(1024,measuredBandwidth*.35):8192;
}
async function streamVideo(generation){
  await measureVideoBandwidth();if(generation!==videoGeneration)return;
  deliveryFPS=Math.min(3,Number($('#acquisition-preview-fps').value)||15);let adaptedAt=performance.now(),healthySince=performance.now();
  const socket=new WebSocket(`${location.protocol==='https:'?'wss:':'ws:'}//${location.host}/ws/camera/video`);
  videoSocket=socket;socket.binaryType='arraybuffer';let pending=null,decoding=false,lastReceived=-1,withheld=-1,averageBytes=0;
  function acknowledge(sequence){if(performance.now()-telemetrySeenAt>250){withheld=Math.max(withheld,sequence);previewYielding=true;return;}if(socket.readyState===WebSocket.OPEN)socket.send(String(sequence));}
  const priorityTimer=setInterval(()=>{
    const now=performance.now(),ceiling=Math.min(3,Number($('#acquisition-preview-fps').value)||15);
    if(now-adaptedAt>2000){
      const late=now-telemetrySeenAt>250;
      const slow=videoTimes.length>0&&displayFPS<deliveryFPS*.7&&now-videoClock>3000;
      if(late||slow){deliveryFPS=Math.max(.1,deliveryFPS*(late?.5:.75));videoByteBudget=Math.max(1024,videoByteBudget*.5);healthySince=now;}
      else if(now-healthySince>10000){deliveryFPS=Math.min(ceiling,deliveryFPS+.5);healthySince=now;}
      deliveryFPS=Math.max(.1,Math.min(deliveryFPS,averageBytes?videoByteBudget/averageBytes:3));
      adaptedAt=now;if(socket.readyState===WebSocket.OPEN){socket.send(`fps:${deliveryFPS}`);socket.send(`budget:${videoByteBudget}`);}
    }if(generation!==videoGeneration){clearInterval(priorityTimer);return;}if(withheld>=0&&performance.now()-telemetrySeenAt<=250){const sequence=withheld;withheld=-1;previewYielding=false;acknowledge(sequence);renderProgress();}},100);
  async function drain(){
    if(decoding)return;decoding=true;
    try{while(pending&&generation===videoGeneration){const frame=pending;pending=null;try{await displayVideoFrame(frame.blob,generation);}finally{acknowledge(frame.sequence);}}}
    finally{decoding=false;}
  }
  socket.onopen=()=>{videoTransport='WebSocket';socket.send(`fps:${deliveryFPS}`);socket.send(`budget:${videoByteBudget}`);renderProgress();};
  socket.onmessage=event=>{
    if(generation!==videoGeneration){socket.close();return;}
    const data=event.data;if(!(data instanceof ArrayBuffer)||data.byteLength<5)return;
    const sequence=new DataView(data).getUint32(0);lastReceived=sequence;averageBytes=averageBytes?averageBytes*.8+data.byteLength*.2:data.byteLength;
    if(performance.now()-telemetrySeenAt>250){acknowledge(sequence);return;}
    if(document.hidden||$('#imaging').hidden){acknowledge(sequence);videoTimes=[];videoClock=performance.now();displayFPS=0;return;}
    if(pending)acknowledge(pending.sequence);
    pending={sequence,blob:new Blob([data.slice(4)],{type:'image/jpeg'})};drain();
  };
  socket.onclose=()=>{clearInterval(priorityTimer);previewYielding=false;if(generation===videoGeneration){videoSocket=null;videoTransport='HTTP fallback';pollVideo(generation);}};
  socket.onerror=()=>socket.close();
}
async function pollVideo(generation){
  let previous=-1,adaptedAt=performance.now(),frameBytes=32768;
  while(generation===videoGeneration){
    if(document.hidden||$('#imaging').hidden){videoTimes=[];videoClock=performance.now();displayFPS=0;await new Promise(r=>setTimeout(r,250));continue;}
    if(performance.now()-telemetrySeenAt>250){previewYielding=true;await new Promise(r=>setTimeout(r,100));continue;}previewYielding=false;
    const began=performance.now();
    try{const r=await fetch('/api/camera/video/frame',{cache:'no-store'});if(generation!==videoGeneration)return;
      if(r.status===200){const sequence=Number(r.headers.get('X-Frame-Sequence'));if(sequence!==previous){const blob=await r.blob();frameBytes=blob.size;if(generation!==videoGeneration)return;await displayVideoFrame(blob,generation);previous=sequence;}}}
    catch{if(generation===videoGeneration)message('Preview interrupted; retrying…');}
    const elapsed=performance.now()-began;
    if(performance.now()-adaptedAt>2000){const ceiling=Math.min(3,Number($('#acquisition-preview-fps').value)||15);deliveryFPS=Math.max(.1,Math.min(ceiling,800/Math.max(1,elapsed),videoByteBudget/frameBytes));adaptedAt=performance.now();}
    await new Promise(r=>setTimeout(r,Math.max(0,Math.max(1000/deliveryFPS,1000*frameBytes/videoByteBudget)-elapsed)));
  }
}
function renderProgress(){
  const bar=$('#acquisition-progress-bar'),phase=$('#acquisition-phase'),value=$('#acquisition-progress-value'),detail=$('#acquisition-progress-detail');if(!bar)return;
  if(mode==='video'){if(camera?.video?.recording){const now=performance.now();videoTimes=videoTimes.filter(t=>now-t<2000);displayFPS=videoTimes.length/Math.max(.5,Math.min(2,(now-videoClock)/1000));}bar.hidden=true;const v=camera?.video;phase.textContent=v?.recording?(v.finishing?'Saving video':previewYielding?'Streaming · waiting for telemetry':'Streaming'):(v?.error?'Error':v?.frames?'Stopped':'Ready for video');value.textContent=v?.recording&&v.saving?`${v.frames} / ${v.target_frames} frames`:'';detail.textContent=v?`Camera ${(v.fps||0).toFixed(1)} FPS · Preview ${(v.preview_fps||0).toFixed(1)} FPS · Web ${displayFPS.toFixed(1)} FPS / Delivery limit ${deliveryFPS.toFixed(1)} FPS (auto) / Video budget ${(videoByteBudget*8/1e6).toFixed(2)} Mbps / Measured spare ${(measuredBandwidth*8/1e6).toFixed(2)} Mbps${videoTransport?' ('+videoTransport+')':''} · Dropped ${v.dropped_frames??'unavailable'}${v.peak_percent!==undefined?' · Peak '+v.peak_percent.toFixed(1)+'% · Saturated '+v.saturated_percent.toFixed(2)+'%':''}`:'Start video to measure the stream.';return;}
  bar.hidden=false;const p=camera?.acquisition||{phase:'Idle'},name=previewPending&&p.phase==='Complete'?'Loading preview':p.phase;phase.textContent=name;
  if(name==='Exposing'){bar.value=Math.min(99,(p.fraction||0)*100);value.textContent=`${(p.elapsed_s||0).toFixed(1)} / ${p.exposure_s||0} s`;}
  else if(['Readout','Processing','Preparing','Loading preview'].includes(name)){bar.removeAttribute('value');value.textContent=name==='Readout'?`${(p.readout_s||0).toFixed(2)} s`:'';}
  else{bar.value=name==='Complete'?100:0;value.textContent=name==='Complete'?'100%':'';}
  detail.textContent=p.error||((p.readout_s!==undefined?`Readout ${p.readout_s.toFixed(2)} s${p.readout_estimated?' (estimated)':''}`:'')+(name==='Complete'?' · Image ready':''));
}

$('#acquisition-gallery').addEventListener('click',event=>{
  const image=event.target.closest('img'),filename=image?.closest('.acquisition-viewport').dataset.filename;
  if(!filename||gestureMoved)return;clearTimeout(clickTimer);
  clickTimer=setTimeout(async()=>{await selectImage(image);if(activeImage===image&&imageRecord?.filename===filename)showFitsHeader(imageRecord);},300);
});
$('#acquisition-gallery').addEventListener('dblclick',event=>{
  const image=event.target.closest('img'),filename=image?.closest('.acquisition-viewport').dataset.filename;
  if(!filename||gestureMoved)return;clearTimeout(clickTimer);
  viewerChannel.postMessage({type:'select',filename});
  if(viewerWindow&&!viewerWindow.closed){viewerWindow.focus();return;}
  viewerWindow=window.open('/assets/image-viewer.html?image='+encodeURIComponent(filename)+'&link='+encodeURIComponent(viewerLink),'NARIT-ImageViewer-'+viewerLink);
  viewerWindow?.focus();
});

// Cooling calls share the native camera worker with acquisition.
let coolingTab='control', coolingPending=false, coolingReading=false, coolingValue=null, coolingDevice='';
function coolingControls(){
 const ready=camera?.connected?.sdk==='sdk2'&&coolingValue?.supported&&!coolingValue.stale&&!busy&&!camera?.exposing&&!coolingPending;
 for(const id of ['cooling-target','cooling-apply','cooling-on','cooling-off'])$('#'+id).disabled=!ready;
 if(ready){$('#cooling-on').disabled=coolingValue.enabled;$('#cooling-off').disabled=!coolingValue.enabled;}
}
function renderCooling(value){
 coolingValue=value;
 $('#cooling-temperature').textContent=Number.isFinite(value.temperature_c)?value.temperature_c.toFixed(2)+' °C':'— °C';
 $('#cooling-state').textContent=value.state||'Temperature unavailable';
 $('#cooling-enabled').textContent=typeof value.enabled==='boolean'?(value.enabled?'On':'Off'):'—';
 $('#cooling-target-current').textContent=Number.isFinite(value.target_c)?value.target_c.toFixed(2)+' °C':'—';
 if(value.supported){
  const input=$('#cooling-target');input.min=value.minimum_c;input.max=value.maximum_c;
  if(coolingDevice!==deviceKey){input.value=Math.min(value.maximum_c,Math.max(value.minimum_c,Number.isFinite(value.saved_target_c)?value.saved_target_c:Number.isFinite(value.target_c)?Math.round(value.target_c):value.maximum_c)).toFixed(2);coolingDevice=deviceKey;}
  $('#cooling-range').textContent=`Camera range: ${value.minimum_c} to ${value.maximum_c} °C`;
 }else if(!value.stale){coolingDevice='';$('#cooling-range').textContent='';}
 coolingControls();
}
async function pollCooling(){
 if(coolingTab!=='cooling'||document.hidden||$('#imaging').hidden||coolingPending||coolingReading)return;
 coolingReading=true;
 try{renderCooling(await api('/api/camera/cooling'));$('#cooling-message').textContent='';}
 catch(e){$('#cooling-message').textContent=e.message;coolingValue=null;}
 finally{coolingReading=false;coolingControls();}
}
async function changeCooling(body){
 if(coolingPending||busy||camera?.exposing)return;
 coolingPending=true;coolingControls();
 try{renderCooling(await api('/api/camera/cooling',body));$('#cooling-message').textContent='Cooling settings updated';}
 catch(e){$('#cooling-message').textContent=e.message;}
 finally{coolingPending=false;coolingControls();}
}
for(const button of document.querySelectorAll('[data-camera-tab]')){
 button.onclick=()=>{
  coolingTab=button.dataset.cameraTab;
  for(const tab of document.querySelectorAll('[data-camera-tab]')){const active=tab===button;tab.classList.toggle('active',active);tab.setAttribute('aria-selected',String(active));tab.tabIndex=active?0:-1;}
  $('#camera-control-panel').hidden=coolingTab!=='control';$('#camera-cooling-panel').hidden=coolingTab!=='cooling';
  if(coolingTab==='cooling')pollCooling();
 };
 button.onkeydown=e=>{if(!['ArrowLeft','ArrowRight','Home','End'].includes(e.key))return;e.preventDefault();const tabs=[...document.querySelectorAll('[data-camera-tab]')];const next=e.key==='Home'?tabs[0]:e.key==='End'?tabs.at(-1):tabs.find(t=>t!==button);next.focus();next.click();};
}
$('#cooling-apply').onclick=()=>{const input=$('#cooling-target');if(input.value===''||!input.reportValidity())return;changeCooling({target_c:Number(input.value)});};
$('#cooling-on').onclick=()=>{const input=$('#cooling-target');if(input.value===''||!input.reportValidity())return;changeCooling({enabled:true,target_c:Number(input.value)});};
$('#cooling-off').onclick=()=>changeCooling({enabled:false});
document.addEventListener('mount-state',()=>{if(coolingValue&&coolingDevice&&coolingDevice!==deviceKey)renderCooling({supported:false,state:'Camera changed · reading temperature…'});coolingControls();});
$('#cooling-target').addEventListener('change',event=>{if(event.target.value!==''&&event.target.checkValidity())event.target.value=Number(event.target.value).toFixed(2);});
setInterval(pollCooling,200);

function updateSeriesInfo(series){$('#acquisition-series-info').textContent=`Series ${series.name||series.id.slice(0,8)} · ${series.count} image${series.count===1?'':'s'}`;}
function clearSeriesDisplay(){
 const image=$('#acquisition-image'),frame=image.closest('.acquisition-viewport');
 for(const other of document.querySelectorAll('#acquisition-gallery .acquisition-viewport'))if(other!==frame)other.remove();
 image.hidden=true;image.removeAttribute('src');delete frame.dataset.filename;activeImage=image;imageRecord=null;resetZoom();
 $('#acquisition-empty').hidden=false;$('#acquisition-download').hidden=true;showFitsHeader(null);stretch.setEnabled(false);
}
async function restoreSeries(){
 try{
  const series=await api('/api/camera/series/latest');if(imageRecord||camera?.exposing)return;updateSeriesInfo(series);clearSeriesDisplay();
  if(!series.images.length)return;
  const image=$('#acquisition-image'),frame=image.closest('.acquisition-viewport');
  frame.dataset.filename=series.images[0].filename;image.src=series.images[0].metadata.preview_url;image.hidden=false;$('#acquisition-empty').hidden=true;
  for(const record of series.images.slice(1)){
   const item=document.createElement('div');item.className='acquisition-viewport';item.dataset.filename=record.filename;item.tabIndex=0;item.setAttribute('aria-label','Captured image '+record.filename);
   const img=document.createElement('img');img.alt='Captured camera preview';img.loading='lazy';img.fetchPriority='low';img.src=record.metadata.preview_url;item.append(img);$('#acquisition-gallery').append(item);
  }
  await selectImage(image);$('#acquisition-download').hidden=false;
 }catch(error){message('Saved series could not load: '+error.message);}
}
const seriesDialog=document.createElement('dialog');seriesDialog.className='acquisition-series-dialog';
seriesDialog.innerHTML='<form><header><h2>Create New Series</h2><button type="button" class="button quiet" aria-label="Close"><svg viewBox="0 0 24 24" width="20" height="20" aria-hidden="true"><path d="M6 6l12 12M18 6L6 18" fill="none" stroke="currentColor" stroke-width="1.8"/></svg></button></header><label class="field">Series name<input name="series-name" aria-label="Series name" required maxlength="120" autocomplete="off"></label><p role="status"></p><footer><button type="button" class="button quiet" data-cancel>Cancel</button><button type="submit" class="button primary">Save</button></footer></form>';
seriesDialog.setAttribute('aria-label','Create New Series');document.body.append(seriesDialog);
for(const button of seriesDialog.querySelectorAll('button[type=button]'))button.onclick=()=>seriesDialog.close();
$('#acquisition-new-series').onclick=()=>{if(busy||camera?.exposing)return;seriesDialog.querySelector('form').reset();seriesDialog.querySelector('p').textContent='';seriesDialog.showModal();seriesDialog.querySelector('input').focus();};
seriesDialog.querySelector('form').onsubmit=async event=>{
 event.preventDefault();const input=seriesDialog.querySelector('input'),name=input.value.trim();if(!name){input.setCustomValidity('Enter a series name');input.reportValidity();return;}input.setCustomValidity('');
 const save=seriesDialog.querySelector('button[type=submit]');save.disabled=true;
 try{const response=await fetch('/api/camera/series/new',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({name})});const series=await response.json();if(!response.ok)throw Error(response.status===404?'Could not save the series. Please try again.':series.detail||'Could not create series');clearSeriesDisplay();updateSeriesInfo(series);message('New series ready');seriesDialog.close();}catch(error){seriesDialog.querySelector('p').textContent=error.message;}finally{save.disabled=false;}
};
seriesDialog.querySelector('input').oninput=event=>event.target.setCustomValidity('');
restoreSeries();
