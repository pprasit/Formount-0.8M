import asyncio
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import AsyncMock
import numpy as np
import pytest
from astropy.io import fits
from mount_service.pointing_workbench import PointingWorkbench
from mount_service.pointing_analysis import stars_in_fits, summarize
from mount_service.astronomy import Astronomy
from mount_service.pointing import PointingModel, parse_pxp_points
from fastapi.testclient import TestClient
from mount_service.api import create_app
from mount_service.service import MountService


def workbench(tmp_path):
    o=SimpleNamespace(directory=tmp_path,points=[],tracking=False,
        _save_points=lambda:None,stop_tracking=AsyncMock(),
        camera=SimpleNamespace(connected=False,busy=False,abort=lambda:None),
        mount=SimpleNamespace(stop_all=AsyncMock(),snapshot=lambda:{'axes':[]}))
    return PointingWorkbench(o)


def test_grid_preserves_solved_and_supports_single_altitude(tmp_path):
    w=workbench(tmp_path);w.create_grid()
    assert len(w.plan)==40
    first=w.plan[0];first['status']='solved';first['point_id']='real'
    w.create_grid();assert len(w.plan)==40 and w.plan[0]['point_id']=='real'
    w.settings.update(azimuth_points=3,altitude_points=1,min_altitude=35)
    w.plan=[];w.create_grid()
    assert [p['altitude_deg'] for p in w.plan]==[35,35,35]
    assert [p['azimuth_deg'] for p in w.plan]==[0,120,240]


def test_pause_cancels_slew_and_stops_mount(tmp_path):
    async def run():
        w=workbench(tmp_path)
        w.task=asyncio.create_task(asyncio.Event().wait())
        await w.stop(True)
        assert w.state=='paused' and not w.busy
        w.o.mount.stop_all.assert_awaited_once()
    asyncio.run(run())


def test_camera_guard_never_substitutes_simulation(tmp_path):
    w=workbench(tmp_path)
    with pytest.raises(ValueError,match='Connect a camera'):w.start()
    assert not w.busy


@pytest.mark.parametrize('count',[1,7,40,288])
def test_exact_count_and_azimuth_bounds(tmp_path,count):
    w=workbench(tmp_path)
    w.settings.update(point_count=count,min_azimuth=45,max_azimuth=225,min_altitude=35,max_altitude=75)
    w.create_grid()
    assert len(w.plan)==count
    assert all(45<=p['azimuth_deg']<=225 and 35<=p['altitude_deg']<=75 for p in w.plan)
    assert len({(p['azimuth_deg'],p['altitude_deg']) for p in w.plan})==count


def test_reset_archives_measurements_and_rebuilds_plan(tmp_path):
    import json
    w=workbench(tmp_path)
    w.o.points=[{'id':'previous','included':True}]
    w.settings.update(point_count=7)
    w.candidate={'path':'previous.pxp'}
    result=w.reset()
    assert not w.o.points and w.candidate is None and result['total']==7
    assert all(p['status']=='pending' for p in w.plan)
    archive=list(tmp_path.glob('calibration-points-*.json'))
    assert len(archive)==1 and json.loads(archive[0].read_text())[0]['id']=='previous'


def test_api_grid_selection_validation_and_operation_conflicts(tmp_path):
    app=create_app(MountService(simulation=True))
    w=app.state.pointing_workbench
    w.o.directory=tmp_path
    w.o.points=[]
    with TestClient(app) as client:
        assert client.post('/api/pointing/settings',json={'min_altitude':80,'max_altitude':30}).status_code==422
        assert client.post('/api/pointing/settings',json={'min_azimuth':225,'max_azimuth':45}).status_code==422
        response=client.post('/api/pointing/action',json={'action':'grid'})
        assert response.status_code==200 and response.json()['total']==40
        identifier=response.json()['plan'][0]['id']
        assert client.post('/api/pointing/points/'+identifier,json={'included':False}).status_code==200
        assert client.get('/api/pointing/workbench').json()['plan'][0]['included'] is False
        assert client.post('/api/pointing/action',json={'action':'start'}).status_code==409
        w.task=SimpleNamespace(done=lambda:False)
        assert client.post('/api/astronomy/goto',json={'body':'moon'}).status_code==409
        assert client.post('/api/axes/azimuth/command',json={'action':'jog','value':.01}).status_code==409
        assert client.post('/api/pointing/action',json={'action':'grid'}).status_code==409
        w.task=None
        assert client.post('/api/pointing/action',json={'action':'delete','identifiers':[identifier]}).json()['total']==39


