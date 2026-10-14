"""Fixed-field centroid/FWHM series matching the C# analysis interfaces."""
import asyncio
import math
import time
import numpy as np
from astropy.io import fits


def stars_in_fits(filename):
    image=np.asarray(fits.getdata(filename),dtype=np.float32)
    if image.ndim!=2:raise ValueError('Star analysis needs a two-dimensional camera FITS image')
    background=float(np.median(image));sigma=max(1.,1.4826*float(np.median(np.abs(image-background))))
    center=image[1:-1,1:-1];peaks=center>background+5*sigma
    for dy in range(3):
        for dx in range(3):
            if dx==1 and dy==1:continue
            peaks &= center>image[dy:dy+center.shape[0],dx:dx+center.shape[1]]
    positions=np.argwhere(peaks)+1
    if not len(positions):raise ValueError('No usable stars detected in this exposure')
    positions=sorted(positions,key=lambda p:float(image[p[0],p[1]]),reverse=True)[:200]
    stars=[]
    for y,x in positions:
        if x<7 or y<7 or x>=image.shape[1]-7 or y>=image.shape[0]-7:continue
        if any(math.hypot(s['x']-x,s['y']-y)<10 for s in stars):continue
        patch=image[y-7:y+8,x-7:x+8];weights=np.maximum(patch-background,0);flux=float(weights.sum())
        if not flux:continue
        yy,xx=np.mgrid[y-7:y+8,x-7:x+8];cx=float((weights*xx).sum()/flux);cy=float((weights*yy).sum()/flux)
        variance=float((weights*((xx-cx)**2+(yy-cy)**2)).sum()/(2*flux));fwhm=2.354820045*math.sqrt(variance)
        if 1<=fwhm<=25:stars.append({'x':cx,'y':cy,'fwhm_pixels':fwhm,'flux':flux})
    if not stars:raise ValueError('No usable star centroids in the image')
    return stars


def summarize(frames,scale):
    xy=np.array([[p['x'],p['y']] for p in frames]);center=xy.mean(axis=0);offsets=(xy-center)*scale
    jumps=np.linalg.norm(np.diff(xy,axis=0),axis=1)*scale
    fwhm=np.array([p['fwhm_pixels']*scale for p in frames])
    return {'usable_frames':len(frames),'fwhm_arcsec':float(np.median(fwhm)),'fwhm_std_arcsec':float(fwhm.std()),
            'x_rms_arcsec':float(np.sqrt(np.mean(offsets[:,0]**2))),'y_rms_arcsec':float(np.sqrt(np.mean(offsets[:,1]**2))),
            'total_rms_arcsec':float(np.sqrt(np.mean(np.sum(offsets**2,axis=1)))),
            'average_jump_arcsec':float(jumps.mean()) if len(jumps) else 0.,'max_jump_arcsec':float(jumps.max()) if len(jumps) else 0.,
            'drift_arcsec':float(np.linalg.norm(xy[-1]-xy[0])*scale),'pixel_scale':scale}


def summarize_positions(results):
    scale=results[0]['summary']['pixel_scale'];frames=[];x_errors=[];y_errors=[];jumps=[]
    for result in results:
        group=result['frames'];frames.extend(group)
        xy=np.array([[f['x'],f['y']] for f in group]);offset=(xy-xy.mean(axis=0))*scale
        x_errors.extend(offset[:,0]);y_errors.extend(offset[:,1]);jumps.extend(np.linalg.norm(np.diff(xy,axis=0),axis=1)*scale)
    fwhm=np.array([f['fwhm_pixels']*scale for f in frames])
    return {'positions':len(results),'usable_frames':len(frames),'pixel_scale':scale,'fwhm_arcsec':float(np.median(fwhm)),'fwhm_std_arcsec':float(fwhm.std()),
            'x_rms_arcsec':float(np.sqrt(np.mean(np.square(x_errors)))),'y_rms_arcsec':float(np.sqrt(np.mean(np.square(y_errors)))),
            'total_rms_arcsec':float(np.sqrt(np.mean(np.square(x_errors)+np.square(y_errors)))),'average_jump_arcsec':float(np.mean(jumps)) if jumps else 0.,'max_jump_arcsec':float(max(jumps,default=0.))}


async def series(workbench,count,interval_ms,publish=True):
    o=workbench.o;s=workbench.settings;frames=[];reference=None;scale=s['pixel_scale']*s['binning']
    for i in range(count):
        workbench.message=f'Capturing analysis frame {i+1}/{count}'
        capture=await o.capture(s['exposure_s'],s['binning'],s['gain'],False)
        detected=await asyncio.to_thread(stars_in_fits,capture['fits'])
        if reference is None:chosen=max(detected,key=lambda star:star['flux']);reference=chosen
        else:
            chosen=min(detected,key=lambda star:math.hypot(star['x']-reference['x'],star['y']-reference['y']))
            if math.hypot(chosen['x']-reference['x'],chosen['y']-reference['y'])>30:raise ValueError('Reference star lost (more than 30 pixels); series stopped')
        frames.append({**chosen,'utc':capture['capture_utc'],'index':i+1,'stars':len(detected),'fits':capture['fits']})
        if publish:workbench.analysis={'kind':'seeing-tracking','frames':list(frames),'summary':summarize(frames,scale)}
        if i<count-1:await asyncio.sleep(interval_ms/1000)
    return {'frames':frames,'summary':summarize(frames,scale)}


async def run_analysis(workbench,kind,count,interval_ms,positions):
    try:
        if kind=='seeing-tracking':workbench.analysis={'kind':kind,**await series(workbench,count,interval_ms)}
        else:
            results=[]
            for p in [row for row in workbench.plan if row.get('included',True)][:positions]:
                try:
                    await workbench.position(dict(p))
                    result=await series(workbench,count,interval_ms,False)
                    results.append({'azimuth_deg':p['azimuth_deg'],'altitude_deg':p['altitude_deg'],**result})
                    workbench.analysis={'kind':kind,'positions':list(results),'summary':summarize_positions(results)}
                finally:
                    await workbench.o.stop_tracking()
                    await workbench.o.mount.stop_all()
        workbench.state='complete';workbench.message='Analysis complete; graphs and measurements are ready.'
        path=workbench.o.directory/f'pointing-analysis-{int(time.time())}.json';path.write_text(__import__('json').dumps(workbench.analysis,indent=2))
        return workbench.analysis
    except asyncio.CancelledError:raise
    except Exception as exc:workbench.state='failed';workbench.message=str(exc);raise
    finally:workbench.current=None;workbench.save()
