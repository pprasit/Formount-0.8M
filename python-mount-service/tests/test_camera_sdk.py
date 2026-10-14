import ctypes as c
import threading

import numpy as np
import pytest
from astropy.io import fits
from fastapi.testclient import TestClient

from mount_service.camera import CameraService
from mount_service.camera_andor import Andor2, Andor3
from mount_service.api import create_app
from mount_service.service import MountService


def put(pointer, value):
    pointer._obj.value = value


class Function:
    def __init__(self, fn): self.fn = fn
    def __call__(self, *args): return self.fn(*args)


class SDK:
    def __init__(self, default=0, **functions):
        self.default, self.functions, self.calls = default, functions, []
    def __getattr__(self, name):
        def call(*args):
            self.calls.append((name, args))
            fn = self.functions.get(name)
            return fn(*args) if fn else self.default
        fn = Function(call)
        setattr(self, name, fn)
        return fn


def zwo(tmp_path, monkeypatch):
    import mount_service.camera as module
    monkeypatch.setattr(module, 'ROOT', tmp_path)
    camera = CameraService()
    camera.connected = dict(key='zwo:7', vendor='zwo', name='ASI Test', id=7, width=19, height=7,
                            bins=[1, 2], formats=[2], pixel_um=3.76, gain_supported=True)
    def read(_id, pointer, size):
        image = np.arange(size // 2, dtype=np.uint16)
        c.memmove(pointer, image.ctypes.data, size)
        return 0
    camera.asi = SDK(ASIGetExpStatus=lambda _id, p: (put(p, 2) or 0), ASIGetDataAfterExp=read)
    return camera


def test_zwo_raw16_roi_origin_fits_and_metadata(tmp_path, monkeypatch):
    camera = zwo(tmp_path, monkeypatch)
    result = camera.capture(.02, 1, 12)
    image, header = fits.getdata(result['fits'], header=True)
    assert image.shape == (6, 16)
    assert image.dtype.kind == 'u' and image[5, 15] == 95
    assert header['GAIN'] == 12 and header['XPIXSZ'] == 3.76
    assert header['EXPTIME'] == .02 and header['IMAGETYP'] == 'Light'
    assert ('ASISetStartPos', (7, 0, 0)) in camera.asi.calls
    assert camera.asi.calls[-1][0] == 'ASIStopExposure'
    assert not camera.busy


def test_zwo_failure_aborts_without_saving(tmp_path, monkeypatch):
    camera = zwo(tmp_path, monkeypatch)
    camera.asi = SDK(ASIGetExpStatus=lambda _id, p: (put(p, 3) or 0))
    with pytest.raises(ValueError, match='failed'): camera.capture(.02)
    assert camera.asi.calls[-1][0] == 'ASIStopExposure'
    assert not list(tmp_path.rglob('*.fits'))
    assert not camera.busy


def test_zwo_cancelled_at_readout_never_saves(tmp_path, monkeypatch):
    camera = zwo(tmp_path, monkeypatch)
    camera.asi = SDK(ASIGetExpStatus=lambda _id, p: (camera.abort() or put(p, 2) or 0))
    with pytest.raises(ValueError, match='cancelled'): camera.capture(.02)
    assert not any(name == 'ASIGetDataAfterExp' for name, _ in camera.asi.calls)
    assert camera.asi.calls[-1][0] == 'ASIStopExposure'


def test_zwo_unsupported_settings_fail_before_acquisition(tmp_path, monkeypatch):
    camera = zwo(tmp_path, monkeypatch)
    camera.connected['controls'] = {'0': dict(minimum=0, maximum=100)}
    with pytest.raises(ValueError, match='Gain'): camera.capture(.02, gain=101)
    with pytest.raises(ValueError, match='binning'): camera.capture(.02, binning=4)
    assert camera.asi.calls == []


def test_abort_while_capture_is_queued_does_not_start_exposure(tmp_path, monkeypatch):
    camera = zwo(tmp_path, monkeypatch)
    epoch = camera.prepare_capture()
    camera.abort()
    with pytest.raises(ValueError, match='before acquisition'):
        camera.capture(.02, epoch=epoch)
    assert camera.asi.calls == [] and not camera.busy


def test_fli_row_readout_abort_and_long_handle(tmp_path, monkeypatch):
    import mount_service.camera as module
    monkeypatch.setattr(module, 'ROOT', tmp_path)
    camera = CameraService(); camera.device = c.c_long(23)
    camera.connected = dict(key='fli:usb0', vendor='fli', name='FLI Test', width=8, height=6,
                            bounds=[10, 20, 18, 26])
    rows = []
    def row(handle, pointer, width):
        values = np.full(width, len(rows) + 20, dtype=np.uint16); rows.append(width)
        c.memmove(pointer, values.ctypes.data, width * 2)
        return 0
    camera.fli = SDK(FLIGetExposureStatus=lambda handle, p: (put(p, 0) or 0), FLIGrabRow=row)
    result = camera.capture(.02, 2)
    assert rows == [4, 4, 4]
    assert np.array_equal(fits.getdata(result['fits']), np.array([[20] * 4, [21] * 4, [22] * 4]))
    assert ('FLISetImageArea', (camera.device, 10, 20, 18, 26)) in camera.fli.calls
    assert camera.fli.FLIGrabRow.argtypes == [c.c_long, c.c_void_p, c.c_size_t]
    camera.fli = SDK(FLIGetExposureStatus=lambda handle, p: (put(p, 0) or 0), FLIGrabRow=lambda *args: -5)
    with pytest.raises(ValueError, match='read row'): camera.capture(.02, 2)
    assert camera.fli.calls[-1][0] == 'FLICancelExposure'


def sdk3():
    adapter = Andor3(); adapter.handle = 4
    integers = {'AOIWidth': 3, 'AOIHeight': 2, 'AOIStride': 8, 'ImageSizeBytes': 16}
    queued = []
    def wait(handle, pointer, size, timeout):
        pixels = np.array([[11, 12, 13, 999], [21, 22, 23, 999]], dtype='<u2')
        c.memmove(queued[0], pixels.ctypes.data, 16); put(pointer, queued[0]); put(size, 16)
        return 0
    adapter.dll = SDK(AT_GetInt=lambda handle, key, p: (put(p, integers[key]) or 0),
                      AT_GetFloat=lambda handle, key, p: (put(p, .05) or 0),
                      AT_QueueBuffer=lambda handle, pointer, size: (queued.append(pointer) or 0),
                      AT_WaitBuffer=wait)
    return adapter, dict(bins=[1], exposure_min_s=.001, exposure_max_s=600)


def test_sdk3_padded_rows_are_copied_before_buffer_flush():
    adapter, device = sdk3()
    pixels, exposure, _ = adapter.capture(device, .05, 1, 0, False, threading.Event())
    assert np.array_equal(pixels, [[11, 12, 13], [21, 22, 23]])
    assert exposure == .05 and adapter.pending_buffer is None
    assert adapter.dll.calls[-2][0] == 'AT_Command' and adapter.dll.calls[-1][0] == 'AT_Flush'
    queued = next(args for name, args in adapter.dll.calls if name == 'AT_QueueBuffer')
    assert queued[1] % 8 == 0


def test_sdk3_cancellation_releases_queued_buffer():
    adapter, device = sdk3(); cancel = threading.Event(); cancel.set()
    with pytest.raises(ValueError, match='cancelled'): adapter.capture(device, .05, 1, 0, False, cancel)
    assert adapter.dll.calls[-1][0] == 'AT_Flush' and adapter.pending_buffer is None


def test_sdk3_invalid_buffer_rejected():
    adapter, device = sdk3()
    adapter.dll.AT_WaitBuffer = Function(lambda handle, pointer, size, timeout:
                                         (put(pointer, 1234) or put(size, 16) or 0))
    with pytest.raises(ValueError, match='unexpected'):
        adapter.capture(device, .05, 1, 0, False, threading.Event())
    assert adapter.dll.calls[-1][0] == 'AT_Flush'


def test_sdk2_uses_vendor_success_code_and_actual_exposure():
    adapter = Andor2(); adapter.handle = 5
    def read(pointer, size):
        image = np.arange(size, dtype=np.uint16); c.memmove(pointer, image.ctypes.data, size * 2)
        return 20002
    adapter.dll = SDK(default=20002, GetStatus=lambda p: (put(p, 20073) or 20002),
                      GetAcquisitionTimings=lambda actual, *_: (put(actual, .06) or 20002),
                      GetAcquiredData16=read)
    device = dict(width=4, height=2, bins=[1], shutter=False)
    image, exposure, _ = adapter.capture(device, .05, 1, 0, False, threading.Event())
    assert image.tolist() == [[0, 1, 2, 3], [4, 5, 6, 7]]
    assert exposure == pytest.approx(.06)
    assert ('SetImage', (1, 1, 1, 4, 1, 2)) in adapter.dll.calls


def test_sdk2_cancellation_aborts():
    adapter = Andor2(); adapter.handle = 5; cancel = threading.Event(); cancel.set()
    adapter.dll = SDK(default=20002, GetStatus=lambda p: (put(p, 20073) or 20002),
                      GetAcquisitionTimings=lambda actual, *_: (put(actual, .05) or 20002))
    with pytest.raises(ValueError, match='cancelled'):
        adapter.capture(dict(width=4, height=2, bins=[1], shutter=False), .05, 1, 0, False, cancel)
    assert adapter.dll.calls[-1][0] == 'AbortAcquisition'


def test_imaging_api_persistence_scale_validation_and_audit():
    app = create_app(MountService(simulation=True))
    with TestClient(app) as client:
        telescope = client.post('/api/pointing/telescopes', json={'name': 'OTA', 'focal_length_mm': 1000}).json()['telescope']
        camera = app.state.observatory.camera
        camera.asi = SDK()
        camera.connected = dict(key='zwo:5', vendor='zwo', id=5, name='ASI Test', width=100, height=80,
                                bins=[1, 2], gain_supported=True)
        values = dict(name='Imager', telescope_id=telescope['id'], device_key='zwo:5', pixel_um=3.76,
                      exposure_s=1, binning=2, gain=0)
        response = client.post('/api/pointing/imaging', json=values)
        assert response.status_code == 200, response.text
        scale = 206.264806247 * 3.76 * 2 / 1000
        assert response.json()['imaging']['pixel_scale'] == pytest.approx(scale)
        assert client.get('/api/pointing/imaging').json()['imaging']['name'] == 'Imager'
        settings = client.get('/api/pointing/workbench').json()['settings']
        assert settings['binning'] == 2 and settings['pixel_scale'] == pytest.approx(scale/2)
        assert client.post('/api/pointing/imaging', json={**values, 'device_key': 'zwo:8'}).status_code == 409
        assert client.post('/api/pointing/imaging', json={**values, 'pixel_um': -1}).status_code == 422
        assert client.post('/api/pointing/imaging', json={**values, 'binning': 4}).status_code == 409
        camera.busy = True
        assert client.post('/api/camera/scan').status_code == 409
        camera.busy = False
        events = client.get('/api/audit/events').json()['events']
        assert any(e['kind'] == 'mount.info' and e['details'].get('title') == 'Imaging equipment saved' for e in events)
        camera.connected = None
    reopened = create_app(MountService(simulation=True))
    with TestClient(reopened) as client:
        assert client.get('/api/pointing/imaging').json()['imaging']['name'] == 'Imager'


def test_simulated_camera_capture_binning_dark_and_abort(tmp_path, monkeypatch):
    monkeypatch.setattr('mount_service.camera.ROOT', tmp_path)
    monkeypatch.setattr('mount_service.camera.dll_path', lambda _: None)
    camera = CameraService()
    monkeypatch.setattr(camera.andor2, 'scan', lambda: [])
    monkeypatch.setattr(camera.andor3, 'scan', lambda: [])
    devices = camera.scan()['devices']
    assert len(devices) == 1 and devices[0]['simulated']
    camera.connect(devices[0]['key'])
    assert camera.connected['pixel_um'] == 3.76
    monkeypatch.setattr('mount_service.camera_simulation.read_catalog',lambda *_:dict(stars=[[50.5,60.5,10.],[500.,400.,12.]],catalog='UCAC4'))
    metadata=dict(SIMRA=3,SIMDEC=20,SIMSCALE=.8,TELESCOP="กล้องทดสอบ")
    camera.connected.update(width=1280,height=960)
    light = camera.capture(.01, binning=5, gain=100, metadata=metadata)
    with fits.open(light['fits']) as hdus:
        assert hdus[0].data.shape == (192, 256)
        assert hdus[0].header['SIMULATE'] and hdus[0].header['XBINNING'] == 5
        assert hdus[0].header['TELESCOP'].isascii()
        from astropy.time import Time
        assert Time(hdus[0].header['DATE-OBS'],scale='utc').isot
        assert np.ptp(hdus[0].data.astype(float)) > 100
    dark = camera.capture(.01, binning=5, dark=True)
    with fits.open(dark['fits']) as hdus:
        assert np.ptp(hdus[0].data.astype(float)) < 100
    timer = threading.Timer(.05, camera.abort)
    timer.start()
    try:
        with pytest.raises(ValueError, match='cancelled'):
            camera.capture(10, metadata=metadata)
    finally:
        timer.join()
    assert not camera.busy
    assert len(list((tmp_path/'python-mount-service/runtime/captures').glob('*.fits'))) == 2
    camera.disconnect()
    assert camera.connected is None


def test_simulated_camera_imaging_setup_can_continue():
    from mount_service.camera import simulated_camera
    app = create_app(MountService(simulation=True))
    with TestClient(app) as client:
        telescope = client.post('/api/pointing/telescopes', json={'name':'Simulation OTA','focal_length_mm':1000}).json()['telescope']
        camera = app.state.observatory.camera
        camera.devices = [simulated_camera()]
        camera.connect('simulation:camera')
        response = client.post('/api/pointing/imaging', json=dict(name='Simulated camera', telescope_id=telescope['id'],
            device_key='simulation:camera', pixel_um=3.76, exposure_s=.125, binning=5, gain=100))
        assert response.status_code == 200, response.text
        assert response.json()['imaging']['vendor'] == 'simulation'
        assert response.json()['imaging']['pixel_scale'] == pytest.approx(206.264806247*3.76*5/1000)
