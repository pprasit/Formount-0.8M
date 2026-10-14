import {updateInterestingRows} from './interesting-objects.js';
import {notify} from './notifications.js';
const dialog=document.createElement('dialog');dialog.id='interesting-ai-dialog';dialog.className='dialog interesting-ai-dialog';
dialog.innerHTML=`<form><header class="dialog-title"><h2>Build List by AI</h2><button type="button" class="button quiet" data-close aria-label="Close">×</button></header><p>NARIT AI will choose interesting objects visible tonight from this observatory, prioritising dusk to 23:00 ICT, then beautiful, well-known, bright and high-altitude targets. Later-night visibility receives a lower score.</p><label class="field">Number of new objects<input name="count" type="number" min="1" max="100" step="1" value="10" required></label><fieldset><legend>Include object types</legend><div class="ai-object-types"></div></fieldset><fieldset class="ai-satellites" hidden><legend>Satellite catalogs</legend><div class="ai-satellite-types"></div><small>Visible passes above 10° during twilight/night; approximate sunlight screening.</small></fieldset><p class="ai-build-note">Existing objects remain in your list. Duplicates are skipped, and new AI selections receive an AI label.</p><div class="ai-progress" hidden><div class="ai-progress-heading"><span>Build progress</span><strong>0%</strong></div><progress max="100" value="0" aria-label="AI list build progress"></progress><small>Percentage represents completed workflow stages; AI response time varies.</small></div><p class="ai-build-status" role="status" aria-live="polite"></p><div class="dialog-actions"><button type="button" class="button quiet" data-close>Cancel</button><button type="submit" class="button primary">Build list</button></div></form>`;
document.body.append(dialog);const form=dialog.querySelector('form'),status=dialog.querySelector('.ai-build-status');let busy=false;const progressPanel=dialog.querySelector('.ai-progress'),progressBar=dialog.querySelector('progress'),progressValue=dialog.querySelector('.ai-progress strong');function showProgress(percent,message){progressPanel.hidden=false;progressBar.hidden=false;progressBar.value=percent;progressValue.textContent=percent+'%';status.textContent=message;}
for(const [id,name] of Object.entries({planet:'Planets & Moon',bright_star:'Bright Stars',galaxy:'Galaxies',open_cluster:'Open Clusters',globular_cluster:'Globular Clusters',nebula:'Nebulae',satellite:'Satellites',asteroid:'Asteroids',comet:'Comets'})){const label=document.createElement('label'),check=document.createElement('input');check.type='checkbox';check.name='categories';check.value=id;check.checked=['planet','bright_star','galaxy'].includes(id);label.append(check,document.createTextNode(name));dialog.querySelector('.ai-object-types').append(label);if(id==='satellite')check.onchange=()=>dialog.querySelector('.ai-satellites').hidden=!check.checked;}
let pollTimer=null,lastAppliedJob=null,observedJob=null,polling=false,statusReady=false,statusFailures=0;
const submit=form.querySelector('[type=submit]'),hide=form.querySelector('.dialog-actions [data-close]');
function setBusy(value){busy=value;for(const el of form.elements)el.disabled=value&&!el.hasAttribute('data-close');submit.textContent=value?'Building…':'Build list';hide.textContent=value?'Hide window':'Cancel';}
function applyJob(job){
 if(job.state==='idle'||(!job.active&&job.id!==observedJob)){setBusy(false);progressPanel.hidden=true;progressBar.value=0;progressValue.textContent='0%';status.textContent='Ready to build a new list.';return;}
 if(job.active)observedJob=job.id;
 setBusy(job.active);showProgress(job.percent||0,job.message);
 if(job.state==='error'){progressBar.hidden=true;progressValue.textContent='Failed';status.textContent='Build failed — '+job.message;submit.textContent='Try again';return;}
 progressBar.hidden=false;
 if(job.active){schedulePoll();return;}
 if(job.state==='completed'&&job.id!==lastAppliedJob){lastAppliedJob=job.id;updateInterestingRows(job.result.objects);status.textContent=`Completed — added ${job.result.added} objects to your list.`;notify(status.textContent);}
}
function schedulePoll(){clearTimeout(pollTimer);pollTimer=setTimeout(checkJob,1000);}
async function checkJob(){
 if(polling)return;pollTimer=null;polling=true;
 try{
  const r=await fetch('/api/astronomy/interesting-ai/status',{cache:'no-store',signal:AbortSignal.timeout(10000)});
  if(r.status===404){statusReady=false;clearTimeout(pollTimer);pollTimer=null;setBusy(false);submit.disabled=true;status.textContent='AI build status API is unavailable. The server needs to load the updated service before you can build a list.';return;}
  if(!r.ok)throw Error('Unable to read build status.');
  const job=await r.json();if(!['idle','running','completed','error'].includes(job.state))throw Error('Invalid build status response.');
  statusReady=true;statusFailures=0;applyJob(job);
 }catch(e){
  statusReady=false;statusFailures++;submit.disabled=true;
  status.textContent='Cannot confirm build status. No completion has been confirmed. Reconnecting…';
  if(statusFailures<5)schedulePoll();else{setBusy(false);submit.disabled=true;status.textContent='Cannot confirm build status after several attempts. Reopen this window to check again before starting another build.';}
 }finally{polling=false;}
}
for(const close of dialog.querySelectorAll('[data-close]'))close.onclick=()=>dialog.close();
document.querySelector('#sky-build-ai').onclick=async()=>{if(!busy){observedJob=null;status.textContent='Checking service availability…';progressPanel.hidden=true;}submit.disabled=true;statusFailures=0;dialog.showModal();try{const r=await fetch('/api/astronomy/interesting-ai/options');if(!r.ok)throw Error('Unable to load AI options.');const d=await r.json();if(!dialog.querySelector('.ai-satellite-types').children.length)for(const id of d.satellite_catalogs){const label=document.createElement('label'),check=document.createElement('input');check.type='checkbox';check.name='satellite_catalogs';check.value=id;check.checked=id==='NARIT-VISUAL';label.append(check,document.createTextNode(id.replace('NARIT-','')));dialog.querySelector('.ai-satellite-types').append(label);}if(!d.configured)status.textContent='NARIT AI connection is not configured on this server yet.';await checkJob();}catch(e){status.textContent=e.message;}};
form.onsubmit=async e=>{e.preventDefault();if(busy||!statusReady||!form.reportValidity())return;const values=new FormData(form),categories=values.getAll('categories'),satellites=values.getAll('satellite_catalogs');if(!categories.length){status.textContent='Select at least one object type.';return;}if(categories.includes('satellite')&&!satellites.length){status.textContent='Select at least one satellite catalog.';return;}setBusy(true);showProgress(0,'Starting…');try{const r=await fetch('/api/astronomy/interesting-ai/build',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({count:Number(values.get('count')),categories,satellite_catalogs:satellites})});const d=await r.json();if(!r.ok)throw Error(d.detail||'Unable to start the build.');if(d.objects){updateInterestingRows(d.objects);showProgress(100,`Completed — added ${d.added} objects to your list.`);setBusy(false);}else applyJob(d);}catch(e){status.textContent=e.message;await checkJob();if(!pollTimer)setBusy(false);}};
document.addEventListener('visibilitychange',()=>{if(!document.hidden)void checkJob();});
void checkJob();
