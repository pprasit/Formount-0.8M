"""NARIT catalogs and worker-owned accelerated Vallado SGP4 propagation."""
import os
import re
import time
import math
import threading
from datetime import datetime, timezone
from urllib.request import Request, urlopen
from .astronomy import ROOT

CATEGORIES = {'NARIT-'+v:v for v in ('ACTIVE','VISUAL','STATIONS','STARLINK','GPS-OPS','CUBESAT')}
_cache = {}
_lock = threading.Lock()

def catalog(category):
    if category not in CATEGORIES:
        raise ValueError('Unknown satellite category')
    with _lock:
        cached = _cache.get(category)
        if cached and time.monotonic()-cached[0] < 3600:
            return cached[1]
        token = os.environ.get('NARIT_TLE_TOKEN')
        if not token:
            source = (ROOT/'Narit-Mount-Lib/Engine/Utility/NaritTLEService.cs').read_text(encoding='utf-8-sig')
            match = re.search(r'API_TOKEN\s*=\s*"([^"]+)"', source)
            token = match.group(1) if match else None
        if not token:
            raise ValueError('Configure NARIT_TLE_TOKEN on the server')
        request = Request('https://engineer.narit.or.th/api/tle.php/content?t='+CATEGORIES[category], headers={'Authorization':'Bearer '+token,'User-Agent':'NARIT-MountController/1.0'})
        with urlopen(request, timeout=60) as response:
            raw = response.read(5*1024*1024+1)
        if len(raw)>5*1024*1024:
            raise ValueError('Catalog exceeds 5 MB')
        lines = [s.rstrip() for s in raw.decode('utf-8-sig').splitlines() if s.strip()]
        rows=[]
        for i,line in enumerate(lines):
            if line.startswith('1 ') and i+1<len(lines) and lines[i+1].startswith('2 '):
                name = lines[i-1].removeprefix('0 ') if i and not lines[i-1].startswith('2 ') else 'Satellite '+line[2:7]
                rows.append(dict(name=name, line1=line,line2=lines[i+1],id=line[2:7].strip()))
        if not rows:
            raise ValueError('NARIT returned no TLE records')
        _cache[category]=(time.monotonic(),rows)
        return rows

class SatelliteFrame:
    def __init__(self, rows, site):
        import numpy as np
        import astropy.units as u
        from astropy.coordinates import EarthLocation
        from sgp4.api import Satrec, SatrecArray
        self.rows=rows
        self.array=SatrecArray([Satrec.twoline2rv(row['line1'],row['line2']) for row in rows])
        self.location=EarthLocation.from_geodetic(site['longitude']*u.deg,site['latitude']*u.deg,site['elevation']*u.m)
        lat,lon=math.radians(site['latitude']),math.radians(site['longitude'])
        self.rotation=np.array([[-math.sin(lon),math.cos(lon),0],[-math.sin(lat)*math.cos(lon),-math.sin(lat)*math.sin(lon),math.cos(lat)],[math.cos(lat)*math.cos(lon),math.cos(lat)*math.sin(lon),math.sin(lat)]])

    def positions(self, times):
        import numpy as np
        import astropy.units as u
        from astropy.coordinates import TEME, ITRS, CartesianRepresentation
        error,positions,_=self.array.sgp4(np.atleast_1d(times.jd1),np.atleast_1d(times.jd2))
        # TEME -> Earth-fixed; subtract the observer before rotating to ENU.
        # This avoids geocentric aberration being applied to a nearby satellite.
        xyz=TEME(CartesianRepresentation(positions.transpose(2,0,1)*u.km),obstime=times).transform_to(ITRS(obstime=times)).cartesian.xyz.to_value(u.km)
        observer=self.location.get_itrs(obstime=times).cartesian.xyz.to_value(u.km)
        relative=xyz-observer[:,None,:]
        enu=np.einsum('ij,jst->ist',self.rotation,relative)
        az=np.degrees(np.arctan2(enu[0],enu[1]))%360
        alt=np.degrees(np.arctan2(enu[2],np.hypot(enu[0],enu[1])))
        return error,az,alt

    def sample(self):
        import numpy as np
        from astropy.time import Time
        started=time.perf_counter();cpu=time.thread_time()
        now=datetime.now(timezone.utc)
        error,az,alt=self.positions(Time([now]))
        objects=[]
        for i,row in enumerate(self.rows):
            if error[i,0] or not np.isfinite(az[i,0]+alt[i,0]):continue
            objects.append({'id':row.get('id',row['line1'][2:7].strip()),'name':row['name'],'azimuth_deg':float(az[i,0]),'altitude_deg':float(alt[i,0])})
        return {'utc':now.isoformat(),'objects':objects,'calculation_ms':(time.perf_counter()-started)*1000,'cpu_ms':(time.thread_time()-cpu)*1000,'target_interval_ms':1000,'total':len(self.rows),'source':'Vallado SGP4 · topocentric TEME / ITRS / ENU'}

def trajectory(row, site):
    import numpy as np
    from astropy.time import Time
    from sgp4.api import Satrec
    satellite = Satrec.twoline2rv(row['line1'], row['line2'])
    period = 2 * math.pi / satellite.no_kozai * 60
    if not math.isfinite(period) or period <= 0:
        raise ValueError('Invalid satellite orbital period')
    frame = SatelliteFrame([row], site)
    # Sample fast orbits more closely; search for the end of the current/next pass.
    step = min(30.0, max(2.0, period / 360))
    started = time.time()
    search_limit = 86400.0
    end = None
    points = []
    setting = None
    sec = 0.0
    while sec <= (end if end is not None else search_limit):
        limit = end if end is not None else search_limit
        seconds = np.arange(sec, min(sec + step * 120, limit + step / 2), step)
        seconds = seconds[seconds <= limit]
        if not len(seconds):
            break
        error, az, alt = frame.positions(Time(started + seconds, format='unix'))
        for i, offset in enumerate(seconds):
            if end is not None and offset > end:
                break
            if error[0, i] or not np.isfinite(az[0, i] + alt[0, i]):
                if not points:
                    raise ValueError('Satellite propagation failed')
                return {'points': points, 'duration_hours': points[-1]['seconds']/3600, 'closed': False}
            p = {'azimuth_deg': float(az[0, i]), 'altitude_deg': float(alt[0, i]), 'seconds': float(offset)}
            if setting is None and points and points[-1]['altitude_deg'] >= 0 > p['altitude_deg']:
                previous = points[-1]
                fraction = previous['altitude_deg'] / (previous['altitude_deg'] - p['altitude_deg'])
                setting = previous['seconds'] + fraction * (offset - previous['seconds'])
                delta = (p['azimuth_deg'] - previous['azimuth_deg'] + 180) % 360 - 180
                points.append({'azimuth_deg': (previous['azimuth_deg'] + fraction * delta) % 360,
                               'altitude_deg': 0.0, 'seconds': float(setting)})
                end = setting + 2 * period
            points.append(p)
        sec = float(seconds[-1]) + step
    if not points:
        raise ValueError('Satellite propagation failed')
    # Include the exact endpoint, even when the period is not a multiple of the step.
    if end is not None and points[-1]['seconds'] < end:
        error, az, alt = frame.positions(Time([started + end], format='unix'))
        if not error[0, 0] and np.isfinite(az[0, 0] + alt[0, 0]):
            points.append({'azimuth_deg': float(az[0, 0]), 'altitude_deg': float(alt[0, 0]), 'seconds': end})
    return {'points': points, 'duration_hours': points[-1]['seconds']/3600, 'closed': False,
            'orbital_period_seconds': period, 'horizon_seconds': setting}
