"""Catalog-backed simulated RAW16 sensor. Never supplies a synthetic solve result."""
import json
import subprocess
import sys
import time
import numpy as np
from .astronomy import ROOT


def read_catalog(payload, cancel):
    process = subprocess.Popen([sys.executable, '-m', 'mount_service.simulation_catalog_worker'],
        stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True,
        cwd=ROOT/'python-mount-service')
    deadline = time.monotonic()+30
    data = json.dumps(payload)
    try:
        while True:
            if cancel.is_set(): raise ValueError('Catalog simulation cancelled')
            if time.monotonic()>deadline: raise ValueError('Catalog simulation timed out')
            try:
                output, error = process.communicate(input=data, timeout=.1)
                break
            except subprocess.TimeoutExpired:
                data = None
        if process.returncode: raise ValueError('Catalog simulation process failed: '+error[-500:])
        result = json.loads(output.strip().splitlines()[-1])
        if not result.get('success'): raise ValueError(result.get('error', 'Catalog simulation failed'))
        return result
    finally:
        if process.poll() is None:
            process.kill()
            process.communicate()


def render_sensor(device, exposure, binning, gain, dark, metadata, cancel, progress=None):
    w, h = device['width']//binning, device['height']//binning
    sensor = np.full((device['height'], device['width']), 120.*exposure, dtype=np.float32)
    count = 0
    if not dark:
        if not metadata or not all(k in metadata for k in ('SIMRA', 'SIMDEC', 'SIMSCALE')):
            raise ValueError('Catalog simulation needs fresh mount coordinates and telescope focal length')
        payload = dict(width=device['width'], height=device['height'], ra_hours=metadata['SIMRA'],
                       dec_deg=metadata['SIMDEC'], pixel_scale=metadata['SIMSCALE'],
                       rotation_deg=metadata.get('SIMROT', 0),
                       magnitude_limit=metadata.get('SIMMAG',16),max_stars=metadata.get('SIMMAX',5000))
        catalog = read_catalog(payload, cancel)
        for x, y, magnitude in catalog['stars']:
            if cancel.is_set(): raise ValueError('Simulated readout cancelled')
            x0, x1 = max(0,int(x)-9), min(device['width'],int(x)+10)
            y0, y1 = max(0,int(y)-9), min(device['height'],int(y)+10)
            if x1<=x0 or y1<=y0: continue
            yy, xx = np.mgrid[y0:y1,x0:x1]
            amplitude = min(1e6, 2000*10**(.4*(14-magnitude)))*exposure
            sensor[y0:y1,x0:x1] += amplitude*np.exp(-((xx-x)**2+(yy-y)**2)/8)
            count += 1
    if progress:progress('Exposing',exposure)
    if cancel.wait(exposure): raise ValueError('Exposure cancelled')
    if progress:progress('Readout')
    signal = sensor[:h*binning,:w*binning].reshape(h,binning,w,binning).sum(axis=(1,3))
    # Electron shot noise and sensor read noise; stellar positions come only from UCAC4.
    rng = np.random.default_rng()
    image = np.empty((h,w),dtype=np.uint16)
    for row in range(0,h,256):
        if cancel.is_set():raise ValueError('Simulated readout cancelled')
        block=signal[row:row+256]
        values=rng.poisson(block)*(1+gain/100)+500+rng.normal(0,4,block.shape)
        image[row:row+256]=np.clip(values,0,65535).astype(np.uint16)
    return image, count
