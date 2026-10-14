"""Validated obstruction polygons in fixed local azimuth/altitude coordinates."""
import math

def unwrap(vertices):
    base=vertices[0][0]
    return [[base+(az-base+180)%360-180,alt] for az,alt in vertices]

def validate(rows):
    if not isinstance(rows,list) or len(rows)>30:raise ValueError('Use at most 30 blind spots.')
    clean=[]
    for row in rows:
        if not isinstance(row,dict):raise ValueError('Invalid blind spot.')
        name=str(row.get('name','')).strip()[:80] or 'Obstruction'
        raw=row.get('vertices')
        if not isinstance(raw,list) or not 3<=len(raw)<=128:raise ValueError('Each polygon needs 3–128 corners.')
        vertices=[]
        for p in raw:
            if not isinstance(p,list) or len(p)!=2 or any(type(v) not in (int,float) or not math.isfinite(v) for v in p):raise ValueError('Invalid polygon coordinates.')
            az,alt=p
            if not 0<=az<360 or not 0<=alt<=90:raise ValueError('Draw above the horizon (altitude 0–90°).')
            vertices.append([float(az),float(alt)])
        plane=unwrap(vertices)
        if len(set(map(tuple,vertices)))<3 or max(p[0] for p in plane)-min(p[0] for p in plane)>=180:raise ValueError('Use a smaller polygon with at least three distinct corners.')
        def orient(a,b,c):return (b[0]-a[0])*(c[1]-a[1])-(b[1]-a[1])*(c[0]-a[0])
        for i in range(len(plane)):
            a,b=plane[i],plane[(i+1)%len(plane)]
            for j in range(i+1,len(plane)):
                if j==i+1 or (i==0 and j==len(plane)-1):continue
                c,d=plane[j],plane[(j+1)%len(plane)]
                if orient(a,b,c)*orient(a,b,d)<0 and orient(c,d,a)*orient(c,d,b)<0:raise ValueError('Polygon edges cross. Enter the corners in perimeter order.')
        area=sum(plane[i][0]*plane[(i+1)%len(plane)][1]-plane[(i+1)%len(plane)][0]*plane[i][1] for i in range(len(plane)))
        if abs(area)<1e-6:raise ValueError('The polygon has no area.')
        clean.append({'name':name,'vertices':vertices})
    return clean

def contains(polygons,az,alt):
    for row in polygons:
        points=unwrap(row['vertices']);base=points[0][0];x=base+(az-base+180)%360-180;y=alt;inside=False
        for i,(a,b) in enumerate(points):
            c,d=points[(i+1)%len(points)]
            cross=(x-a)*(d-b)-(y-b)*(c-a)
            if abs(cross)<1e-8 and min(a,c)-1e-8<=x<=max(a,c)+1e-8 and min(b,d)-1e-8<=y<=max(b,d)+1e-8:return True
            if (b>y)!=(d>y) and x<(c-a)*(y-b)/(d-b)+a:inside=not inside
        if inside:return True
    return False
