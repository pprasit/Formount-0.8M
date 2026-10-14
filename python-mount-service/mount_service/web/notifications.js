let timer;
export function notify(message, error = false, duration = 5000) {
  // Also cover replies from a backend that is still finishing a live session.
  if (/^Elmo rejected\s/i.test(message)) {
    const reason = message.slice(message.indexOf(':') + 1).replace(/\s*\(EC \d+\)/g, '').trim();
    message = `TCS rejected this movement: ${reason}`;
  }
  const toast = document.querySelector('#toast');
  // A modal dialog occupies the browser's top layer. Put notifications
  // there too so save failures remain visible while the dialog is open.
  toast.setAttribute('popover', 'manual');
  toast.setAttribute('role', error ? 'alert' : 'status');
  toast.textContent = message;
  toast.classList.toggle('error', error);
  if (toast.showPopover && !toast.matches(':popover-open')) toast.showPopover();
  toast.classList.add('visible');
  clearTimeout(timer);
  timer = setTimeout(() => {
    toast.classList.remove('visible');
    if (toast.hidePopover && toast.matches(':popover-open')) toast.hidePopover();
  }, duration);
  window.engineeringAudit?.('ui.notification', {
    level: error ? 'error' : 'info', message
  });
}
