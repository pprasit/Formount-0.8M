"""Controller-side USB discovery and serialized ZWO, FLI and Andor acquisition."""
import ctypes as c
import threading
import math
import time
from pathlib import Path

import numpy as np
from astropy.io import fits
from PIL import Image

from .astronomy import ROOT
from .camera_native import sdk_path, load_sdk
from .camera_andor import Andor2, Andor3


class ASIInfo(c.Structure):
    _fields_ = [("name", c.c_char*64), ("id", c.c_int), ("height", c.c_int), ("width", c.c_int),
                ("color", c.c_int), ("bayer", c.c_int), ("bins", c.c_int*16), ("formats", c.c_int*8),
                ("pixel_um", c.c_double), ("shutter", c.c_int), ("st4", c.c_int), ("cooler", c.c_int),
                ("usb3host", c.c_int), ("usb3", c.c_int), ("electrons", c.c_float), ("bits", c.c_int),
                ("trigger", c.c_int), ("unused", c.c_byte*16)]


class ASIControlCaps(c.Structure):
    _fields_ = [("name", c.c_char*64), ("description", c.c_char*128),
                ("maximum", c.c_long), ("minimum", c.c_long), ("default", c.c_long),
                ("auto", c.c_int), ("writable", c.c_int), ("kind", c.c_int),
                ("unused", c.c_byte*32)]


def dll_path(name):
    return sdk_path(name)


def simulated_camera():
    return dict(key='simulation:camera', vendor='simulation', name='Simulated camera',
                serial='SIM-001', width=6248, height=4176, pixel_um=3.76,
                pixel_height_um=3.76, bins=list(range(1, 9)), color=False, bits=16,
                simulated=True, gain_supported=True, exposure_min_s=.001, exposure_max_s=600,
                controls={'0': dict(minimum=0, maximum=600, default=100, writable=True)})


