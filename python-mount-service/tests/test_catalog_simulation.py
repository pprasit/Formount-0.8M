"""Opt-in native catalog/solver integration; never controls a mount or saves a model."""
import asyncio
import json
import math
import os
import subprocess
import sys
import threading
from pathlib import Path
from types import SimpleNamespace
import pytest
from astropy.io import fits
from astropy.coordinates import SkyCoord
import astropy.units as u
from mount_service.astronomy import ROOT
from mount_service.camera import CameraService, simulated_camera
from mount_service.observatory import Observatory


@pytest.mark.skipif(os.environ.get('NARIT_NATIVE_CATALOG_TESTS')!='1', reason='Opt-in installed PWLib/UCAC4 integration')
@pytest.mark.parametrize('ra,dec,binning,rotation',[(12/math.pi,math.degrees(.5),1,0),(23.999,20,2,37),(6,80,1,-62)])
def test_catalog_image_solves_without_wcs(tmp_path,monkeypatch,ra,dec,binning,rotation):
    monkeypatch.setattr('mount_service.camera.ROOT',tmp_path)
    camera=CameraService();camera.devices=[simulated_camera()];camera.connect('simulation:camera');camera.connected.update(width=1280,height=960)
    scale=206.264806247*3.76/1000
    capture=camera.capture(1,binning=binning,gain=100,metadata=dict(SIMRA=ra,SIMDEC=dec,SIMSCALE=scale,SIMROT=rotation))
    with fits.open(capture['fits']) as hdus:
        assert 'CTYPE1' not in hdus[0].header and 'CRVAL1' not in hdus[0].header
        assert hdus[0].header['SIMCAT']=='UCAC4' and hdus[0].header['SIMSTARS']>10
        assert hdus[0].header['SIMULATE']
    payload=dict(fits=capture['fits'],ra_hours=(ra+.001)%24,dec_deg=dec+.01,pixel_scale=scale*binning)
    process=subprocess.run([sys.executable,'-m','mount_service.solver_worker'],input=json.dumps(payload),capture_output=True,text=True,timeout=55,cwd=ROOT/'python-mount-service')
    result=json.loads(process.stdout.strip().splitlines()[-1])
    assert result['success'],result
    separation=SkyCoord(ra=ra*u.hourangle,dec=dec*u.deg).separation(SkyCoord(ra=result['ra_hours']*u.hourangle,dec=result['dec_deg']*u.deg)).arcsec
    assert separation<3
    assert result['pixel_scale']==pytest.approx(scale*binning,rel=.02)
    assert result['matched_stars']>=10
    print(json.dumps(dict(ra=ra,dec=dec,binning=binning,rotation=rotation,center_error_arcsec=separation,**result)))
    camera.close()


def test_observatory_uses_actual_encoder_site_and_selected_telescope(monkeypatch,tmp_path):
    axes=[dict(id='azimuth',valid=True,position_deg=145.),dict(id='altitude',valid=True,position_deg=55.)]
    mount=SimpleNamespace(snapshot=lambda:dict(axes=axes))
    o=Observatory(mount)
    o.astro.set_site(18.85,98.95,350)
    o.camera.devices=[simulated_camera()];o.camera.connect('simulation:camera')
    scopes=[dict(id='selected',name='OTA',focal_length_mm=1000,optical_multiplier=2)]
    o.pointing_workbench=SimpleNamespace(config=SimpleNamespace(get=lambda _:None,telescopes=lambda:scopes))
    received=[]
    def capture(exposure,binning,gain,dark,metadata,epoch,roi=None,frame_type=None,readout=None):
        received.append(metadata)
        import numpy as np
        path=tmp_path/'fixture.fits';fits.writeto(path,np.ones((8,8),dtype=np.uint16),overwrite=True)
        return dict(filename=path.name,fits=str(path),preview='test.png',capture_utc='2026-10-10T00:00:00Z',exposure_s=exposure)
    monkeypatch.setattr(o.camera,'capture',capture)
    try:
        asyncio.run(o.capture(1,2,100,False,telescope_id='selected'))
        metadata=received[0]
        from datetime import datetime,timezone
        midpoint=datetime.fromtimestamp(metadata['SIMSTART']+.5,timezone.utc).isoformat().replace('+00:00','Z')
        expected=o.astro.from_encoder(145,55,midpoint)
        assert metadata['SIMRA']==pytest.approx(expected['ra_hours'])
        assert metadata['SIMDEC']==pytest.approx(expected['dec_deg'])
        assert metadata['SIMSCALE']==pytest.approx(206.264806247*3.76/2000)
        assert metadata['PIXSCALE']==pytest.approx(metadata['SIMSCALE']*2)
        assert metadata['SITELONG']==98.95
        assert metadata['SIMMAG']==18 and metadata['SIMMAX']==15000
        asyncio.run(o.capture(1,1,0,False,telescope_id='selected',simulation_purpose='calibration'))
        assert received[-1]['SIMMAG']==16 and received[-1]['SIMMAX']==5000
        axes[0]['valid']=False
        with pytest.raises(ValueError,match='fresh mount encoders'):
            asyncio.run(o.capture(1,1,0,False,telescope_id='selected'))
    finally:
        o.camera.close();o.actor.shutdown();o.camera_actor.shutdown()
