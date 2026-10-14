from test_camera_sdk import zwo,SDK,put
import ctypes as c
import numpy as np
import pytest
from astropy.io import fits

def test_native_roi_readback_is_written_to_fits(tmp_path,monkeypatch):
    camera=zwo(tmp_path,monkeypatch)
    camera.asi.functions['ASIGetStartPos']=lambda _id,x,y:(put(x,2),put(y,0),0)[-1]
    result=camera.capture(.01,1,5,roi=dict(x=2,y=0,width=8,height=4))
    image,header=fits.getdata(result['fits'],header=True)
    assert image.shape==(4,8)
    assert ('ASISetROIFormat',(7,8,4,1,2)) in camera.asi.calls
    assert ('ASISetStartPos',(7,2,0)) in camera.asi.calls
    assert header['XORGSUBF']==2
    assert result['roi']==dict(x=2,y=0,width=8,height=4)

def test_roi_invalid_is_rejected_before_sdk_commands(tmp_path,monkeypatch):
    camera=zwo(tmp_path,monkeypatch)
    with pytest.raises(ValueError):camera.capture(.01,2,5,roi=dict(x=1,y=0,width=16,height=4))
    assert not camera.asi.calls

def test_native_video_reads_raw16_frames_and_stops(tmp_path,monkeypatch):
    camera=zwo(tmp_path,monkeypatch);camera.connected['video_supported']=True
    def read(_id,pointer,size,wait):
        data=np.arange(size//2,dtype=np.uint16);c.memmove(pointer,data.ctypes.data,size);return 0
    camera.asi.functions['ASIGetVideoData']=read
    result=camera.video_capture(.01,1,5,dict(x=0,y=0,width=8,height=4),3,camera.prepare_capture())
    cube=fits.getdata(result['fits']);assert cube.shape==(3,4,8)
    assert result['frames']==3 and not result['recording'] and not camera.busy
    assert ('ASIStartVideoCapture',(7,)) in camera.asi.calls
    assert ('ASIStopVideoCapture',(7,)) in camera.asi.calls
    assert camera.video_frame.startswith(b'\xff\xd8')

def test_video_memory_budget_prevents_start(tmp_path,monkeypatch):
    camera=zwo(tmp_path,monkeypatch);camera.connected.update(video_supported=True,width=10000,height=10000)
    with pytest.raises(ValueError):camera.video_capture(.01,1,5,None,100,camera.prepare_capture())
    assert not camera.asi.calls and not camera.busy


def test_video_abort_stops_sdk_and_keeps_completed_frames(tmp_path,monkeypatch):
    camera=zwo(tmp_path,monkeypatch);camera.connected['video_supported']=True
    calls=0
    def read(_id,pointer,size,wait):
        nonlocal calls
        calls+=1
        if calls==2:camera.cancel.set();return 11
        data=np.arange(size//2,dtype=np.uint16);c.memmove(pointer,data.ctypes.data,size);return 0
    camera.asi.functions['ASIGetVideoData']=read
    result=camera.video_capture(.01,1,5,dict(x=0,y=0,width=8,height=4),10,camera.prepare_capture())
    assert result['cancelled'] and result['frames']==1
    assert fits.getdata(result['fits']).shape==(1,4,8)
    assert not camera.busy and ('ASIStopVideoCapture',(7,)) in camera.asi.calls


def test_fli_bias_and_readout_use_native_sdk_and_fits(tmp_path,monkeypatch):
    import mount_service.camera as module
    from mount_service.camera import CameraService
    monkeypatch.setattr(module,'ROOT',tmp_path)
    camera=CameraService();camera.device=c.c_long(23)
    camera.connected=dict(key='fli:test',vendor='fli',name='FLI Test',width=8,height=4,bounds=[0,0,8,4],frame_types=['Light','Dark','Bias'],readout_options={'camera_mode':[dict(value='1',label='Slow')]})
    camera.fli=SDK(FLIGetExposureStatus=lambda handle,p:(put(p,0) or 0))
    result=camera.capture(10,frame_type='Bias',readout={'camera_mode':'1'})
    assert ('FLISetExposureTime',(camera.device,0)) in camera.fli.calls
    assert ('FLISetFrameType',(camera.device,1)) in camera.fli.calls
    assert ('FLISetCameraMode',(camera.device,1)) in camera.fli.calls
    header=fits.getheader(result['fits'])
    assert header['IMAGETYP']=='Bias' and header['EXPTIME']==0
    camera.fli.calls.clear()
    with pytest.raises(ValueError,match='Readout'):camera.capture(.01,readout={'camera_mode':'999'})
    assert not camera.fli.calls


def test_andor2_readout_selection_maps_adc_output_and_shift_speed():
    from mount_service.camera_andor import Andor2
    adapter=Andor2();adapter.dll=SDK(default=20002)
    adapter.configure_readout({'horizontal_speed':'1:0:2','vertical_speed':'3'})
    assert adapter.dll.calls==[('SetADChannel',(1,)),('SetOutputAmplifier',(0,)),('SetHSSpeed',(0,2)),('SetVSSpeed',(3,))]


def test_capture_progress_tracks_exposure_readout_and_completion(tmp_path,monkeypatch):
    camera=zwo(tmp_path,monkeypatch);phases=[]
    original=camera.phase
    def phase(name,*args,**kwargs):phases.append(name);original(name,*args,**kwargs)
    camera.phase=phase
    result=camera.capture(.01,1,5,roi=dict(x=0,y=0,width=8,height=4))
    assert phases==['Exposing','Readout','Processing','Complete']
    assert camera.status()['acquisition']['fraction']==1
    assert camera.status()['acquisition']['readout_s']>=0
    assert result['preview']


def test_capture_failure_never_reports_complete(tmp_path,monkeypatch):
    camera=zwo(tmp_path,monkeypatch)
    camera.asi.functions['ASIGetDataAfterExp']=lambda *args:12
    with pytest.raises(ValueError):camera.capture(.01,1,5)
    assert camera.status()['acquisition']['phase']=='Error'
    assert camera.status()['acquisition']['fraction']==0
    assert not camera.busy


def test_simulation_video_raw8_record_and_live_abort(tmp_path,monkeypatch):
    import mount_service.camera as module
    from mount_service.camera import CameraService
    import threading,time
    monkeypatch.setattr(module,'ROOT',tmp_path)
    camera=CameraService();camera.connect('simulation:camera')
    assert camera.connected['video_supported']
    region=dict(x=0,y=0,width=64,height=48)
    result=camera.video_capture(.001,1,100,region,3,camera.prepare_capture(),image_format='RAW8')
    cube,header=fits.getdata(result['fits'],header=True)
    assert cube.dtype==np.uint8 and cube.shape==(3,48,64) and header['SIMULATE']
    assert result['fps']>0 and result['dropped_frames']==0
    epoch=camera.prepare_capture();result={}
    thread=threading.Thread(target=lambda:result.update(camera.video_capture(.001,1,100,region,1,epoch,record=False,preview_fps=30)))
    thread.start();deadline=time.monotonic()+3
    while (camera.video_status.get('frames',0)<5 or not camera.busy) and time.monotonic()<deadline:time.sleep(.01)
    first=camera.video_packet
    assert camera.busy and camera.video_status['frames']>=5
    time.sleep(.08);assert camera.video_packet[0]>first[0] and camera.video_packet[1]!=first[1]
    camera.abort();thread.join(3)
    assert not thread.is_alive() and result['cancelled'] and 'fits' not in result
    assert camera.status()['acquisition']['phase']=='Stopped'


def test_video_controls_raw8_and_capability_validation(tmp_path,monkeypatch):
    camera=zwo(tmp_path,monkeypatch);camera.connected.update(video_supported=True,formats=[0,2],controls={'6':dict(minimum=40,maximum=100,writable=True)})
    camera.asi.functions['ASIGetVideoData']=lambda _id,pointer,size,wait:(c.memset(pointer,100,size),0)[-1]
    result=camera.video_capture(.01,1,5,dict(x=0,y=0,width=8,height=4),2,camera.prepare_capture(),image_format='RAW8',video_controls={'6':80})
    assert ('ASISetControlValue',(7,6,80,0)) in camera.asi.calls
    assert ('ASISetROIFormat',(7,8,4,1,0)) in camera.asi.calls
    assert fits.getdata(result['fits']).dtype==np.uint8
    for control in ({'6':101},{'16':-40},{'14':1}):
        camera.asi.calls.clear()
        with pytest.raises(ValueError,match='control'):camera.video_capture(.01,1,5,None,1,camera.prepare_capture(),video_controls=control)
        assert not camera.asi.calls


def test_zwo_auto_video_uses_sdk_auto_flags_and_honest_metadata(tmp_path,monkeypatch):
    camera=zwo(tmp_path,monkeypatch);camera.connected.update(video_supported=True,controls={'0':dict(auto=True,minimum=0,maximum=600),'1':dict(auto=True)})
    camera.asi.functions['ASIGetVideoData']=lambda _id,pointer,size,wait:(c.memset(pointer,0,size),0)[-1]
    result=camera.video_capture(.01,1,5,dict(x=0,y=0,width=8,height=4),1,camera.prepare_capture(),auto_exposure=True,auto_gain=True)
    assert ('ASISetControlValue',(7,1,10000,1)) in camera.asi.calls
    assert ('ASISetControlValue',(7,0,5,1)) in camera.asi.calls
    header=fits.getheader(result['fits'])
    assert header['AUTOEXP'] and header['AUTOGAIN'] and 'EXPTIME' not in header
    assert header['SETEXPT']==.01 and header['GAINSET']==5
    camera.connected['controls']['1']['auto']=False;camera.asi.calls.clear()
    with pytest.raises(ValueError,match='automatic'):camera.video_capture(.01,1,5,None,1,camera.prepare_capture(),auto_exposure=True)
    assert not camera.asi.calls


def test_video_websocket_has_bounded_credits_and_sends_latest():
    import threading,time
    from fastapi.testclient import TestClient
    from mount_service.api import create_app
    from mount_service.service import MountService
    app=create_app(MountService(simulation=True));stop=threading.Event()
    camera=app.state.observatory.camera
    def produce():
        sequence=1
        while not stop.is_set():
            camera.video_packet=(sequence,b'JPEG')
            sequence+=1;time.sleep(.01)
    with TestClient(app) as client:
        worker=threading.Thread(target=produce);worker.start()
        try:
            with client.websocket_connect('/ws/camera/video') as stream:
                frames=[]
                for index in range(3):
                    frame=stream.receive_bytes();frames.append(frame)
                    if index<2:stream.send_text(str(int.from_bytes(frame[:4],'big')))
                numbers=[int.from_bytes(frame[:4],'big') for frame in frames]
                assert numbers==sorted(set(numbers)) and all(frame[4:]==b'JPEG' for frame in frames)
                time.sleep(.12)
                stream.send_text(str(numbers[-1]))
                latest=int.from_bytes(stream.receive_bytes()[:4],'big')
                assert latest>=numbers[-1]+8, 'Do not queue old frames while waiting for credits'
        finally:stop.set();worker.join(2)


def test_video_websocket_rejects_cross_origin():
    from fastapi.testclient import TestClient
    from starlette.websockets import WebSocketDisconnect
    from mount_service.api import create_app
    from mount_service.service import MountService
    with TestClient(create_app(MountService(simulation=True))) as client:
        with pytest.raises(WebSocketDisconnect):
            with client.websocket_connect('/ws/camera/video',headers={'origin':'https://unrelated.example'}):pass


def test_video_delivery_fps_paces_latest_frames_without_changing_camera():
    import threading,time
    from fastapi.testclient import TestClient
    from mount_service.api import create_app
    from mount_service.service import MountService
    app=create_app(MountService(simulation=True));stop=threading.Event()
    camera=app.state.observatory.camera
    def produce():
        sequence=1
        while not stop.is_set():
            camera.video_packet=(sequence,b'JPEG');sequence+=1;time.sleep(.01)
    with TestClient(app) as client:
        worker=threading.Thread(target=produce);worker.start()
        try:
            with client.websocket_connect('/ws/camera/video') as stream:
                stream.send_text('fps:3')
                first=stream.receive_bytes();stream.send_text(str(int.from_bytes(first[:4],'big')))
                second=stream.receive_bytes();stream.send_text(str(int.from_bytes(second[:4],'big')))
                began=time.monotonic();third=stream.receive_bytes()
                assert time.monotonic()-began>=.15
                assert int.from_bytes(third[:4],'big')-int.from_bytes(second[:4],'big')>=10
                assert third[4:]==b'JPEG'
        finally:stop.set();worker.join(2)


def test_video_byte_budget_keeps_image_quality_and_bounds_bandwidth():
    import threading,time
    from fastapi.testclient import TestClient
    from mount_service.api import create_app
    from mount_service.service import MountService
    app=create_app(MountService(simulation=True));stop=threading.Event()
    camera=app.state.observatory.camera
    jpeg=b'JPEG'+b'x'*1020
    def produce():
        sequence=1
        while not stop.is_set():
            camera.video_packet=(sequence,jpeg);sequence+=1;time.sleep(.01)
    with TestClient(app) as client:
        probe=client.get('/api/camera/video/bandwidth-probe')
        assert len(probe.content)==65536 and probe.headers['cache-control']=='no-store'
        worker=threading.Thread(target=produce);worker.start()
        try:
            with client.websocket_connect('/ws/camera/video') as stream:
                stream.send_text('fps:30');stream.send_text('budget:4096')
                for _ in range(2):
                    frame=stream.receive_bytes();stream.send_text(str(int.from_bytes(frame[:4],'big')))
                began=time.monotonic();frame=stream.receive_bytes()
                assert time.monotonic()-began>=.20
                assert frame[4:]==jpeg, 'Bandwidth adaptation must not alter JPEG quality'
        finally:stop.set();worker.join(2)