class CameraService:
    def __init__(self):
        self.lock = threading.RLock()
        self.cancel = threading.Event()
        self.cancel_lock = threading.Lock()
        self.cancel_epoch = 0
        self.devices = []
        self.errors = {}
        self.connected = None
        self.last = None
        self.asi, self.fli = None, None
        self.device = None
        self.busy = False
        self.video_frame=None;self.video_status=None
        self.video_sequence=0
        self.video_packet=None
        self.acquisition={'phase':'Idle'}
        self.andor2, self.andor3 = Andor2(), Andor3()

    def phase(self, phase, exposure_s=None, fraction=None, estimated=False):
        previous=self.acquisition
        now=time.monotonic()
        value={**previous,'phase':phase,'phase_started':now}
        if exposure_s is not None:value['exposure_s']=exposure_s
        if fraction is not None:value['fraction']=fraction
        if previous.get('phase')=='Readout':value['readout_s']=now-previous['phase_started']
        if phase=='Exposing':value['exposure_started']=now
        if phase=='Readout':value['readout_estimated']=estimated
        self.acquisition=value

    def _bind(self, dll, name, types):
        fn = getattr(dll, name)
        fn.argtypes, fn.restype = types, c.c_int
        return fn

    def _check(self, status, operation):
        if status:
            raise ValueError(f"Camera SDK {operation}: error {status}")

    def scan(self):
        with self.lock:
            if self.busy:
                raise ValueError("Cannot scan while camera is exposing or reading out")
            devices, self.errors = [], {}
            try:
                path = dll_path("ASICamera2.dll")
                if path:
                    self.asi = self.asi or load_sdk("ASICamera2.dll")
                    self._bind(self.asi, "ASIGetNumOfConnectedCameras", [])
                    self._bind(self.asi, "ASIGetCameraProperty", [c.POINTER(ASIInfo), c.c_int])
                    for index in range(self.asi.ASIGetNumOfConnectedCameras()):
                        info = ASIInfo()
                        self._check(self.asi.ASIGetCameraProperty(c.byref(info), index), "get property")
                        devices.append({"key": f"zwo:{info.id}", "vendor": "zwo", "name": info.name.decode(errors="replace"),
                                        "width": info.width, "height": info.height, "bins": [b for b in info.bins if b],
                                        "pixel_um": info.pixel_um, "id": info.id, "color": bool(info.color),
                                        "bayer": info.bayer if info.color else None, "bits": info.bits,
                                        "formats": [v for v in info.formats if v != -1], "gain_supported": True,
                                        "usb3": bool(info.usb3), "trigger": bool(info.trigger)})
                else:
                    self.errors["zwo"] = "ASICamera2.dll not found"
            except Exception as exc:
                self.errors["zwo"] = str(exc)
            try:
                path = dll_path("libfli64.dll")
                if path:
                    self.fli = self.fli or load_sdk("libfli64.dll", True)
                    self._bind(self.fli, "FLIList", [c.c_long, c.POINTER(c.c_void_p)])
                    self._bind(self.fli, "FLIFreeList", [c.c_void_p])
                    names = c.c_void_p()
                    self._check(self.fli.FLIList(0x102, c.byref(names)), "FLI list")
                    try:
                        array = c.cast(names, c.POINTER(c.c_char_p))
                        for i in range(64):
                            if not names or not array[i]:
                                break
                            name, _, model = array[i].decode(errors="replace").partition(";")
                            devices.append({"key": f"fli:{name}", "vendor": "fli", "name": model or name, "filename": name})
                    finally:
                        if names:
                            self.fli.FLIFreeList(names)
                else:
                    self.errors["fli"] = "libfli64.dll not found"
            except Exception as exc:
                self.errors["fli"] = str(exc)
            for name, adapter in (("andor2", self.andor2), ("andor3", self.andor3)):
                try:
                    devices.extend(adapter.scan())
                except Exception as exc:
                    self.errors[name] = str(exc)
            if self.connected:
                devices = [self.connected if d["key"] == self.connected["key"] else d for d in devices]
            if not devices or self.connected and self.connected.get("simulated"):
                devices.append(simulated_camera())
            self.devices = devices
            return self.status()

    def cooling_status(self, enabled=None, target_c=None):
        with self.lock:
            if not self.connected or self.connected.get('sdk') != 'sdk2':
                return dict(supported=False, state='Connect an Andor CCD to control cooling')
            if self.busy:
                raise ValueError('Wait for acquisition to finish before changing cooling')
            result = self.andor2.set_cooling(enabled, target_c) if enabled is not None or target_c is not None else self.andor2.cooling_status()
            self.connected['cooling'] = result
            return result

    def status(self):
        progress=dict(self.acquisition)
        now=time.monotonic()
        progress['elapsed_s']=max(0,now-progress.get('phase_started',now))
        if progress['phase']=='Readout':progress['readout_s']=progress['elapsed_s']
        if progress['phase']=='Exposing':progress['fraction']=min(.99,progress['elapsed_s']/max(.001,progress.get('exposure_s',1)))
        return {"devices": self.devices or [simulated_camera()], "errors": self.errors, "connected": self.connected,
                "last": self.last, "exposing": self.busy, "video":self.video_status,'acquisition':progress}

    def connect(self, key):
        with self.lock:
            if self.busy:
                raise ValueError("Cannot change cameras during exposure/readout")
            if self.connected and self.connected["key"] == key:
                return self.status()
            if not self.devices:
                self.scan()
            device = simulated_camera() if key == "simulation:camera" else next((d for d in self.devices if d["key"] == key), None)
            if not device:
                raise ValueError("Camera not found; scan connected devices first")
            self.disconnect()
            if device.get("simulated"):
                pass
            elif device["vendor"] == "zwo":
                for name in ("ASIOpenCamera", "ASIInitCamera", "ASICloseCamera", "ASIStopExposure"):
                    self._bind(self.asi, name, [c.c_int])
                self._check(self.asi.ASIOpenCamera(device["id"]), "open")
                try:
                    self._check(self.asi.ASIInitCamera(device["id"]), "initialize")
                    if device.get('trigger'):
                        self._bind(self.asi, 'ASISetCameraMode', [c.c_int,c.c_int])
                        self._check(self.asi.ASISetCameraMode(device['id'],0), 'normal snapshot mode')
                    self._bind(self.asi, "ASIGetNumOfControls", [c.c_int,c.POINTER(c.c_int)])
                    self._bind(self.asi, "ASIGetControlCaps", [c.c_int,c.c_int,c.POINTER(ASIControlCaps)])
                    count = c.c_int()
                    self._check(self.asi.ASIGetNumOfControls(device['id'],c.byref(count)), "control count")
                    controls = {}
                    for index in range(count.value):
                        caps = ASIControlCaps()
                        self._check(self.asi.ASIGetControlCaps(device['id'],index,c.byref(caps)), "control capabilities")
                        controls[str(caps.kind)] = dict(minimum=caps.minimum,maximum=caps.maximum,
                                                       default=caps.default,writable=bool(caps.writable),auto=bool(caps.auto),name=caps.name.decode(errors='replace'),description=caps.description.decode(errors='replace'))
                    device['controls'] = controls
                    device['gain_supported'] = bool(controls.get('0',{}).get('writable'))
                    if '1' in controls:
                        device['exposure_min_s'] = controls['1']['minimum']/1e6
                        device['exposure_max_s'] = controls['1']['maximum']/1e6
                except Exception:
                    self.asi.ASICloseCamera(device["id"])
                    raise
            elif device["vendor"] == "andor":
                adapter = self.andor2 if device["sdk"] == "sdk2" else self.andor3
                device = adapter.connect(device)
            else:
                self._bind(self.fli, "FLIOpen", [c.POINTER(c.c_long), c.c_char_p, c.c_long])
                self._bind(self.fli, "FLIClose", [c.c_long])
                self.device = c.c_long(-1)
                self._check(self.fli.FLIOpen(c.byref(self.device), device["filename"].encode(), 0x102), "open")
                try:
                    self._bind(self.fli, "FLIGetVisibleArea", [c.c_long]+[c.POINTER(c.c_long)]*4)
                    bounds = [c.c_long() for _ in range(4)]
                    self._check(self.fli.FLIGetVisibleArea(self.device, *[c.byref(b) for b in bounds]), "visible area")
                    device["bounds"] = [b.value for b in bounds]
                    device["width"], device["height"] = bounds[2].value-bounds[0].value, bounds[3].value-bounds[1].value
                    self._bind(self.fli, "FLIGetPixelSize", [c.c_long, c.POINTER(c.c_double), c.POINTER(c.c_double)])
                    px, py = c.c_double(), c.c_double()
                    self._check(self.fli.FLIGetPixelSize(self.device, c.byref(px), c.byref(py)), "pixel size")
                    # The FLI native API specifies metres, unlike ZWO/Andor's micrometres.
                    device.update(pixel_um=px.value*1e6, pixel_height_um=py.value*1e6,
                                  gain_supported=False, color=False)
                    self._bind(self.fli, "FLIGetSerialString", [c.c_long, c.c_char_p, c.c_size_t])
                    serial = c.create_string_buffer(256)
                    if self.fli.FLIGetSerialString(self.device, serial, len(serial)) == 0:
                        device["serial"] = serial.value.decode(errors="replace")
                except Exception:
                    self.fli.FLIClose(self.device)
                    self.device = None
                    raise
            device['frame_types']=['Light','Dark','Bias'] if device.get('simulated') or device['vendor']=='fli' or device.get('sdk')=='sdk2' and device.get('shutter') else ['Light','Dark'] if device['vendor']=='zwo' else ['Light']
            if device['vendor']=='fli':
                device['exposure_min_s']=0
                modes=[]
                try:
                    self._bind(self.fli,'FLIGetCameraModeString',[c.c_long,c.c_long,c.c_char_p,c.c_size_t])
                    for index in range(32):
                        text=c.create_string_buffer(256)
                        if self.fli.FLIGetCameraModeString(self.device,index,text,len(text))!=0:break
                        modes.append(dict(value=str(index),label=text.value.decode(errors='replace')))
                except AttributeError:pass
                device['readout_options']={'camera_mode':modes} if modes else {}
            device['roi_supported']=device.get('sdk')!='sdk3'
            device['video_supported']=bool(device.get('simulated')) or device['vendor']=='zwo' and all(hasattr(self.asi,n) for n in ('ASIStartVideoCapture','ASIStopVideoCapture','ASIGetVideoData'))
            if device.get('simulated'):device['formats']=[0,2]
            self.connected = device
            return self.status()

    def validate_settings(self, exposure_s, binning, gain):
        d = self.connected
        if not d:
            raise ValueError('Connect a camera first')
        if not math.isfinite(exposure_s) or not .001 <= exposure_s <= 600:
            raise ValueError('Exposure must be between 0.001 and 600 seconds')
        if binning not in range(1, 9) or (d.get('bins') and binning not in d['bins']):
            raise ValueError('Camera does not support this binning')
        if not d.get('exposure_min_s',.001) <= exposure_s <= d.get('exposure_max_s',600):
            raise ValueError('Exposure is outside this camera\'s supported range')
        if not d.get('gain_supported') and gain:
            raise ValueError('This camera does not support the numeric gain control; use gain 0')
        caps = d.get('controls',{}).get('0')
        if caps and not caps['minimum'] <= gain <= caps['maximum']:
            raise ValueError('Gain is outside this camera\'s supported range')

    def video_capture(self,exposure_s,binning,gain,roi,frames,epoch,record=True,image_format='RAW16',preview_fps=15,video_controls=None,auto_exposure=False,auto_gain=False):
        """Serialized native acquisition; preview holds only the newest JPEG, never a queue."""
        import io
        from concurrent.futures import ProcessPoolExecutor
        from .camera_video_worker import encode_preview,simulated_frame,lower_priority
        with self.lock:
            self.busy=True;self.video_frame=None;self.video_packet=None
            started=False;pool=None;pending=None
            try:
                if epoch!=self.cancel_epoch or self.cancel.is_set():raise ValueError('Video cancelled before acquisition')
                self.validate_settings(exposure_s,binning,gain)
                d=self.connected
                if not d.get('video_supported'):raise ValueError('Camera does not support video')
                if image_format not in ('RAW8','RAW16'):raise ValueError('Unsupported video format')
                if not 1<=preview_fps<=30:raise ValueError('Preview FPS must be 1 to 30')
                fmt=0 if image_format=='RAW8' else 2
                if fmt not in d['formats']:raise ValueError('Camera does not support '+image_format)
                region=self.validate_roi(roi,binning)
                w,h=region['width']//binning,region['height']//binning
                w-=w%8;h-=h%2
                if w and 'ASI120' in d['name'].upper() and not d.get('usb3'):
                    quantum=math.lcm(2,1024//math.gcd(w,1024));h-=h%quantum
                dtype=np.uint8 if fmt==0 else np.uint16
                byte_count=w*h*np.dtype(dtype).itemsize
                if not w or not h or byte_count>256*1024*1024 or record and (not 1<=frames<=1000 or frames*byte_count>256*1024*1024):raise ValueError('Video exceeds 256 MiB frame budget; reduce ROI, use binning, or reduce frame count')
                extra=video_controls or {}
                # Only known imaging controls; never accept arbitrary SDK control IDs.
                allowed={2,3,4,5,6,9,10,11,12,13,14,18}
                for key,enabled in ((1,auto_exposure),(0,auto_gain)):
                    if enabled and not d.get('controls',{}).get(str(key),{}).get('auto'):raise ValueError('Camera does not support automatic exposure/gain')
                for key,value in extra.items():
                    caps=d.get('controls',{}).get(str(key),{})
                    if int(key) not in allowed or not caps.get('writable') or type(value)!=int or not caps['minimum']<=value<=caps['maximum']:raise ValueError('Unsupported video control or value')
                if not d.get('simulated'):
                    for name,args in {'ASISetROIFormat':[c.c_int]*5,'ASISetStartPos':[c.c_int]*3,'ASIGetStartPos':[c.c_int,c.POINTER(c.c_int),c.POINTER(c.c_int)],'ASISetControlValue':[c.c_int,c.c_int,c.c_long,c.c_int],'ASIStartVideoCapture':[c.c_int],'ASIStopVideoCapture':[c.c_int],'ASIGetVideoData':[c.c_int,c.c_void_p,c.c_long,c.c_int]}.items():self._bind(self.asi,name,args)
                    self._check(self.asi.ASISetROIFormat(d['id'],w,h,binning,fmt),'video ROI')
                    self._check(self.asi.ASISetStartPos(d['id'],region['x']//binning,region['y']//binning),'video origin')
                    rx,ry=c.c_int(),c.c_int()
                    self._check(self.asi.ASIGetStartPos(d['id'],c.byref(rx),c.byref(ry)),'video origin readback')
                    region['x'],region['y']=rx.value*binning,ry.value*binning
                    for key,value in {0:gain,1:int(exposure_s*1e6),**{int(k):v for k,v in extra.items()}}.items():
                        self._check(self.asi.ASISetControlValue(d['id'],key,value,int(auto_exposure if key==1 else auto_gain if key==0 else False)),'video control '+str(key))
                    self._check(self.asi.ASIStartVideoCapture(d['id']),'start video');started=True
                began=time.time();clock=time.monotonic();count=0;last_preview=-1;preview_count=0;next_frame=clock
                cube=np.empty((frames,h,w),dtype=dtype) if record else None
                frame=np.empty((h,w),dtype=dtype)
                self.phase('Streaming')
                self.video_status={'recording':True,'saving':record,'frames':0,'target_frames':frames if record else None,'width':w,'height':h,'format':image_format,'fps':0,'preview_fps':0,'dropped_frames':0 if d.get('simulated') else None,'simulated':bool(d.get('simulated'))}
                pool=ProcessPoolExecutor(max_workers=1,initializer=lower_priority)
                def publish_preview(future):
                    nonlocal preview_count
                    data,peak,saturated=future.result()
                    self.video_frame=data;self.video_sequence+=1;self.video_packet=(self.video_sequence,data)
                    preview_count+=1
                    self.video_status.update(preview_fps=preview_count/max(.001,time.monotonic()-clock),preview_sequence=self.video_sequence,peak_percent=peak,saturated_percent=saturated)
                while (not record or count<frames) and not self.cancel.is_set():
                    if d.get('simulated'):
                        next_frame=max(next_frame+max(exposure_s,1/30),time.monotonic())
                        if self.cancel.wait(max(0,next_frame-time.monotonic())):break
                        frame[:]=pool.submit(simulated_frame,h,w,count,gain,exposure_s,fmt).result()
                    else:
                        deadline=time.monotonic()+(600 if auto_exposure else exposure_s+30)
                        while True:
                            if self.cancel.is_set():break
                            status=self.asi.ASIGetVideoData(d['id'],frame.ctypes.data,frame.nbytes,200)
                            if status==0:break
                            if status!=11:self._check(status,'video frame')
                            if time.monotonic()>deadline:raise ValueError('Video frame timed out')
                        if self.cancel.is_set():break
                    if record:cube[count]=frame
                    count+=1;elapsed=max(.001,time.monotonic()-clock)
                    self.video_status.update(frames=count,elapsed_s=elapsed,fps=count/elapsed)
                    # Encoding cannot block SDK reads; at most one pending preview, no frame backlog.
                    if pending is not None and pending.done():publish_preview(pending);pending=None
                    slot=int((time.monotonic()-clock)*min(preview_fps,3))
                    if slot>last_preview and pending is None:
                        pending=pool.submit(encode_preview,frame.copy());last_preview=slot
                        if not d.get('simulated') and hasattr(self.asi,'ASIGetDroppedFrames'):
                            dropped=c.c_int();self._bind(self.asi,'ASIGetDroppedFrames',[c.c_int,c.POINTER(c.c_int)])
                            if self.asi.ASIGetDroppedFrames(d['id'],c.byref(dropped))==0:self.video_status['dropped_frames']=dropped.value
                if pending is not None:publish_preview(pending)
                if started:self.asi.ASIStopVideoCapture(d['id']);started=False
                if record and count:
                    self.video_status['finishing']=True
                    directory=ROOT/'python-mount-service/runtime/captures';directory.mkdir(parents=True,exist_ok=True)
                    filename=directory/f'video-{time.time_ns()}.fits'
                    header=fits.Header({'SETEXPT' if auto_exposure else 'EXPTIME':exposure_s,'XBINNING':binning,'YBINNING':binning,'GAINSET' if auto_gain else 'GAIN':gain,'AUTOEXP':auto_exposure,'AUTOGAIN':auto_gain,'INSTRUME':d['name'],'VIDSTART':began,'NFRAMES':count,'READFMT':image_format,'SIMULATE':bool(d.get('simulated')),'XORGSUBF':region['x'],'YORGSUBF':region['y']})
                    for key,value in extra.items():header['HIERARCH ASI CONTROL '+str(key)]=value
                    fits.writeto(filename,cube[:count],header)
                    self.video_status.update(filename=filename.name,fits=str(filename))
                self.video_status.update(recording=False,finishing=False,cancelled=self.cancel.is_set())
                self.phase('Stopped' if self.cancel.is_set() else 'Complete')
                return self.video_status
            except Exception as exc:
                self.video_status={'recording':False,'error':str(exc)}
                self.phase('Cancelled' if self.cancel.is_set() else 'Error')
                raise
            finally:
                if started:self.asi.ASIStopVideoCapture(self.connected['id'])
                if pool is not None:pool.shutdown(wait=True,cancel_futures=True)
                self.busy=False

    def validate_roi(self,roi,binning):
        d=self.connected
        if not d:raise ValueError('Connect a camera first')
        region=roi or dict(x=0,y=0,width=d['width']//binning*binning,height=d['height']//binning*binning)
        if roi and d.get('sdk')=='sdk3':raise ValueError('Native ROI is not available in this Andor SDK3 adapter')
        if set(region)!={'x','y','width','height'} or any(type(v)!=int for v in region.values()):raise ValueError('ROI must contain integer x, y, width and height')
        if region['x']<0 or region['y']<0 or region['width']<binning or region['height']<binning or region['x']+region['width']>d['width'] or region['y']+region['height']>d['height']:raise ValueError('ROI exceeds sensor bounds')
        if any(v%binning for v in region.values()):raise ValueError('ROI coordinates and dimensions must be multiples of binning')
        return region

    def disconnect(self):
        with self.lock:
            if self.connected:
                if self.connected.get("simulated"):
                    pass
                elif self.connected["vendor"] == "zwo":
                    self.asi.ASICloseCamera(self.connected["id"])
                elif self.connected["vendor"] == "andor":
                    (self.andor2 if self.connected["sdk"] == "sdk2" else self.andor3).disconnect()
                else:
                    self._check(self.fli.FLIClose(self.device), "close")
                    self.device = None
            self.connected = None
            self.last = None

    def close(self):
        self.disconnect()
        self.andor3.close()

    def abort(self):
        with self.cancel_lock:
            self.cancel_epoch += 1
            self.cancel.set()

    def prepare_capture(self):
        with self.cancel_lock:
            self.cancel.clear()
            self.acquisition={'phase':'Preparing','phase_started':time.monotonic()}
            return self.cancel_epoch

    def capture(self, exposure_s=1, binning=1, gain=0, dark=False, metadata=None, epoch=None, roi=None, frame_type=None, readout=None):
        if epoch is None:
            epoch = self.prepare_capture()
        self.busy = True
        try:
            result=self._capture(exposure_s, binning, gain, dark, metadata, epoch,roi,frame_type,readout)
            self.phase('Complete',fraction=1)
            return result
        except Exception as exc:
            self.phase('Cancelled' if self.cancel.is_set() else 'Error',fraction=0)
            self.acquisition['error']=str(exc)
            raise
        finally:
            self.busy = False

    def _capture(self, exposure_s=1, binning=1, gain=0, dark=False, metadata=None, epoch=None, roi=None, frame_type=None, readout=None):
        with self.lock:
            if epoch != self.cancel_epoch or self.cancel.is_set():
                raise ValueError('Exposure cancelled before acquisition started')
            if not self.connected:
                raise ValueError("Connect a camera first")
            d=self.connected
            frame_type=frame_type or ('Dark' if dark else 'Light')
            if frame_type not in d.get('frame_types',['Light','Dark']):raise ValueError('Frame type is not supported by this camera')
            dark=frame_type!='Light'
            if frame_type=='Bias':exposure_s=0
            readout=readout or {}
            for key,value in readout.items():
                if value not in [str(o['value']) for o in d.get('readout_options',{}).get(key,[])]:raise ValueError('Readout option is not supported by this camera')
            self.validate_settings(max(.001,d.get('exposure_min_s',.001)) if frame_type=='Bias' else exposure_s,binning,gain)
            if not math.isfinite(exposure_s) or not (0 if frame_type=='Bias' else .001) <= exposure_s <= 600 or binning not in range(1, 9):
                raise ValueError("Exposure/binning outside supported range")
            d = self.connected
            if readout.get('read_mode')=='0' and roi:raise ValueError('FVB uses the full sensor; disable ROI')
            region=self.validate_roi(roi,binning)
            ox,oy=region["x"],region["y"]
            w, h = region["width"]//binning, region["height"]//binning
            if w*h <= 0 or w*h > 100_000_000:
                raise ValueError("Frame exceeds memory budget; use binning")
            image = np.zeros((h, w), dtype=np.uint16)
            began = time.time()
            simulated_stars = 0
            if d.get("simulated"):
                from .camera_simulation import render_sensor
                began = (metadata or {}).get('SIMSTART', began)
                image, simulated_stars = render_sensor(d, exposure_s, binning, gain, dark, metadata, self.cancel,self.phase)
                image=image[oy//binning:oy//binning+h,ox//binning:ox//binning+w]
                if roi:simulated_stars=None
            elif d["vendor"] == "zwo":
                if binning not in d["bins"]:
                    raise ValueError("Camera does not support this binning")
                if 2 not in d["formats"]:
                    raise ValueError("ZWO camera does not support RAW16")
                # ASI ROI dimensions after binning must be multiples of 8 x 2.
                w, h = w - w % 8, h - h % 2
                if 'ASI120' in d['name'].upper() and not d.get('usb3'):
                    quantum = math.lcm(2,1024//math.gcd(w,1024))
                    h -= h % quantum
                if not w or not h:
                    raise ValueError("Binned ZWO ROI is too small")
                image = np.zeros((h, w), dtype=np.uint16)
                signatures = {"ASISetROIFormat": [c.c_int]*5, "ASISetControlValue": [c.c_int,c.c_int,c.c_long,c.c_int],
                              "ASIStartExposure": [c.c_int]*2, "ASIGetExpStatus": [c.c_int,c.POINTER(c.c_int)],
                              "ASIGetDataAfterExp": [c.c_int,c.c_void_p,c.c_long],
                              "ASISetStartPos": [c.c_int]*3,"ASIGetStartPos":[c.c_int,c.POINTER(c.c_int),c.POINTER(c.c_int)]}
                for name, args in signatures.items(): self._bind(self.asi, name, args)
                self._check(self.asi.ASISetROIFormat(d["id"],w,h,binning,2), "RAW16 ROI")
                self._check(self.asi.ASISetStartPos(d["id"],ox//binning,oy//binning), "ROI origin")
                rx,ry=c.c_int(),c.c_int()
                self._check(self.asi.ASIGetStartPos(d["id"],c.byref(rx),c.byref(ry)),"ROI readback")
                ox,oy=rx.value*binning,ry.value*binning
                self._check(self.asi.ASISetControlValue(d["id"],0,gain,0), "gain")
                self._check(self.asi.ASISetControlValue(d["id"],1,int(exposure_s*1e6),0), "exposure")
                began = time.time()
                deadline=time.monotonic()+exposure_s+60
                self._check(self.asi.ASIStartExposure(d["id"],int(dark)), "start exposure")
                self.phase('Exposing',exposure_s)
                try:
                    while True:
                        status = c.c_int()
                        self._check(self.asi.ASIGetExpStatus(d["id"], c.byref(status)), "exposure status")
                        if self.cancel.is_set(): raise ValueError("Exposure cancelled")
                        if status.value == 2: break
                        if status.value in (0,3): raise ValueError("Camera exposure failed / returned idle")
                        if self.cancel.is_set() or time.monotonic()>deadline: raise ValueError("Exposure cancelled/timed out")
                        time.sleep(.05)
                    self.phase('Readout')
                    self._check(self.asi.ASIGetDataAfterExp(d["id"],image.ctypes.data,image.nbytes), "read frame")
                finally:
                    self.asi.ASIStopExposure(d["id"])
            elif d["vendor"] == "andor":
                adapter = self.andor2 if d["sdk"] == "sdk2" else self.andor3
                image, exposure_s, began = adapter.capture(d, exposure_s, binning, gain, dark, self.cancel,roi=None if readout.get('read_mode')=='0' else region,readout=readout,progress=self.phase) if d["sdk"]=="sdk2" else adapter.capture(d,exposure_s,binning,gain,dark,self.cancel,readout=readout,progress=self.phase)
                h, w = image.shape
            else:
                if gain:
                    raise ValueError("FLI CCD does not support digital gain; use gain 0")
                args = {"FLISetExposureTime":[c.c_long,c.c_long], "FLISetHBin":[c.c_long,c.c_long],
                        "FLISetVBin":[c.c_long,c.c_long], "FLISetFrameType":[c.c_long,c.c_long],
                        "FLISetImageArea":[c.c_long]*5, "FLIExposeFrame":[c.c_long],
                        "FLIGetExposureStatus":[c.c_long,c.POINTER(c.c_long)], "FLICancelExposure":[c.c_long],
                        "FLIGrabRow":[c.c_long,c.c_void_p,c.c_size_t], "FLISetBitDepth":[c.c_long,c.c_long]}
                for name, types in args.items(): self._bind(self.fli,name,types)
                if 'camera_mode' in readout:
                    self._bind(self.fli,'FLISetCameraMode',[c.c_long,c.c_long])
                    self._check(self.fli.FLISetCameraMode(self.device,int(readout['camera_mode'])), 'readout mode')
                self._check(self.fli.FLISetBitDepth(self.device,1), "16-bit readout")
                x,y,_,_=d["bounds"];x+=ox;y+=oy
                for name, values in (("FLISetHBin",[binning]),("FLISetVBin",[binning]),("FLISetFrameType",[int(dark)]),
                                     ("FLISetImageArea",[x,y,x+w*binning,y+h*binning]),("FLISetExposureTime",[int(exposure_s*1000)])):
                    self._check(getattr(self.fli,name)(self.device,*values),name)
                began = time.time()
                deadline=time.monotonic()+exposure_s+60
                self._check(self.fli.FLIExposeFrame(self.device),"expose")
                self.phase('Exposing',exposure_s)
                completed = False
                try:
                    while True:
                        remaining=c.c_long();self._check(self.fli.FLIGetExposureStatus(self.device,c.byref(remaining)),"status")
                        if self.cancel.is_set(): raise ValueError("Exposure cancelled")
                        if remaining.value<=0: break
                        if self.cancel.is_set() or time.monotonic()>deadline: raise ValueError("Exposure cancelled/timed out")
                        time.sleep(.05)
                    self.phase('Readout')
                    for row in image:
                        if self.cancel.is_set(): raise ValueError("Readout cancelled")
                        self._check(self.fli.FLIGrabRow(self.device,row.ctypes.data,w),"read row")
                    completed = True
                finally:
                    if not completed: self.fli.FLICancelExposure(self.device)
            if self.cancel.is_set():
                raise ValueError("Capture cancelled before saving")
            self.phase('Processing',fraction=.99)
            directory=ROOT/"python-mount-service/runtime/captures";directory.mkdir(parents=True,exist_ok=True)
            from datetime import datetime,timezone
            stamp=datetime.fromtimestamp(began,timezone.utc).isoformat().replace("+00:00","Z")
            filename=directory/f"capture-{time.time_ns()}.fits"
            header=fits.Header({"DATE-OBS":stamp,"EXPTIME":exposure_s,"XBINNING":binning,"YBINNING":binning,"INSTRUME":d["name"],"IMAGETYP":frame_type,
                                "CAMVEND":d["vendor"],"READFMT":"RAW16"})
            header["XORGSUBF"]=ox;header["YORGSUBF"]=oy
            if d.get("serial"): header["CAM-SER"]=d["serial"]
            if d.get("pixel_um"): header["XPIXSZ"]=d["pixel_um"]
            if d.get("pixel_height_um",d.get("pixel_um")): header["YPIXSZ"]=d.get("pixel_height_um",d["pixel_um"])
            if d.get("color") and binning==1: header["BAYERPAT"]=["RGGB","BGGR","GRBG","GBRG"][d["bayer"]]
            if d.get("gain_supported"): header["GAIN"]=gain
            for key,value in (metadata or {}).items():
                # FITS headers are ASCII; retain non-ASCII names as reversible Unicode escapes.
                header[key]=value.encode("ascii",errors="backslashreplace").decode() if isinstance(value,str) else value
            header["SIMULATE"] = bool(d.get("simulated"))
            if d.get("simulated"):
                header["SIMCAT"] = "UCAC4" if not dark else "Dark"
                if simulated_stars is not None:header["SIMSTARS"] = simulated_stars
            for key,value in readout.items():
                header['HIERARCH READOUT '+key]=value
            fits.writeto(filename,image,header)
            from .camera_preview import save_preview
            preview=filename.with_suffix('.png')
            preview_settings=save_preview(image,preview)
            self.last={"fits":str(filename),"preview":str(preview),"filename":filename.name,"width":w,"height":h,
                       "frame_type":frame_type,"readout":readout,"roi":dict(x=ox,y=oy,width=w*binning,height=h*binning),"capture_utc":stamp,"exposure_s":exposure_s,"binning":binning,"simulated":bool(d.get("simulated")),"simulated_stars":simulated_stars,"preview_settings":preview_settings}
            return self.last
