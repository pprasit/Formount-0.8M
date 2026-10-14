// Fetch a lossless bounded preview once; stretch locally in a worker.
export class ImageStretch {
  constructor(select, host, onError=console.error) {
    this.select=select;this.onError=onError;this.enabled=true;this.token=0;this.version=0;this.cache=new Map();this.saves=new Map();
    this.tools=document.createElement('div');this.tools.className='image-stretch-sliders';
    this.tools.innerHTML='<label class="field">Black percentile <output>0.00%</output><input data-level="black" aria-label="Black percentile (%)" type="range" min="0" max="100" step="0.01" value="0" disabled></label><label class="field">White percentile <output>100.00%</output><input data-level="white" aria-label="White percentile (%)" type="range" min="0" max="100" step="0.01" value="100" disabled></label>';
    host.append(this.tools);this.black=this.tools.querySelector('[data-level=black]');this.white=this.tools.querySelector('[data-level=white]');
    this.select.addEventListener('change',()=>{if(!this.pixels)return;const name=this.select.value;if(name!=='Manual')this.view={...this.record.presets[name]};else this.view.profile='Manual';this.view.black=Math.min(65534,Math.max(0,this.view.black));this.view.white=Math.min(65535,Math.max(this.view.black+1,this.view.white));this.showLevels();this.render();this.publish();this.save();});
    for(const slider of [this.black,this.white])slider.addEventListener('input',()=>{
      if(!this.pixels)return;
      let low=Number(this.black.value),high=Number(this.white.value);
      if(high<=low){if(slider===this.black)low=Math.max(0,high-.01);else high=Math.min(100,low+.01);}
      this.black.value=low;this.white.value=high;
      const black=Math.min(65534,this.percentile(low)),white=Math.max(black+1,this.percentile(high));
      this.view={...this.view,profile:'Manual',black,white};this.select.value='Manual';this.outputs();this.render();this.publish();
    });
    for(const slider of [this.black,this.white])slider.addEventListener('change',()=>this.save());
  }
  link(channel){
    this.channel=channel;this.pendingLinks=new Map();
    channel.addEventListener('message',event=>{
      const data=event.data;
      if(data?.type==='stretch-ready'&&data.filename===this.record?.filename&&this.worker&&this.pixels)this.publish();
      if(data?.type!=='stretch'||typeof data.filename!=='string')return;
      const v=data.view;if(!v||!['black','white','tone'].every(k=>Number.isFinite(v[k]))||v.black<0||v.white>65535||v.white<=v.black||v.tone<=0||v.tone>30)return;
      clearTimeout(this.saves.get(data.filename));this.saves.delete(data.filename);
      this.pendingLinks.set(data.filename,data);if(this.pendingLinks.size>8)this.pendingLinks.delete(this.pendingLinks.keys().next().value);
      if(data.filename===this.record?.filename&&this.worker&&this.pixels)this.applyLinked(data);
    });
  }
  publish(){
    if(!this.channel||!this.record||!this.pixels)return;
    this.latestLink={type:'stretch',filename:this.record.filename,view:{...this.view},blackPercent:Number(this.black.value),whitePercent:Number(this.white.value)};
    if(this.linkTimer)return;
    const delay=Math.max(0,66-(performance.now()-(this.lastLinkSent||0)));
    this.linkTimer=setTimeout(()=>{this.linkTimer=null;this.lastLinkSent=performance.now();this.channel.postMessage(this.latestLink);},delay);
  }
  applyLinked(data){
    this.view={...data.view};this.select.value=this.view.profile;
    if(Number.isFinite(data.blackPercent)&&Number.isFinite(data.whitePercent)){this.black.value=data.blackPercent;this.white.value=data.whitePercent;this.outputs();}else this.showLevels();
    this.render();this.pendingLinks?.delete(data.filename);
  }
  linkedReady(){const pending=this.pendingLinks?.get(this.record.filename);if(pending)this.applyLinked(pending);this.channel?.postMessage({type:'stretch-ready',filename:this.record.filename});}
  setEnabled(enabled){const previous=this.enabled;this.enabled=enabled;this.select.disabled=!enabled||this.loading||!this.worker||!this.pixels;this.black.disabled=this.white.disabled=!enabled||this.loading||!this.worker||!this.pixels;if(!enabled&&previous){this.version++;this.controller?.abort();clearTimeout(this.timer);clearTimeout(this.detailTimer);}if(enabled&&!previous&&this.pixels)this.render(0);}
  async load(record,image,attempt=0){
    this.loading=true;clearTimeout(this.retryTimer);
    const token=++this.token;this.version++;this.controller?.abort();cancelAnimationFrame(this.timer);this.worker?.terminate();this.worker=null;clearTimeout(this.renderTimer);this.renderTimer=null;clearTimeout(this.linkTimer);this.linkTimer=null;this.record=record;this.image=image;this.pixels=null;this.setEnabled(this.enabled);
    this.select.replaceChildren(...[...Object.keys(record.presets),'Manual'].map(name=>new Option(name,name)));this.view={...record.stretch};this.select.value=this.view.profile;
    try{
      let source=this.cache.get(record.filename);
      if(!source){const r=await fetch('/api/camera/images/'+encodeURIComponent(record.filename)+'/display-levels',{signal:AbortSignal.timeout(12000)});if(!r.ok)throw Error('Could not load stretch settings');source=await r.json();this.cache.set(record.filename,source);if(this.cache.size>8)this.cache.delete(this.cache.keys().next().value);}
      if(token!==this.token)return;this.pixels=source.percentiles;
      const response=await fetch('/api/camera/images/'+encodeURIComponent(record.filename)+'/display-data.png?maximum='+(this.maximum||1280),{signal:AbortSignal.timeout(30000),priority:"low"});
      if(response.status===404){
        const fallback=await fetch('/api/camera/stretch-source/'+encodeURIComponent(record.filename),{signal:AbortSignal.timeout(30000),priority:"low"});
        if(!fallback.ok)throw Error('Could not load local stretch preview');
        const buffer=await fallback.arrayBuffer(),bytes=buffer.byteLength;if(token!==this.token)return;
        const width=Number(fallback.headers.get('X-Preview-Width')),height=Number(fallback.headers.get('X-Preview-Height'));
        this.worker?.terminate();this.worker=new Worker('/assets/image-stretch-worker.js');
        this.worker.onmessage=event=>{const data=event.data;if(token!==this.token||!this.enabled)return;const url=URL.createObjectURL(data.blob),old=this.image.dataset.stretchBlob;this.image.src=url;this.image.dataset.stretchBlob=url;this.image.hidden=false;if(old)URL.revokeObjectURL(old);this.onReady?.({width,height,bytes,maximum:this.maximum||1280,local:true});};
        this.worker.postMessage({raw:buffer,width,height},[buffer]);this.loading=false;this.showLevels();this.setEnabled(this.enabled);this.render();this.linkedReady();return;
      }
      if(!response.ok)throw Error('Could not load local stretch preview');
      const blob=await response.blob(),bitmap=await createImageBitmap(blob,{colorSpaceConversion:'none',premultiplyAlpha:'none'});
      if(token!==this.token){bitmap.close();return;}
      this.worker?.terminate();this.worker=new Worker('/assets/image-stretch-worker.js');
      this.worker.onmessage=event=>{const data=event.data;if(token!==this.token||!this.enabled)return;const url=URL.createObjectURL(data.blob),old=this.image.dataset.stretchBlob;this.image.src=url;this.image.dataset.stretchBlob=url;this.image.hidden=false;if(old)URL.revokeObjectURL(old);this.onReady?.({width:data.width,height:data.height,bytes:blob.size,maximum:this.maximum||1280,local:true});};
      this.worker.postMessage({bitmap},[bitmap]);this.loading=false;
      this.view.black=Math.min(65534,Math.max(0,this.view.black));this.view.white=Math.min(65535,Math.max(this.view.black+1,this.view.white));this.showLevels();this.setEnabled(this.enabled);this.render(0);this.linkedReady();
    }catch(error){
      if(token!==this.token)return;
      this.onError(attempt<2?'Loading stretch controls. Retrying...':error.message);
      if(attempt<2)this.retryTimer=setTimeout(()=>{if(token===this.token)this.load(record,image,attempt+1);},750*(attempt+1));
    }
  }
  percentile(percent){const index=percent*(this.pixels.length-1)/100,low=Math.floor(index),high=Math.min(this.pixels.length-1,Math.ceil(index));return this.pixels[low]+(this.pixels[high]-this.pixels[low])*(index-low);}
  showLevels(){for(const [slider,level] of [[this.black,this.view.black],[this.white,this.view.white]]){let low=0,high=this.pixels.length;while(low<high){const middle=(low+high)>>1;if(this.pixels[middle]<=level)low=middle+1;else high=middle;}slider.value=Math.min(100,low/(this.pixels.length-1)*100).toFixed(2);}this.outputs();}
  outputs(){for(const input of [this.black,this.white])input.parentElement.querySelector('output').value=Number(input.value).toFixed(2)+'%';}
  render(){
    if(!this.pixels||!this.enabled||!this.worker)return;
    this.version++;
    if(this.renderTimer)return;
    const delay=Math.max(0,66-(performance.now()-(this.lastRenderSent||0))),token=this.token;
    this.renderTimer=setTimeout(()=>{
      this.renderTimer=null;if(token!==this.token||!this.enabled)return;
      this.lastRenderSent=performance.now();this.worker?.postMessage({view:this.view,version:this.version});
    },delay);
  }
  save(){
    const filename=this.record.filename,view=Object.fromEntries(['profile','black','white','tone'].map(key=>[key,this.view[key]]));clearTimeout(this.saves.get(filename));
    this.saves.set(filename,setTimeout(async()=>{this.saves.delete(filename);try{const r=await fetch('/api/camera/images/'+encodeURIComponent(filename)+'/stretch',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(view)});if(!r.ok)throw Error('Could not save image stretch');}catch(error){this.onError(error.message);}},450));
  }
}