def test_interactive_preview_preserves_raw_sensor_data(tmp_path):
    app=create_app(MountService(simulation=True))
    app.state.observatory.directory=tmp_path
    captures=tmp_path/'captures';captures.mkdir()
    raw=np.arange(80*60,dtype=np.uint16).reshape(60,80)
    fits.writeto(captures/'fixture.fits',raw)
    original=(captures/'fixture.fits').read_bytes()
    with TestClient(app) as client:
        response=client.get('/api/camera/stretch-source/fixture.fits')
        assert response.status_code==200
        assert response.headers['X-Preview-Width']=='80'
        assert response.headers['X-Preview-Height']=='60'
        assert np.array_equal(np.frombuffer(response.content,dtype='<u2').reshape(raw.shape),raw)
        assert float(response.headers['X-Stretch-White'])>float(response.headers['X-Stretch-Black'])
        assert client.get('/api/camera/stretch-source/missing.fits').status_code==404
    assert (captures/'fixture.fits').read_bytes()==original


def test_location_draft_validation_persistence_and_runtime_isolation():
    app=create_app(MountService(simulation=True))
    with TestClient(app) as client:
        original=dict(app.state.observatory.astro.site)
        assert client.get('/api/pointing/location').json()['location']==original
        draft={'latitude':18.9,'longitude':99.1,'elevation':550}
        assert client.post('/api/pointing/location',json={**draft,'latitude':91}).status_code==422
        assert client.post('/api/pointing/location',json=draft).status_code==200
        assert client.get('/api/pointing/location').json()['location']==draft
        assert app.state.observatory.astro.site==original
        import json
        assert json.loads(app.state.pointing_workbench.path.read_text())['location']==draft


def test_real_image_analysis_and_empty_image_failure(tmp_path):
    y,x=np.mgrid[:80,:80];image=100+1000*np.exp(-((x-40.2)**2+(y-39.7)**2)/(2*2**2))
    path=tmp_path/'test-only.fits';fits.writeto(path,image)
    star=stars_in_fits(path)[0]
    assert star['x']==pytest.approx(40.2,abs=.02)
    assert star['fwhm_pixels']==pytest.approx(4.7096,abs=.05)
    result=summarize([star,{**star,'x':star['x']+2}],.5)
    assert result['total_rms_arcsec']==pytest.approx(.5)
    fits.writeto(path,np.ones((80,80)),overwrite=True)
    with pytest.raises(ValueError,match='No usable stars'):stars_in_fits(path)


@pytest.mark.parametrize('backend',['legacy-pxp','experimental-core-direct'])
def test_candidate_preserves_active_model_and_native_pxp_round_trip(tmp_path,monkeypatch,backend):
    astro=Astronomy();model=PointingModel(astro);model.initialize()
    import mount_service.pointing as module
    monkeypatch.setattr(module,'ROOT',tmp_path)
    points=[];utc='2026-10-08T12:00:00'
    for i,(az,alt) in enumerate([(60,20),(100,35),(150,65),(210,45),(280,30),(320,70),
                               (350,55),(80,75),(240,25),(300,50),(180,40),(120,50)]):
        sky=astro.from_encoder(az,alt,utc)
        points.append(dict(name=f'Test fixture {i}',solved_ra_hours=sky['ra_hours'],solved_dec_deg=sky['dec_deg'],
                           encoder_azimuth_deg=az,encoder_altitude_deg=alt,capture_utc=utc,included=i!=11))
    preview=model.fit(points,backend,False)
    assert not model.active and model.path is None and preview['used']==11
    active=model.fit(points,backend)
    correction=model.corrected(60,20)
    candidate=model.fit(points,backend,False)
    assert model.path==active['path'] and model.active
    assert model.corrected(60,20)==pytest.approx(correction,abs=1e-7)
    assert candidate['used']==11 and len(candidate['residuals'])==12
    parsed=parse_pxp_points(candidate['path'])
    assert len(parsed)==12 and not parsed[-1]['included']
    assert parsed[0]['solved_ra_hours']==pytest.approx(points[0]['solved_ra_hours'])
    inspected=model.inspect_file(candidate['path'])
    assert inspected['used']==11 and model.path==active['path']
    assert model.corrected(60,20)==pytest.approx(correction,abs=1e-7)
    model.load(candidate['path'])
    assert model.corrected(60,20)==pytest.approx(correction,abs=1e-7)


