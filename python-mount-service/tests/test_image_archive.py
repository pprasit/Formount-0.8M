import json
from types import SimpleNamespace
import numpy as np
from astropy.io import fits
from fastapi.testclient import TestClient
from mount_service.image_archive import ImageArchive
from mount_service.api import create_app
from mount_service.service import MountService


def test_reopen_retains_preview_all_profiles_metadata_and_manual_stretch(tmp_path,monkeypatch):
    captures=tmp_path/'captures';captures.mkdir()
    raw=np.arange(1500*1000,dtype=np.uint16).reshape(1000,1500)
    path=captures/'fixture.fits'
    header=fits.Header({'EXPTIME':.125,'XBINNING':2,'AZENC':120.,'ALTENC':45.,'DATE-OBS':'2026-10-10T15:00:00Z'})
    fits.writeto(path,raw,header)
    archive=ImageArchive(tmp_path)
    record=archive.ensure(path.name,{'calibration_plan_id':'plan-1','calibration_status':'failed','error':'No matching stars'})
    assert len(record['presets'])==8
    assert record['metadata']['exposure_s']==.125
    assert record['preview_width']<=1280 and record['preview_height']<=1280
    source=archive.source(path.name)
    view={'profile':'Manual','black':200,'white':3000,'tone':8}
    auto_png=archive.source(path.name,png=True)[0]
    archive.save_stretch(path.name,view)
    saved_png=archive.source(path.name,png=True)[0]
    assert saved_png!=auto_png
    archive.save_stretch(path.name,{**view,'zoom':2,'pan_x':20,'pan_y':-10})
    assert archive.source(path.name,png=True)[0]==saved_png
    archive.save_stretch(path.name,view)
    original=path.read_bytes()
    monkeypatch.setattr('mount_service.image_archive.fits.getdata',lambda *a,**k: (_ for _ in ()).throw(AssertionError('History must not reread FITS')))
    reopened=ImageArchive(tmp_path)
    assert reopened.ensure(path.name)['stretch']==view
    assert reopened.source(path.name,png=True)[0]==saved_png
    assert reopened.source(path.name)==source
    assert reopened.source(path.name,True)[0].startswith(b'\x89PNG')
    assert reopened.calibration_images()[0]['metadata']['error']=='No matching stars'
    assert path.read_bytes()==original


def test_image_endpoints_restore_saved_view_without_fits(tmp_path):
    app=create_app(MountService(simulation=True));app.state.observatory.directory=tmp_path;app.state.observatory.points=[]
    captures=tmp_path/'captures';captures.mkdir()
    path=captures/'fixture.fits';fits.writeto(path,np.arange(80*60,dtype=np.uint16).reshape(60,80))
    app.state.observatory.image_archive.ensure(path.name,{'point':{'id':'point-1','name':'Point 1'},'calibration_status':'solved',
        'solution':{'success':True,'detected_stars':1,'matched_star_positions':[{'x':20.5,'y':30.5,'fwhm_pixels':4}],'star_source':'PlaneWave PWLib extraction'}})
    with TestClient(app) as client:
        assert len(client.get('/api/camera/images/fixture.fits').json()['presets'])==8
        view=dict(profile='Manual',black=100,white=3000,tone=7,zoom=2,pan_x=40,pan_y=-20)
        assert client.post('/api/camera/images/fixture.fits/stretch',json=view).status_code==200
        assert client.post('/api/camera/images/fixture.fits/stretch',json={**view,'white':50}).status_code==422
        assert client.post('/api/camera/images/missing.fits/stretch',json=view).status_code==404
        first=client.post('/api/camera/images/fixture.fits/stars',json={})
        assert first.status_code==200 and first.json()['metadata']['matched_star_positions'][0]['x']==20.5
        assert client.post('/api/camera/images/fixture.fits/stars',json={}).json()['metadata']['matched_star_positions']==first.json()['metadata']['matched_star_positions']
        assert not app.state.observatory.points
    path.unlink()
    reopened=create_app(MountService(simulation=True));reopened.state.observatory.directory=tmp_path;reopened.state.observatory.points=[];reopened.state.pointing_workbench.plan=[];reopened.state.pointing_workbench.image_session_started_utc=None
    with TestClient(reopened) as client:
        assert client.get('/api/camera/images/fixture.fits').json()['stretch']==view
        assert client.get('/api/camera/stretch-source/fixture.fits').status_code==200
        assert client.get('/api/camera/preview/fixture.png').status_code==200
        assert client.get('/api/camera/latest-image').json()['image']['stretch']==view
        assert client.get('/api/pointing/images').json()['images'][0]['metadata']['point']['id']=='point-1'


