import {notify} from './notifications.js';

let dialog;
let session = 0;
export async function openZeroPosition(axis) {
  if (!dialog) {
    dialog = document.createElement('dialog');
    dialog.className = 'dialog zero-position-dialog';
    dialog.setAttribute('aria-labelledby', 'zero-position-title');
    dialog.innerHTML = `<div class="dialog-title"><div><small>ABSOLUTE ENCODER · DRIVE FLASH</small><h2 id="zero-position-title">Set zero position</h2></div><button type="button" class="zero-close" aria-label="Close">×</button></div><p class="dialog-copy">Use this physical position as the axis zero reference.</p><div class="zero-reference"><span>Axis</span><strong class="zero-axis"></strong><span>Current position</span><strong class="zero-current"></strong></div><p class="notice">This changes the encoder reference and saves it to drive Flash. All four motors on this drive must be disabled. Flash also saves other pending drive settings.</p><p class="zero-calibration" hidden>Azimuth / altitude zero changes the pointing reference. Recheck travel limits and calibrate pointing before tracking.</p><p class="zero-message" role="status"></p><div class="dialog-actions"><button type="button" class="zero-cancel">Cancel</button><button type="button" class="primary-button zero-confirm">Set Zero &amp; Save Flash</button></div>`;
    document.body.append(dialog);
  }
  if (dialog.open) return;
  const currentSession = ++session;
  dialog.querySelectorAll('button').forEach(button => button.disabled = false);
  const message = dialog.querySelector('.zero-message');
  const confirm = dialog.querySelector('.zero-confirm');
  let busy = false;
  const close = () => { if (!busy) dialog.close(); };
  dialog.querySelector('.zero-close').onclick = close;
  dialog.querySelector('.zero-cancel').onclick = close;
  dialog.oncancel = event => { if (busy) event.preventDefault(); };
  dialog.querySelector('.zero-axis').textContent = axis.name;
  dialog.querySelector('.zero-current').textContent = `${axis.position_deg?.toFixed(4) ?? '—'}°`;
  dialog.querySelector('.zero-calibration').hidden = !['azimuth', 'altitude'].includes(axis.id);
  confirm.disabled = true;
  message.textContent = 'Checking the drive and encoder…';
  dialog.showModal();
  let expected;
  try {
    const response = await fetch(`/api/axes/${axis.id}/zero-position`);
    const data = await response.json();
    if (!response.ok) throw Error(data.detail || 'Cannot read the encoder');
    if (!dialog.open || currentSession !== session) return;
    expected = data;
    message.textContent = `Drive ${data.target.drive} · AX${data.target.channel} · absolute encoder verified`;
    confirm.disabled = false;
  } catch (error) { if (currentSession === session) message.textContent = error.message; }
  confirm.onclick = async () => {
    if (!expected || busy) return;
    busy = true;
    dialog.querySelectorAll('button').forEach(button => button.disabled = true);
    message.textContent = 'Setting zero and verifying drive Flash…';
    try {
      const response = await fetch(`/api/axes/${axis.id}/zero-position`, {method:'POST', headers:{'Content-Type':'application/json'}, body:JSON.stringify(expected)});
      const data = await response.json();
      if (!response.ok) throw Error(data.detail || 'Zero position could not be saved');
      if (!data.saved_to_flash) throw Error('Drive Flash was not verified');
      dialog.close();
      notify(`Saved successfully · ${axis.name} zero position saved to drive Flash`, false, 8000);
    } catch (error) {
      message.textContent = error.message;
      notify(`Zero position save failed · ${error.message}`, true, 10000);
    } finally {
      busy = false;
      dialog.querySelector('.zero-close').disabled = false;
      dialog.querySelector('.zero-cancel').disabled = false;
      // An unsuccessful transaction must be inspected again before retrying.
    }
  };
}
