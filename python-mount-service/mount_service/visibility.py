"""Three-day geometric horizon forecast, isolated from the live mount actor."""
from datetime import datetime, timedelta, timezone
import threading
import time

from .astronomy import Astronomy


def forecast(payload, site, center=None, cancelled=None):
    astro = Astronomy(**site)
    center = center or datetime.now(timezone.utc)
    start, end = center-timedelta(days=1.5), center+timedelta(days=1.5)
    step = 20 if payload.get('tle') else 600
    samples, events = [], []
    # Each instance/worker owns its mutable PWLib objects.
    satellite = None
    if payload.get('tle'):
        from .moving_objects import load_astro_vendor
        load_astro_vendor()
        from PWLib.Astro import Satellite
        tle = payload['tle']
        satellite = Satellite(tle['name'], tle['line1'], tle['line2'])

    def altitude(at):
        if cancelled and cancelled.is_set():
            raise InterruptedError('Forecast superseded')
        if satellite is not None:
            from PWLib.Astro import Angle, LatLon
            location = LatLon(Angle.FromDegs(site['latitude']),Angle.FromDegs(site['longitude']))
            jd = float(astro.time(at).jd)
            satellite.RefreshPosition(location,site['elevation'],jd)
            return float(satellite.RaDecApparent.ToAltAz(location,jd).Alt.Degs)
        return astro.target(**payload, utc=at)['altitude_deg']

    count = int((end-start).total_seconds()/step)
    previous = None
    for i in range(count+1):
        at = start+timedelta(seconds=i*step)
        alt = altitude(at)
        samples.append([at.timestamp()*1000,alt])
        if previous and (previous[1]>0) != (alt>0):
            left, right = previous[0], at
            rising = alt>0
            while (right-left).total_seconds()>1:
                middle = left+(right-left)/2
                if (altitude(middle)>0) == rising:
                    right=middle
                else:
                    left=middle
            events.append({'kind':'rise' if rising else 'set','utc':right.isoformat(),
                           'timestamp_ms':right.timestamp()*1000})
        previous=(at,alt)
        if i%16==0:
            time.sleep(.001)
    return {'center_ms':center.timestamp()*1000,'start_ms':start.timestamp()*1000,
            'end_ms':end.timestamp()*1000,'samples':samples,'events':events,
            'step_seconds':step,'site':site,'horizon_deg':0,
            'frame':'Geometric altitude · horizon 0° · no atmospheric refraction'}
