const $=s=>document.querySelector(s);
const input=$('#tracking-tolerance'),message=$('#tracking-tolerance-message');
let dirty=false,saving=false,lastSeen=0;
function render(quality){
 const state=quality?.state||'unavailable';
 $('.tracking-quality').dataset.state=state;
 $('#tracking-quality-state').textContent=state.toUpperCase();
 for(const [name,id] of [['azimuth','az'],['altitude','alt']]){
  const value=quality?.error_arcsec_s?.[name];
  $('#tracking-error-'+id).classList.toggle('tracking-error-empty',!Number.isFinite(value));
  $('#tracking-error-'+id).textContent=Number.isFinite(value)?(value>0?'+':'')+value.toFixed(3):(state==='idle'?'Not tracking':'Unavailable');
 }
 if(!dirty&&!saving&&document.activeElement!==input&&Number.isFinite(quality?.tolerance_arcsec_s))input.value=quality.tolerance_arcsec_s;
}
document.addEventListener('mount-state',e=>{lastSeen=performance.now();render(e.detail.observatory?.tracking_quality);});
input.addEventListener('input',()=>{dirty=true;message.textContent='Press Enter or leave this field to save';});
async function save(){
 if(!dirty||saving)return;
 if(!input.reportValidity()||input.value==='')return;
 const value=Number(input.value);if(!Number.isFinite(value)||value<=0)return;
 saving=true;input.disabled=true;message.textContent='Saving…';
 try{
  const response=await fetch('/api/observatory/tracking-tolerance',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({tolerance_arcsec_s:value})});
  if(!response.ok)throw Error('Unable to save tracking tolerance');
  dirty=false;message.textContent='Saved';
 }catch(error){message.textContent=error.message;}finally{saving=false;input.disabled=false;}
}
input.addEventListener('change',save);
input.addEventListener('keydown',e=>{if(e.key==='Enter'){e.preventDefault();void save();}});
setInterval(()=>{if(lastSeen&&performance.now()-lastSeen>2000)render(null);},500);