def test_manual_capture_is_not_part_of_automatic_progress(tmp_path):
    w=workbench(tmp_path);w.settings.update(point_count=7);w.create_grid()
    w.o.camera.connected=True
    w.o.mount.snapshot=lambda:{'axes':[{'valid':True,'position_deg':90},{'valid':True,'position_deg':45}]}
    w.launch=lambda kind,function: {'kind':kind}
    w.add_current()
    manual=w.plan[-1]
    assert manual['automatic_series'] is False
    for status in ('capturing','solving','solved','failed','pending'):
        manual['status']=status
        result=w.snapshot()
        assert (result['total'],result['processed'],result['solved'])==(7,0,0)
    w.create_grid()
    assert w.snapshot()['total']==7 and manual in w.plan
    w.o.points=[{'id':'manual-restored','encoder_azimuth_deg':90,'encoder_altitude_deg':45,'automatic_series':False}]
    assert w.snapshot()['total']==7
    restored=PointingWorkbench(w.o)
    assert restored.snapshot()['total']==7


def test_new_image_session_boundary_is_saved_without_changing_points(tmp_path):
    w=workbench(tmp_path);w.settings.update(point_count=7);w.create_grid()
    previous=list(w.plan)
    boundary=w.new_image_session()['image_session_started_utc']
    assert boundary and w.plan==previous
    restored=PointingWorkbench(w.o)
    assert restored.snapshot()['image_session_started_utc']==boundary


def test_rms_uses_included_pointing_errors_and_selection_invalidates_fit(tmp_path):
    w=workbench(tmp_path);w.o.points=[{'id':'one','total_error_arcsec':3,'encoder_azimuth_deg':0,'encoder_altitude_deg':45},{'id':'two','total_error_arcsec':4,'encoder_azimuth_deg':90,'encoder_altitude_deg':45}]
    w.candidate={'rms_arcsec':1.2}
    result=w.snapshot()
    assert result['calibration_rms_arcsec']==pytest.approx(np.sqrt(12.5))
    assert result['model_rms_arcsec']==1.2
    one=next(p for p in w.plan if p['point_id']=='one')
    result=w.select(one['id'],False)
    assert result['calibration_rms_arcsec']==4
    assert result['model_rms_arcsec'] is None and not w.o.points[0]['included']


def test_pointing_error_matches_csharp_ra_wrap_and_mean_declination():
    from mount_service.pointing import pointing_error
    errors=pointing_error(23.999,60,0.001,60.001)
    assert errors['ra_error_arcsec']==pytest.approx(.002*15*3600*np.cos(np.deg2rad(60.0005)))
    assert errors['dec_error_arcsec']==pytest.approx(3.6)
    assert errors['total_error_arcsec']==pytest.approx(np.hypot(errors['ra_error_arcsec'],3.6))


def test_fit_inputs_and_rms_exclude_images_from_previous_session(tmp_path):
    w=workbench(tmp_path);w.image_session_started_utc='2026-10-11T00:00:00+00:00'
    w.o.points=[{'id':'old','capture_utc':'2026-10-10T23:59:59Z','total_error_arcsec':99,'encoder_azimuth_deg':0,'encoder_altitude_deg':45},
                {'id':'new','capture_utc':'2026-10-11T01:00:00','total_error_arcsec':3,'encoder_azimuth_deg':90,'encoder_altitude_deg':45}]
    assert [p['id'] for p in w.fit_points()]==['new']
    assert w.snapshot()['calibration_rms_arcsec']==3


@pytest.mark.parametrize('error,automatic,rejected',[(99.9,True,False),(100,True,False),(100.1,True,True),(101,False,False)])
def test_auto_reject_threshold_preserves_capture_and_fit_exclusion(tmp_path,error,automatic,rejected):
    from mount_service.image_archive import ImageArchive
    async def run():
        w=workbench(tmp_path);w.settings['post_exposure_s']=0
        captures=tmp_path/'captures';captures.mkdir()
        fits.writeto(captures/'fixture.fits',np.ones((20,20),dtype=np.uint16))
        w.o.image_archive=ImageArchive(tmp_path);w.o.image_archive.ensure('fixture.fits')
        p={'id':'plan-1','azimuth_deg':0,'altitude_deg':45,'status':'pending','included':True,'automatic_series':automatic};w.plan=[p]
        point={'id':'point-1','included':True,'total_error_arcsec':error}
        w.o.capture=AsyncMock(return_value={'filename':'fixture.fits'})
        async def solve(*args):w.o.points.append(point);return {'matched_stars':20,'rms_arcsec':.1}
        w.o.solve=solve
        await w.capture_point(p,{'ra_hours':1,'dec_deg':30})
        assert p['status']==('rejected' if rejected else 'solved')
        assert point['included'] is not rejected
        metadata=w.o.image_archive.get('fixture.fits')['metadata']
        assert metadata['point']['included'] is not rejected
        if rejected:
            assert '100 arcsec' in metadata['rejection_reason']
            assert (w.snapshot()['total'],w.snapshot()['processed'],w.snapshot()['solved'])==(1,1,0)
            assert w.snapshot()['calibration_rms_arcsec'] is None
            assert PointingWorkbench(w.o).plan[0]['status']=='rejected'
    asyncio.run(run())


