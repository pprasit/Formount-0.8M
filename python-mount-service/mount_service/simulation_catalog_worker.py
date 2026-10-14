"""Generate catalog star positions with the existing C# PWLib, in an isolated process."""
import json
import math
import os
import sys
from pathlib import Path
from .pointing import load_vendor


def catalog_stars(payload):
    catalog = Path(os.environ.get('UCAC4_PATH', str(Path.home()/'Documents/Kepler/UC4')))
    if not (catalog/'Index.UC4').is_file():
        raise ValueError('Simulation requires UCAC4 catalog; set UCAC4_PATH to its Index.UC4 folder')
    if catalog.name.upper() != 'UC4':
        raise ValueError('PWLib FakeStarField requires a UC4 folder inside its catalog directory')
    os.environ['UCAC4_PATH'] = str(catalog)
    load_vendor()
    from PWLib import FakeStarField
    field = FakeStarField()
    field.CatalogDir = str(catalog.parent)
    field.XPixels, field.YPixels = payload['width'], payload['height']
    field.ArcsecPerPixel = payload['pixel_scale']
    field.CenterRaRadsNative = math.radians(payload['ra_hours']*15)
    field.CenterDecRadsNative = math.radians(payload['dec_deg'])
    field.RotationDegs = payload.get('rotation_deg', 0)
    # Catalog J2000 geometry, matching the solver's output reference frame.
    field.Epoch = 2000
    field.UseApparentCoordinateFrame = False
    field.ShowBscStars, field.ShowUcac4Stars = False, True
    magnitude_limit=int(payload.get('magnitude_limit',16))
    max_stars=int(payload.get('max_stars',5000))
    if not 8<=magnitude_limit<=18 or not 1<=max_stars<=15000:
        raise ValueError('Invalid simulation catalog limits')
    field.MagnitudeLimit = magnitude_limit
    stars = field.Create()
    if field.Uc4LoadFailedMessage:
        raise ValueError(str(field.Uc4LoadFailedMessage))
    rows = sorted(([float(s.X), float(s.Y), float(s.Mag)/1000] for s in stars), key=lambda s:s[2])[:max_stars]
    if not rows:
        raise ValueError('No UCAC4 stars in the simulated field; check telescope field of view')
    return dict(stars=rows, catalog='UCAC4', catalog_path=str(catalog))


if __name__ == '__main__':
    try: print(json.dumps(dict(success=True, **catalog_stars(json.loads(sys.stdin.read())))))
    except Exception as exc: print(json.dumps(dict(success=False, error=str(exc))))
