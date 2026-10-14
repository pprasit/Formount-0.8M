"""PWLib solver in an isolated subprocess: timeout/native faults cannot kill API."""
import json
import math
import os
import sys
import time

from .pointing import load_vendor


def solve(payload):
    from pathlib import Path
    catalog=os.environ.get('UCAC4_PATH')
    if not catalog:
        default=Path.home()/'Documents/Kepler/UC4'
        if (default/'Index.UC4').is_file():catalog=str(default)
    if not catalog or not (Path(catalog)/'Index.UC4').is_file():
        raise ValueError('UCAC4 catalog not found; set UCAC4_PATH to the folder containing Index.UC4')
    os.environ['UCAC4_PATH']=catalog
    target=load_vendor()
    import clr
    from System.Reflection import Assembly
    from System import Activator,Array,Object
    assembly=Assembly.LoadFrom(str(target/'PWLib.dll'))
    kind=assembly.GetType('PWLib.BackgroundPlateSolver',True)
    solver=Activator.CreateInstance(kind,Array[Object]([payload['fits']]))
    solver.CenterRa2000GuessRads=math.radians(payload['ra_hours']*15)
    solver.CenterDec2000GuessRads=math.radians(payload['dec_deg'])
    solver.ArcsecPerPixelGuess=payload['pixel_scale']
    solver.UseStartingCoordinates=True
    solver.BeginSolve()
    deadline=time.monotonic()+45
    while not solver.IsFinished:
        if time.monotonic()>deadline: raise ValueError('Plate solve timeout')
        time.sleep(.1)
    if not solver.FoundMatch:
        if payload.get('inspect_only'):return {**star_positions(solver),'success':False,'error':str(solver.Error)}
        raise ValueError(f'No plate match: {solver.Error}')
    return {**star_positions(solver),'success':True,'ra_hours':float(solver.CenterRaRad2000)*12/math.pi,
            'dec_deg':math.degrees(float(solver.CenterDecRad2000)), 'pixel_scale':float(solver.MatchedArcsecPerPixel),
            'rotation_deg':float(solver.RotationAngleDegs),'matched_stars':int(solver.NumMatchedStars),
            'rms_arcsec':float(solver.MatchRmsErrorArcsec),'frame':'J2000'}


def star_positions(solver):
    plate=solver.Plate
    stars=[];matched=[]
    if plate is not None and plate.Plate is not None:
        for star in list(plate.Plate)[:int(plate.NumPlate)]:
            x,y=float(star.Xcen),float(star.Ycen)
            if math.isfinite(x) and math.isfinite(y):
                stars.append({'x':x,'y':y,'fwhm_pixels':float(star.FWHMGauss)})
                # PWLib sets SeqNum to the UCAC catalog ID for matched stars;
                # extracted stars without a catalog match retain SeqNum=-1.
                if solver.FoundMatch and int(star.SeqNum)>=0:
                    matched.append({**stars[-1],'catalog_id':int(star.SeqNum),
                                    'ra_hours':float(star.RARad)*12/math.pi,'dec_deg':math.degrees(float(star.DecRad))})
    if solver.FoundMatch and len(matched)!=int(solver.NumMatchedStars):
        raise ValueError('PWLib matched-star coordinate count differs from solve result')
    return {'detected_stars':int(solver.NumExtractedStars),'star_positions':stars,
            'matched_star_positions':matched,'star_source':'PlaneWave PWLib plate solve'}


if __name__=='__main__':
    try:
        payload=json.loads(sys.stdin.read())
        print(json.dumps(solve(payload)),flush=True)
    except Exception as exc: print(json.dumps({'success':False,'error':str(exc)}))