def test_automatic_series_continues_after_rejecting_bad_point(tmp_path):
    from mount_service.image_archive import ImageArchive
    async def run():
        w=workbench(tmp_path);w.settings['post_exposure_s']=0
        (tmp_path/'captures').mkdir();w.o.image_archive=ImageArchive(tmp_path)
        for name in ('first.fits','second.fits'):
            fits.writeto(tmp_path/'captures'/name,np.ones((20,20),dtype=np.uint16));w.o.image_archive.ensure(name)
        w.plan=[{'id':'first','azimuth_deg':0,'altitude_deg':45,'status':'pending','included':True},{'id':'second','azimuth_deg':90,'altitude_deg':45,'status':'pending','included':True}]
        w.position=AsyncMock(return_value={'ra_hours':1,'dec_deg':30})
        w.o.capture=AsyncMock(side_effect=[{'filename':'first.fits'},{'filename':'second.fits'}])
        async def solve(*args):
            w.o.points.append({'id':str(len(w.o.points)),'included':True,'total_error_arcsec':101 if not w.o.points else 1})
            return {'matched_stars':20,'rms_arcsec':.1}
        w.o.solve=solve
        await w.run()
        assert w.state=='complete' and [p['status'] for p in w.plan]==['rejected','solved']
        assert (w.snapshot()['total'],w.snapshot()['processed'],w.snapshot()['solved'])==(2,2,1)
        assert w.snapshot()['calibration_rms_arcsec']==1
        assert w.o.mount.stop_all.await_count==2
    asyncio.run(run())


def test_save_model_preserves_native_file_and_activates_selected_path(tmp_path):
    async def run():
        w=workbench(tmp_path)
        w.o.points=[{'id':str(i),'name':str(i),'included':True,'encoder_azimuth_deg':i*30,'encoder_altitude_deg':45,'solved_ra_hours':i,'solved_dec_deg':30} for i in range(9)]
        native=tmp_path/'working.pxp';native.write_text('Cal Star 9:\nModel Terms:\n0.123\n')
        w.candidate={'path':str(native),'rms_arcsec':.5}
        w.o.load_model=AsyncMock();w.o.model_status={'active':True};w.o.mount.event=lambda *args:None
        assert w.save_ready()
        result=await w.save_model(str(tmp_path),'Chosen model')
        destination=tmp_path/'Chosen model.pxp'
        assert destination.read_bytes()==native.read_bytes()
        w.o.load_model.assert_awaited_once_with(str(destination))
        assert result['path']==str(destination)
        assert w.candidate['saved']
        import json
        assert json.loads((tmp_path/'active-model.json').read_text())['path']==str(destination)
        with pytest.raises(ValueError,match='already exists'):await w.save_model(str(tmp_path),'Chosen model')
        with pytest.raises(ValueError,match='valid'):await w.save_model(str(tmp_path),'../outside')
        with pytest.raises(ValueError,match='reserved'):await w.save_model(str(tmp_path),'CON')
        w.o.points[0]['included']=False
        assert not w.save_ready()
        with pytest.raises(ValueError,match='9 distinct'):await w.save_model(str(tmp_path),'Too few')
    asyncio.run(run())


def test_manual_solve_sync_race_does_not_count_or_duplicate_series(tmp_path):
    w=workbench(tmp_path)
    manual={'id':'manual','status':'solving','automatic_series':False,'capture_filename':'test.fits'}
    w.plan=[{'id':str(i),'status':'pending'} for i in range(100)]+[manual]
    point={'id':'point','included':True,'encoder_azimuth_deg':30,'encoder_altitude_deg':45}
    w.o.points=[point]
    assert w.snapshot()['total']==100
    assert len(w.plan)==101
    manual.update(point_id='point',status='solved')
    w.plan.append({'id':'race-duplicate','point_id':'point','status':'solved','automatic_series':True})
    state=w.snapshot()
    assert (state['total'],state['solved'],state['processed'])==(100,0,0)
    assert len(w.plan)==101
    assert point['automatic_series'] is False
