"""Build a browser display catalog from the repository's BSC5 and OpenNGC data."""
import csv
import json
from pathlib import Path
root=Path(__file__).resolve().parents[2]
source=root/'Narit-Mount-Lib/Data'
rows=[]
for star in json.loads((source/'BrightStars.json').read_text(encoding='utf-8-sig'))['stars']:
    rows.append(dict(id=star['id'],name=star['name'],category='bright_star',well_known=bool(star.get('bayer')),ra_hours=star['ra'],dec_deg=star['dec'],image_ra_hours=star['ra'],image_dec_deg=star['dec'],magnitude=star['magnitude'],aliases=[f"HR {star['id']}",f"HD {star['hd']}" if star.get('hd') else '',star.get('bayer') or '']))
types={'G':'galaxy','GPair':'galaxy','GTrpl':'galaxy','GGroup':'galaxy','OCl':'open_cluster','GCl':'globular_cluster','*':'bright_star','**':'bright_star','*Ass':'open_cluster'}
with (source/'OpenNgcCatalog.csv').open(encoding='utf-8-sig',newline='') as f:
    for obj in csv.DictReader(f,delimiter=';'):
        if obj['Type'] in ('NonEx','Dup') or not obj['RA'] or not obj['Dec']:continue
        h,m,sec=map(float,obj['RA'].split(':'))
        d,dm,ds=map(float,obj['Dec'].lstrip('+-').split(':'))
        ra=h+m/60+sec/3600;dec=(d+dm/60+ds/3600)*(-1 if obj['Dec'].startswith('-') else 1)
        mag=obj['V-Mag'] or obj['B-Mag']
        aliases=[obj['Name'],obj['Identifiers'],obj['Common names']]
        if obj['M']:aliases.append('M'+obj['M'])
        for key in ('NGC','IC'):
            if obj[key]:aliases.extend(key+v.strip() for v in obj[key].split(','))
        name=('M'+obj['M']+' · ' if obj['M'] else '')+obj['Name']
        rows.append(dict(id=obj['Name'],name=name,category=types.get(obj['Type'],'nebula'),well_known=bool(obj['M'] or obj['Common names']),ra_hours=ra,dec_deg=dec,image_ra_hours=ra,image_dec_deg=dec,magnitude=float(mag) if mag else None,aliases=aliases,major_arcmin=float(obj['MajAx']) if obj['MajAx'] else None))
rows.extend(dict(id=b,name=b.title(),category='planet',well_known=True,body=b,magnitude=None,aliases=[]) for b in ('moon','mercury','venus','mars','jupiter','saturn','uranus','neptune'))
output=root/'python-mount-service/mount_service/web/astronomy-extended.json'
output.write_text(json.dumps(dict(objects=rows,source='BSC5 · OpenNGC',attribution='OpenNGC: Mattia Verga et al., CC BY-SA 4.0; repository catalog snapshot'),ensure_ascii=False,separators=(',',':')),encoding='utf-8')
print(len(rows),'objects')
