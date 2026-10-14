"""Andor single-frame acquisition, following the SDK2 and SDK3 vendor manuals.

Calls run exclusively on Observatory.camera_actor. Cooling, EM gain and external
shutter timing are deliberately left unchanged; they are model-specific settings.
"""
import ctypes as c
import os
import time

import numpy as np

from .camera_native import bind, load_sdk, sdk_path


class AndorCapabilities(c.Structure):
    _fields_ = [(name, c.c_uint32) for name in
                ('size', 'acquisition', 'read', 'trigger', 'camera_type', 'pixel',
                 'set', 'get', 'features', 'pci', 'em_gain', 'frame_transfer_read')]


class Andor2:
    def __init__(self):
        self.dll = None
        self.handle = None
        self.cooling_target = None

    def call(self, name, types, *args):
        code = bind(self.dll, name, types, c.c_uint)(*args)
        if code != 20002:  # DRV_SUCCESS; unlike ZWO and FLI this is nonzero.
            raise ValueError(f'Andor SDK2 {name}: error {code}')

    def scan(self):
        if self.dll is None:
            self.dll = load_sdk('atmcd64d.dll' if c.sizeof(c.c_void_p) == 8 else 'atmcd32d.dll', True)
        count = c.c_int32()
        self.call('GetAvailableCameras', [c.POINTER(c.c_int32)], c.byref(count))
        devices = []
        for index in range(count.value):
            handle = c.c_int32()
            self.call('GetCameraHandle', [c.c_int32, c.POINTER(c.c_int32)], index, c.byref(handle))
            devices.append(dict(key=f'andor2:{handle.value}', vendor='andor', sdk='sdk2',
                                name=f'Andor CCD / EMCCD · camera {index + 1}', id=handle.value))
        return devices

    def connect(self, device):
        self.call('SetCurrentCamera', [c.c_int32], device['id'])
        path = sdk_path('atmcd64d.dll' if c.sizeof(c.c_void_p) == 8 else 'atmcd32d.dll')
        config = os.environ.get('NARIT_ANDOR2_CONFIG_DIR', str(path.parent))
        self.call('Initialize', [c.c_char_p], os.fsencode(config))
        self.handle = device['id']
        try:
            width, height = c.c_int(), c.c_int()
            self.call('GetDetector', [c.POINTER(c.c_int)] * 2, c.byref(width), c.byref(height))
            model = c.create_string_buffer(256)
            self.call('GetHeadModel', [c.c_char_p], model)
            serial = c.c_int()
            self.call('GetCameraSerialNumber', [c.POINTER(c.c_int)], c.byref(serial))
            px, py = c.c_float(), c.c_float()
            self.call('GetPixelSize', [c.POINTER(c.c_float)] * 2, c.byref(px), c.byref(py))
            maxima = []
            for axis in (0, 1):
                maximum = c.c_int()
                self.call('GetMaximumBinning', [c.c_int, c.c_int, c.POINTER(c.c_int)], 4, axis, c.byref(maximum))
                maxima.append(maximum.value)
            shutter = c.c_int()
            self.call('IsInternalMechanicalShutter', [c.POINTER(c.c_int)], c.byref(shutter))
            caps = AndorCapabilities()
            caps.size = c.sizeof(caps)
            self.call('GetCapabilities', [c.POINTER(AndorCapabilities)], c.byref(caps))
            if not caps.acquisition & 1 or not caps.read & 1:
                raise ValueError('This Andor camera does not support single-frame image acquisition')
            device.update(name=model.value.decode(errors='replace'), serial=str(serial.value),
                          width=width.value, height=height.value, pixel_um=px.value,
                          pixel_height_um=py.value, bins=[n for n in range(1, 9) if n <= min(maxima)],
                          gain_supported=False, shutter=bool(shutter.value), color=False,
                          shutter_ex=bool(caps.features & 16),read_capabilities=caps.read,exposure_min_s=0)
            device['readout_options']=self.readout_options(device)
            return device
        except Exception:
            self.disconnect()
            raise

    def cooling_status(self):
        if self.handle is None:
            raise ValueError('Connect an Andor CCD first')
        self.call('SetCurrentCamera', [c.c_int32], self.handle)
        minimum, maximum, enabled = c.c_int(), c.c_int(), c.c_int()
        self.call('GetTemperatureRange', [c.POINTER(c.c_int)] * 2, c.byref(minimum), c.byref(maximum))
        self.call('IsCoolerOn', [c.POINTER(c.c_int)], c.byref(enabled))
        temperature = c.c_float()
        code = bind(self.dll, 'GetTemperatureF', [c.POINTER(c.c_float)], c.c_uint)(c.byref(temperature))
        states = {20034: 'Off', 20035: 'Stabilizing', 20036: 'Stabilized', 20037: 'Cooling', 20040: 'Temperature drift'}
        if code not in states:
            raise ValueError(f'Andor temperature read failed: error {code}')
        target = self.cooling_target
        return dict(supported=True, temperature_c=temperature.value, target_c=target,
                    enabled=bool(enabled.value), state=states[code], minimum_c=minimum.value,
                    maximum_c=maximum.value, updated_utc=time.time(), stale=False)

    def set_cooling(self, enabled=None, target_c=None):
        state = self.cooling_status()
        if target_c is not None:
            if not state['minimum_c'] <= target_c <= state['maximum_c']:
                raise ValueError(f"Target must be between {state['minimum_c']} and {state['maximum_c']} °C")
            self.call('SetTemperature', [c.c_int], target_c)
            self.cooling_target = target_c
        if enabled is not None:
            self.call('CoolerON' if enabled else 'CoolerOFF', [])
        return self.cooling_status()

    def readout_options(self,device):
        options={}
        try:
            channels,amps=c.c_int(),c.c_int()
            self.call('GetNumberADChannels',[c.POINTER(c.c_int)],c.byref(channels))
            self.call('GetNumberAmp',[c.POINTER(c.c_int)],c.byref(amps))
            speeds=[]
            for channel in range(channels.value):
                for amp in range(amps.value):
                    count=c.c_int()
                    self.call('GetNumberHSSpeeds',[c.c_int,c.c_int,c.POINTER(c.c_int)],channel,amp,c.byref(count))
                    for index in range(count.value):
                        speed=c.c_float()
                        self.call('GetHSSpeed',[c.c_int,c.c_int,c.c_int,c.POINTER(c.c_float)],channel,amp,index,c.byref(speed))
                        speeds.append(dict(value=f'{channel}:{amp}:{index}',label=f'{speed.value:g} MHz · ADC {channel+1} · output {amp+1}'))
            if speeds:options['horizontal_speed']=speeds
        except (ValueError,AttributeError):pass
        try:
            count=c.c_int();self.call('GetNumberVSSpeeds',[c.POINTER(c.c_int)],c.byref(count))
            speeds=[]
            for index in range(count.value):
                speed=c.c_float();self.call('GetVSSpeed',[c.c_int,c.POINTER(c.c_float)],index,c.byref(speed))
                speeds.append(dict(value=str(index),label=f'{speed.value:g} µs / row'))
            if speeds:options['vertical_speed']=speeds
        except (ValueError,AttributeError):pass
        options['read_mode']=[dict(value='4',label='Image')]
        if device.get('read_capabilities',0)&8:options['read_mode'].append(dict(value='0',label='Full vertical binning (FVB)'))
        return options

    def configure_readout(self,readout):
        if 'horizontal_speed' in readout:
            channel,amp,index=map(int,readout['horizontal_speed'].split(':'))
            self.call('SetADChannel',[c.c_int],channel)
            self.call('SetOutputAmplifier',[c.c_int],amp)
            self.call('SetHSSpeed',[c.c_int,c.c_int],amp,index)
        if 'vertical_speed' in readout:self.call('SetVSSpeed',[c.c_int],int(readout['vertical_speed']))

    def disconnect(self):
        if self.handle is not None:
            self.call('SetCurrentCamera', [c.c_int32], self.handle)
            # Do not automatically switch a cold camera's cooler off on disconnect.
            temperature = c.c_int()
            code = bind(self.dll, 'GetTemperature', [c.POINTER(c.c_int)], c.c_uint)(c.byref(temperature))
            if code in (20034, 20035, 20036, 20037, 20040) and temperature.value < -20:
                raise ValueError('Andor sensor is below -20 °C; warm it using the vendor cooler controls before disconnecting')
            self.call('ShutDown', [])
            self.handle = None

    def capture(self, device, exposure, binning, gain, dark, cancel,roi=None,readout=None,progress=None):
        if gain:
            raise ValueError('Andor gain modes differ by model; use gain 0 to preserve the current native gain')
        if binning not in device['bins']:
            raise ValueError('Andor camera does not support this binning')
        if dark and not device['shutter']:
            raise ValueError('Andor has no internal shutter; cover the sensor and capture a normal frame for a dark')
        region=roi or dict(x=0,y=0,width=device['width']//binning*binning,height=device['height']//binning*binning)
        width,height=region['width']//binning,region['height']//binning
        if width * height <= 0 or width * height > 100_000_000:
            raise ValueError('Invalid frame dimensions / memory budget exceeded')
        self.call('SetCurrentCamera', [c.c_int32], self.handle)
        status = c.c_int()
        self.call('GetStatus', [c.POINTER(c.c_int)], c.byref(status))
        if status.value != 20073:  # DRV_IDLE
            raise ValueError('Andor camera is not idle')
        self.call('SetAcquisitionMode', [c.c_int], 1)  # single scan
        readout=readout or {}
        self.configure_readout(readout)
        read_mode=int(readout.get('read_mode',4))
        self.call('SetReadMode', [c.c_int], read_mode)
        self.call('SetTriggerMode', [c.c_int], 0)  # internal
        if read_mode==0:
            if roi is not None:raise ValueError('FVB uses the full sensor; disable ROI')
            self.call('SetFVBHBin',[c.c_int],binning)
            height=1
        else:self.call('SetImage', [c.c_int] * 6, binning, binning, region['x']+1, region['x']+width*binning, region['y']+1, region['y']+height*binning)
        self.call('SetExposureTime', [c.c_float], exposure)
        if device['shutter']:
            # Use minimum transfer delays reported for the installed internal shutter.
            opening, closing = c.c_int(), c.c_int()
            self.call('GetShutterMinTimes', [c.POINTER(c.c_int)] * 2, c.byref(closing), c.byref(opening))
            if device.get('shutter_ex'):
                self.call('SetShutterEx', [c.c_int] * 5, 1, 2 if dark else 0,
                          closing.value, opening.value, 1)
            else:
                self.call('SetShutter', [c.c_int] * 4, 1, 2 if dark else 0, closing.value, opening.value)
        actual, accumulate, kinetic = c.c_float(), c.c_float(), c.c_float()
        self.call('GetAcquisitionTimings', [c.POINTER(c.c_float)] * 3,
                  c.byref(actual), c.byref(accumulate), c.byref(kinetic))
        image = np.empty((height, width), dtype=np.uint16)
        started = time.time()
        deadline = time.monotonic() + max(actual.value, exposure) + 60
        self.call('StartAcquisition', [])
        if progress:progress('Exposing',actual.value)
        completed = False
        readout_started=False
        try:
            while True:
                if cancel.is_set() or time.monotonic() > deadline:
                    raise ValueError('Andor exposure cancelled/timed out')
                self.call('GetStatus', [c.POINTER(c.c_int)], c.byref(status))
                if progress and not readout_started and time.time()-started>=actual.value:
                    progress('Readout',estimated=True);readout_started=True
                if status.value == 20073:
                    break
                if status.value != 20072:
                    raise ValueError(f'Andor acquisition status {status.value}')
                time.sleep(.05)
            if progress and not readout_started:progress('Readout',estimated=True)
            self.call('GetAcquiredData16', [c.c_void_p, c.c_uint32], image.ctypes.data, image.size)
            if cancel.is_set():
                raise ValueError('Andor readout cancelled')
            completed = True
            return image, actual.value, started
        finally:
            if not completed:
                self.call('AbortAcquisition', [])


class Andor3:
    def __init__(self):
        self.dll = None
        self.handle = None
        self.pending_buffer = None

    def call(self, name, types, *args):
        code = bind(self.dll, name, types)(*args)
        if code:
            raise ValueError(f'Andor SDK3 {name}: error {code}')

    def integer(self, handle, feature, suffix=''):
        value = c.c_int64()
        self.call('AT_GetInt' + suffix, [c.c_int, c.c_wchar_p, c.POINTER(c.c_int64)],
                  handle, feature, c.byref(value))
        return value.value

    def number(self, handle, feature, suffix=''):
        value = c.c_double()
        self.call('AT_GetFloat' + suffix, [c.c_int, c.c_wchar_p, c.POINTER(c.c_double)],
                  handle, feature, c.byref(value))
        return value.value

    def string(self, handle, feature):
        value = c.create_unicode_buffer(256)
        self.call('AT_GetString', [c.c_int, c.c_wchar_p, c.c_wchar_p, c.c_int], handle, feature, value, 256)
        return value.value

    def enum(self, feature, value):
        self.call('AT_SetEnumString', [c.c_int, c.c_wchar_p, c.c_wchar_p], self.handle, feature, value)

    def enums(self, handle, feature):
        count = c.c_int()
        self.call('AT_GetEnumCount', [c.c_int, c.c_wchar_p, c.POINTER(c.c_int)], handle, feature, c.byref(count))
        values = []
        for index in range(count.value):
            available = c.c_int()
            self.call('AT_IsEnumIndexAvailable', [c.c_int, c.c_wchar_p, c.c_int, c.POINTER(c.c_int)],
                      handle, feature, index, c.byref(available))
            if available.value:
                text = c.create_unicode_buffer(256)
                self.call('AT_GetEnumStringByIndex', [c.c_int, c.c_wchar_p, c.c_int, c.c_wchar_p, c.c_int],
                          handle, feature, index, text, 256)
                values.append(text.value)
        return values

    def scan(self):
        if self.dll is None:
            self.dll = load_sdk('atcore.dll', True)
            try:
                self.call('AT_InitialiseLibrary', [])
            except Exception:
                self.dll = None
                raise
        devices = []
        for index in range(self.integer(1, 'DeviceCount')):  # AT_HANDLE_SYSTEM
            handle = c.c_int()
            own = False
            if self.handle is not None and getattr(self, 'index', None) == index:
                handle.value = self.handle
            else:
                self.call('AT_Open', [c.c_int, c.POINTER(c.c_int)], index, c.byref(handle))
                own = True
            try:
                model = self.string(handle.value, 'CameraModel')
                interface = self.string(handle.value, 'InterfaceType')
                # SDK3 can enumerate its SimCam; never present it as real USB hardware.
                if 'sim' in model.lower() or 'usb' not in interface.lower():
                    continue
                serial = self.string(handle.value, 'SerialNumber')
                devices.append(dict(key=f'andor3:{serial}', vendor='andor', sdk='sdk3',
                                    name=model, serial=serial, id=index, interface=interface))
            finally:
                if own:
                    self.call('AT_Close', [c.c_int], handle.value)
        return devices

    def connect(self, device):
        handle = c.c_int()
        self.call('AT_Open', [c.c_int, c.POINTER(c.c_int)], device['id'], c.byref(handle))
        self.handle, self.index = handle.value, device['id']
        try:
            if self.string(self.handle, 'SerialNumber') != device['serial']:
                raise ValueError('Andor USB device order changed; scan again')
            bins = self.enums(self.handle, 'AOIBinning')
            device.update(width=self.integer(self.handle, 'SensorWidth'),
                          height=self.integer(self.handle, 'SensorHeight'),
                          pixel_um=self.number(self.handle, 'PixelWidth'),
                          pixel_height_um=self.number(self.handle, 'PixelHeight'),
                          bins=[n for n in range(1, 9) if f'{n}x{n}' in bins],
                          gain_supported=False, shutter=False, color=False,
                          exposure_min_s=self.number(self.handle, 'ExposureTime', 'Min'),
                          exposure_max_s=self.number(self.handle, 'ExposureTime', 'Max'))
            device['readout_options']={}
            for feature in ('PixelReadoutRate','SimplePreAmpGainControl','ElectronicShutteringMode'):
                try:device['readout_options'][feature]=[dict(value=v,label=v) for v in self.enums(self.handle,feature)]
                except (ValueError,AttributeError):pass
            return device
        except Exception:
            self.disconnect()
            raise

    def disconnect(self):
        if self.handle is not None:
            self.call('AT_Close', [c.c_int], self.handle)
            self.handle = None
            self.pending_buffer = None

    def close(self):
        self.disconnect()
        if self.dll is not None:
            self.call('AT_FinaliseLibrary', [])
            self.dll = None

    def capture(self, device, exposure, binning, gain, dark, cancel,readout=None,progress=None):
        if gain:
            raise ValueError('Andor SDK3 uses named gain modes; use gain 0 to preserve its native gain')
        if dark:
            raise ValueError('Andor sCMOS has no mechanical shutter; cover the sensor and capture a normal frame for a dark')
        if binning not in device['bins']:
            raise ValueError('Andor camera does not support this binning')
        if not device['exposure_min_s'] <= exposure <= device['exposure_max_s']:
            raise ValueError('Exposure is outside this Andor camera\'s limits')
        for feature,value in (readout or {}).items():self.enum(feature,value)
        self.enum('AOIBinning', f'{binning}x{binning}')
        self.enum('PixelEncoding', 'Mono16')
        self.enum('TriggerMode', 'Internal')
        self.enum('CycleMode', 'Fixed')
        self.call('AT_SetInt', [c.c_int, c.c_wchar_p, c.c_int64], self.handle, 'FrameCount', 1)
        self.call('AT_SetFloat', [c.c_int, c.c_wchar_p, c.c_double], self.handle, 'ExposureTime', exposure)
        # Read current AOI and stride rather than assuming a tightly packed full sensor.
        width = self.integer(self.handle, 'AOIWidth')
        height = self.integer(self.handle, 'AOIHeight')
        stride = self.integer(self.handle, 'AOIStride')
        size = self.integer(self.handle, 'ImageSizeBytes')
        if width * height <= 0 or width * height > 100_000_000 or not 0 < size <= 256_000_000:
            raise ValueError('Andor frame exceeds memory budget')
        if stride < width * 2 or stride * height > size:
            raise ValueError('Andor returned inconsistent Mono16 frame dimensions')
        allocation = c.create_string_buffer(size + 7)
        address = (c.addressof(allocation) + 7) & ~7
        self.call('AT_Flush', [c.c_int], self.handle)
        self.pending_buffer = allocation
        self.call('AT_QueueBuffer', [c.c_int, c.c_void_p, c.c_int], self.handle, address, size)
        started = time.time()
        actual = self.number(self.handle, 'ExposureTime')
        deadline = time.monotonic() + actual + 60
        try:
            self.call('AT_Command', [c.c_int, c.c_wchar_p], self.handle, 'AcquisitionStart')
            if progress:progress('Exposing',actual)
            readout_started=False
            while True:
                if cancel.is_set() or time.monotonic() > deadline:
                    raise ValueError('Andor exposure cancelled/timed out')
                ptr, received = c.c_void_p(), c.c_int()
                code = bind(self.dll, 'AT_WaitBuffer', [c.c_int, c.POINTER(c.c_void_p), c.POINTER(c.c_int), c.c_uint])(
                    self.handle, c.byref(ptr), c.byref(received), 100)
                if code == 13 and progress and not readout_started and time.time()-started >= actual:
                    progress('Readout',estimated=True);readout_started=True
                if code == 13:  # AT_ERR_TIMEDOUT: return to cancellation check.
                    continue
                if code:
                    raise ValueError(f'Andor SDK3 readout: error {code}')
                if ptr.value != address or received.value < stride * height or received.value > size:
                    raise ValueError('Andor returned an unexpected / truncated image buffer')
                if progress and not readout_started:progress('Readout',estimated=True)
                pixels = np.ndarray((height, width), dtype='<u2', buffer=allocation,
                                    offset=address - c.addressof(allocation), strides=(stride, 2)).copy()
                if cancel.is_set():
                    raise ValueError('Andor readout cancelled')
                return pixels, actual, started
        finally:
            try:
                self.call('AT_Command', [c.c_int, c.c_wchar_p], self.handle, 'AcquisitionStop')
            finally:
                # Buffers remain alive until the SDK has released its queue.
                self.call('AT_Flush', [c.c_int], self.handle)
                self.pending_buffer = None