def test_extraction_without_successful_solve_cannot_supply_overlay(tmp_path,monkeypatch):
    app=create_app(MountService(simulation=True));app.state.observatory.directory=tmp_path;app.state.observatory.points=[]
    captures=tmp_path/'captures';captures.mkdir()
    path=captures/'unmatched.fits'
    fits.writeto(path,np.ones((20,20),dtype=np.uint16),fits.Header({'SIMRA':1.,'SIMDEC':20.,'PIXSCALE':1.}))
    app.state.observatory.image_archive.ensure(path.name,{'star_positions':[{'x':10,'y':10}],'detected_stars':23})
    monkeypatch.setattr('subprocess.run',lambda *a,**k:SimpleNamespace(returncode=0,stderr='',stdout=json.dumps({'success':False,'detected_stars':23,'star_positions':[{'x':10,'y':10}],'error':'No catalog match'})))
    with TestClient(app) as client:
        response=client.post('/api/camera/images/unmatched.fits/stars',json={})
        assert response.status_code==409 and response.json()['detail']=='No catalog match'
        assert not app.state.observatory.image_archive.get(path.name)['metadata'].get('matched_star_positions')
        assert not app.state.observatory.points


def test_new_image_session_hides_previous_captures_without_deleting_them(tmp_path):
    captures=tmp_path/'captures';captures.mkdir()
    archive=ImageArchive(tmp_path)
    for name,date in [('old.fits','2026-10-10T15:00:00Z'),('new.fits','2026-10-11T15:00:00Z')]:
        fits.writeto(captures/name,np.ones((20,20),dtype=np.uint16))
        archive.ensure(name,{'capture_utc':date,'point':{'id':name}})
    boundary='2026-10-11T14:00:00+00:00'
    assert [r['filename'] for r in archive.calibration_images(boundary)]==['new.fits']
    assert not archive.calibration_images('2026-10-12T00:00:00+00:00')
    assert archive.get('old.fits') and (captures/'old.fits').exists()


def test_use_in_model_persists_to_image_metadata_and_fit_inputs(tmp_path):
    app=create_app(MountService(simulation=True));o=app.state.observatory;w=app.state.pointing_workbench
    o.directory=tmp_path;w.path=tmp_path/'workbench.json'
    point={'id':'point-1','name':'Point 1','included':True,'fits':str(tmp_path/'captures'/'fixture.fits'),'encoder_azimuth_deg':90,'encoder_altitude_deg':45}
    o.points=[point];w.plan=[{'id':'plan-1','point_id':'point-1','capture_filename':'fixture.fits','status':'solved','included':True}]
    w.candidate={'rms_arcsec':1.2}
    (tmp_path/'captures').mkdir();fits.writeto(tmp_path/'captures'/'fixture.fits',np.ones((20,20),dtype=np.uint16))
    o.image_archive.ensure('fixture.fits',{'point':point})
    with TestClient(app) as client:
        response=client.post('/api/pointing/points/plan-1',json={'included':False})
        assert response.status_code==200 and response.json()['model_rms_arcsec'] is None
        assert o.points[0]['included'] is False
        assert client.get('/api/camera/images/fixture.fits').json()['metadata']['point']['included'] is False
    assert json.loads((tmp_path/'calibration-points.json').read_text())[0]['included'] is False
