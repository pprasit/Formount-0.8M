export function mountStatus(s){
 if(!s.drive?.connected)return {text:'Disabled',state:'disabled'};
 if(!['azimuth','altitude','m3'].every(id=>s.axes?.some(a=>a.id===id&&a.valid)))return {text:'Telemetry unavailable',state:'unknown'};
 const o=s.observatory;
 if(o?.tracking){const t=o.target||{};let type='Sidereal';if(t.tle)type='Satellite';else if(t.orbit)type=t.orbit.perihelion_distance_au!=null?'Comet':'Small body';else if(t.body)type=t.body==='moon'?'Moon':t.body==='sun'?'Sun':'Planet';return {text:`Tracking · ${type}`,state:'tracking'};}
 const event=(s.events||[]).find(e=>e.title==='STOP ALL'||/ · (stop|enable|disable|goto)$/.test(e.title)||e.title==='Combined Az/Alt slew'||e.title==='Drive disconnected');
 if(event&&(event.title==='STOP ALL'||event.title.endsWith(' · stop')))return {text:'Stopped',state:'stopped'};
 const enabled=['azimuth','altitude','m3'].every(id=>s.axes.some(a=>a.id===id&&a.enabled));
 return {text:enabled?'Enabled':'Disabled',state:enabled?'enabled':'disabled'};
}

