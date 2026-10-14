let panel,drag;
function clamp(){const box=panel.getBoundingClientRect();panel.style.left=Math.max(8,Math.min(innerWidth-box.width-8,box.left))+'px';panel.style.top=Math.max(8,Math.min(innerHeight-Math.min(box.height,innerHeight-16)-8,box.top))+'px';}
export function hideFitsHeader(){if(panel)panel.hidden=true;}
export function showFitsHeader(record){
  if(!panel){
    panel=document.createElement('section');panel.className='fits-header-window';panel.setAttribute('role','dialog');panel.setAttribute('aria-modal','false');panel.setAttribute('aria-label','FITS header');
    panel.innerHTML='<header tabindex="0"><div><strong>FITS header</strong><span class="fits-header-filename"></span></div><button type="button" class="button quiet" aria-label="Close FITS header"><svg viewBox="0 0 24 24" aria-hidden="true"><path d="M6 6l12 12M18 6L6 18"/></svg></button></header><div class="fits-header-scroll"><table><thead><tr><th>Keyword</th><th>Value</th><th>Comment</th></tr></thead><tbody></tbody></table></div>';
    document.body.append(panel);panel.querySelector('button').onclick=hideFitsHeader;
    const header=panel.querySelector('header');header.addEventListener('pointerdown',event=>{if(event.target.closest('button')||event.button!==0)return;const box=panel.getBoundingClientRect();drag={id:event.pointerId,x:event.clientX-box.left,y:event.clientY-box.top};header.setPointerCapture(event.pointerId);event.preventDefault();});
    header.addEventListener('pointermove',event=>{if(!drag||drag.id!==event.pointerId)return;panel.style.left=event.clientX-drag.x+'px';panel.style.top=event.clientY-drag.y+'px';clamp();});
    for(const name of ['pointerup','pointercancel','lostpointercapture'])header.addEventListener(name,()=>drag=null);
    header.addEventListener('keydown',event=>{if(event.target!==header)return;const offsets={ArrowLeft:[-16,0],ArrowRight:[16,0],ArrowUp:[0,-16],ArrowDown:[0,16]};if(offsets[event.key]){event.preventDefault();const box=panel.getBoundingClientRect(),[x,y]=offsets[event.key];panel.style.left=box.left+x+'px';panel.style.top=box.top+y+'px';clamp();}if(event.key==='Escape')hideFitsHeader();});
    panel.style.left=Math.max(8,innerWidth-480)+'px';panel.style.top='100px';addEventListener('resize',()=>{if(!panel.hidden)clamp();});
  }
  panel.querySelector('.fits-header-filename').textContent=record.filename;const body=panel.querySelector('tbody');body.replaceChildren();
  for(const card of record.metadata?.fits_header||[]){const row=document.createElement('tr');for(const value of [card.key,card.value,card.comment]){const cell=document.createElement('td');cell.textContent=String(value??'');row.append(cell);}body.append(row);}
  panel.hidden=false;clamp();
}
