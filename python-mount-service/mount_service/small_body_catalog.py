"""Bounded real JPL SBDB catalogs using the existing small-body targeting model."""
import json
import math
import threading
import time
from datetime import datetime, timezone
from urllib.parse import urlencode
from urllib.request import Request, urlopen
from pathlib import Path
from .astronomy import ROOT
from .moving_objects import small_body

CATEGORIES={'asteroid':{'kind':'a','label':'Numbered asteroids'},'comet':{'kind':'c','label':'Numbered periodic comets'}}
FIELDS='spkid,full_name,epoch,e,a,q,i,om,w,ma,tp,H,diameter'
_lock=threading.Lock()
_cache={}
LIMIT=500


def parse_catalog(raw, category):
    if category not in CATEGORIES:raise ValueError('Unknown small-body category')
    rows=[];seen=set()
    for values in raw.get('data',[]):
        record=dict(zip(raw['fields'],values))
        try:
            name=str(record['full_name']).strip(); identifier=str(record['spkid'])
            e=float(record['e'])
            if not 0<=e<1 or identifier in seen:continue
            orbit={'name':name,'eccentricity':e,'inclination_deg':float(record['i']),
                   'ascending_node_deg':float(record['om'])%360,'arg_perihelion_deg':float(record['w'])%360}
            if category=='asteroid':orbit.update(epoch_jd=float(record['epoch']),semi_major_axis_au=float(record['a']),mean_anomaly_deg=float(record['ma'])%360)
            else:orbit.update(perihelion_epoch_jd=float(record['tp']),perihelion_distance_au=float(record['q']))
            if not all(math.isfinite(v) for v in orbit.values() if isinstance(v,float)):continue
            if not 0<=orbit['inclination_deg']<=180 or orbit.get('semi_major_axis_au',orbit.get('perihelion_distance_au'))<=0:continue
            epoch=orbit.get('epoch_jd',orbit.get('perihelion_epoch_jd'))
            if not 2000000<=epoch<=3000000:continue
            rows.append({'id':identifier,'name':name,'orbit':orbit,'epoch_jd':float(record['epoch']),
                         'absolute_magnitude':record.get('H'),'diameter_km':record.get('diameter')})
            seen.add(identifier)
        except (KeyError,ValueError,TypeError):continue
    if not rows:raise ValueError('No supported orbital elements in JPL catalog')
    return rows


def catalog(category):
    if category not in CATEGORIES:raise ValueError('Unknown small-body category')
    with _lock:
        cached=_cache.get(category)
        if cached and time.monotonic()-cached[0]<3600:return cached[1]
        path=ROOT/'python-mount-service/runtime'/f'jpl-{category}-catalog.json'
        saved=None
        try:
            saved=json.loads(path.read_text(encoding='utf-8'))
            if time.time()-saved['fetched_unix']<86400:
                _cache[category]=(time.monotonic(),saved);return saved
        except (OSError,ValueError,KeyError):pass
        parameters={'fields':FIELDS,'sb-kind':CATEGORIES[category]['kind'],'sb-ns':'n','sb-xfrag':'1',
                    'sort':'spkid','limit':LIMIT,'full-prec':'true','sb-cdata':json.dumps({'AND':['e|LT|1']})}
        try:
            request=Request('https://ssd-api.jpl.nasa.gov/sbdb_query.api?'+urlencode(parameters),headers={'User-Agent':'NARIT-MountController/1.0'})
            with urlopen(request,timeout=30) as response:raw=response.read(2*1024*1024+1)
            if len(raw)>2*1024*1024:raise ValueError('JPL response exceeds 2 MB')
            result={'objects':parse_catalog(json.loads(raw),category),'source':'NASA/JPL SBDB',
                    'source_url':'https://ssd-api.jpl.nasa.gov/doc/sbdb_query.html',
                    'fetched_utc':datetime.now(timezone.utc).isoformat(),'fetched_unix':time.time(),
                    'category':category,'label':CATEGORIES[category]['label'],'limit':LIMIT,'stale':False}
            path.parent.mkdir(parents=True,exist_ok=True)
            temporary=path.with_suffix('.tmp');temporary.write_text(json.dumps(result),encoding='utf-8');temporary.replace(path)
        except Exception:
            if not saved:raise
            result={**saved,'stale':True}
        _cache[category]=(time.monotonic(),result)
        return result


def sky_frame(astro, category):
    import astropy.units as u
    from astropy.coordinates import SkyCoord
    result=catalog(category)
    t=astro.time();coordinates=[small_body(row['orbit'],float(t.jd)) for row in result['objects']]
    sky=SkyCoord(ra=[p[0] for p in coordinates]*u.hourangle,dec=[p[1] for p in coordinates]*u.deg,frame='icrs')
    horizontal=sky.transform_to(astro.frame(t))
    objects=[{**row,'azimuth_deg':float(az),'altitude_deg':float(alt)} for row,az,alt in zip(result['objects'],horizontal.az.deg,horizontal.alt.deg)]
    return {**result,'objects':objects,'utc':t.utc.isot+'Z','site':dict(astro.site),
            'calculation_model':'Approximate Kepler orbit · no perturbations or light-time corrections'}
