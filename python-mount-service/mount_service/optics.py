"""Two-port optics controls using the active physical axis mapping."""
import json
import math
import time
from dataclasses import replace
from .domain import AXES
from .mapped_hardware import PhysicalDrive
from .domain import DriveError

ACCESSORIES=('focuser1','focuser2','rotator2')

def opposite_m3_position(angle, minimum, maximum):
    # Keep the opposite port in the same signed, multi-turn drive coordinates.
    for candidate in (angle + 180, angle - 180):
        if minimum <= candidate <= maximum:
            return candidate
    return None


class OpticsControl:
    def __init__(self,mount,hardware,directory):
        self.mount=mount;self.hardware=hardware;self.path=directory/'optics-settings.json'
        self.settings=json.loads(self.path.read_text()) if self.path.exists() else {'m3_port1':330.0}
        self._status_cache={}
        migrated=False
        for config in self.settings.get('axes',{}).values():
            if 'zero_count' in config:config.pop('zero_count');migrated=True
        if migrated:self.path.write_text(json.dumps(self.settings,indent=2))
        self.mount.config.pop('m3_position_limits',None)

    def configure_m3(self,angle):
        if not math.isfinite(angle):raise DriveError('Enter a finite Port 1 position')
        for key in ('m3_minimum','m3_maximum','m3_in_position_window'):self.settings.pop(key,None)
        self.settings['m3_port1']=angle
        self.path.write_text(json.dumps(self.settings,indent=2))
        return self.settings

    def read_m3_sync(self,drive,axis):
        q=lambda command:float(drive._query_sync(f'AX{axis.number}.{command}'))
        f={i:q(f'FC[{i}]') for i in (1,2,5,6,7,8,9,10)}
        if any(not math.isfinite(v) or v<1 for v in f.values()):raise DriveError('Invalid M3 encoder scaling')
        factor=f[7]/f[8]*f[6]/f[5]*f[2]/f[1]
        resolution=drive.resolutions.get('m3',axis.counts_per_rev)
        scale=resolution/360*factor
        velocity_limit=q('VH[2]')/(scale*f[9]/f[10])
        if not math.isfinite(velocity_limit) or velocity_limit<=0:raise DriveError('Invalid M3 drive velocity limit')
        native={key:q(key) for key in ('VL[3]','VH[3]','TR[1]')}
        if not all(math.isfinite(v) for v in native.values()):raise DriveError('Invalid M3 settings reply')
        return dict(max_jog_speed=velocity_limit,minimum=native['VL[3]']/scale+axis.offset_deg,maximum=native['VH[3]']/scale+axis.offset_deg,in_position_window=native['TR[1]']/scale,scale=scale,resolution=resolution,native=native)

    async def m3_settings(self):
        async with self.hardware.topology.operation_lock:
            drive,axis=self.hardware.drive('m3')
            values=await drive._job(lambda:self.read_m3_sync(drive,axis))
            return {**values,'target':self.hardware.binding('m3'),'port1':self.settings['m3_port1'],'port2':opposite_m3_position(self.settings['m3_port1'],values['minimum'],values['maximum']),'jog_speed':self.settings.get('m3_jog_speed',1.0)}

    async def save_m3(self,angle,minimum,maximum,window,target,expected,jog_speed=None):
        if jog_speed is not None:
            if not math.isfinite(jog_speed) or jog_speed<=0:raise DriveError('M3 speed exceeds the configured velocity limit')
        if not all(math.isfinite(v) for v in (angle,minimum,maximum,window)) or not minimum<maximum or not 0<window<90:raise DriveError('Enter valid M3 positions, limits and window')
        if not minimum<=angle<=maximum:raise DriveError('Port 1 position must be within Minimum and Maximum')
        if opposite_m3_position(angle,minimum,maximum) is None:raise DriveError('The opposite port (180 degrees from Port 1) must also fit within Minimum and Maximum')
        async with self.hardware.topology.operation_lock:
            self.hardware.verify_binding('m3',target)
            drive,axis=self.hardware.drive('m3')
            def apply():
                if any(float(drive._query_sync(f'AX{n}.MO'))!=0 for n in range(1,5)):raise DriveError('Disable all motors on the selected drive before saving M3 settings')
                current=self.read_m3_sync(drive,axis)
                if jog_speed is not None and jog_speed>current['max_jog_speed']:raise DriveError(f"M3 speed exceeds the configured velocity limit ({current['max_jog_speed']:.6g} degrees/s)")
                if current['native']!=expected:raise DriveError('M3 drive settings changed; reopen settings before saving')
                lower=math.ceil((minimum-axis.offset_deg)*current['scale'])
                upper=math.floor((maximum-axis.offset_deg)*current['scale'])
                radius=window*current['scale']
                if lower>=upper or not math.isfinite(radius) or radius<=0:raise DriveError('M3 limits or window are outside the encoder count range')
                new={'VL[3]':lower,'VH[3]':upper,'TR[1]':radius}
                order=['VL[3]','VH[3]'] if upper<current['native']['VL[3]'] else ['VH[3]','VL[3]']
                try:
                    for key in [*order,'TR[1]']:
                        drive._query_sync(f'AX{axis.number}.{key}={new[key]}')
                        if not math.isclose(float(drive._query_sync(f'AX{axis.number}.{key}')),new[key],rel_tol=1e-6,abs_tol=1e-6):raise DriveError('M3 settings readback mismatch')
                    drive._query_sync('SV',timeout_seconds=6)
                    self.hardware.refresh_sync(drive, after_flash=True)
                    return self.read_m3_sync(drive,axis)
                except Exception as exc:
                    drive.configuration_errors['m3']='M3 settings save unconfirmed; read and verify before motion'
                    raise DriveError(f'M3 save failed; some drive values may have changed: {exc}') from exc
            result=await drive._job(apply,timeout_seconds=10)
            drive.configuration_errors.pop('m3',None)
            if jog_speed is not None:self.settings['m3_jog_speed']=jog_speed
            self.configure_m3(angle)
            self._status_cache.clear()
            return {**result,'saved_to_flash':True}

    def axis_settings(self,role):
        config=self.settings.get('axes',{}).get(role)
        return config if config and config['binding']==self.hardware.binding(role) else None

    async def configure_axis(self,role,minimum,maximum,unit_per_count,target):
        if role not in (*ACCESSORIES,'rotator'):raise DriveError('Unknown optics axis')
        if not all(math.isfinite(v) for v in (minimum,maximum,unit_per_count)) or minimum>=maximum or unit_per_count<=0:raise DriveError('Enter valid minimum, maximum and positive unit conversion')
        async with self.hardware.topology.operation_lock:
            self.hardware.verify_binding(role,target)
            drive,axis=self.hardware.drive(role)
            enabled=float(await drive.query(f'AX{axis.number}.MO'))
            if enabled!=0:raise DriveError('Disable this axis before changing travel or unit mapping')
            config=dict(minimum=minimum,maximum=maximum,unit_per_count=unit_per_count,binding=target)
            self._status_cache.clear()
            self.settings.setdefault('axes',{})[role]=config
            self.path.write_text(json.dumps(self.settings,indent=2))
            return config

    def check_calibrated_target(self,role,position,counts_per_base=1):
        config=self.axis_settings(role)
        if config:
            physical=position*counts_per_base*config['unit_per_count']
            if not config['minimum']<=physical<=config['maximum']:raise DriveError('Target is outside optics travel limits')

    def read_sync(self,drive,axis):
        def q(name):
            v=float(drive._query_sync(f'AX{axis.number}.{name}'))
            if not math.isfinite(v):raise DriveError('Invalid drive telemetry')
            return v
        f=[1. if i in (3,4) else q(f'FC[{i}]') for i in range(1,13)]
        if any(v<1 for v in f):raise DriveError('Read and configure valid encoder scaling first')
        p=(f[6]/f[7])*(f[5]/f[4])*(f[1]/f[0]);v=p*f[8]/f[9]
        lo,hi=q('VL[3]')/p,q('VH[3]')/p
        enabled=q('MO');mode=q('UM');speed=q('VH[2]')/v
        if enabled not in (0,1):raise DriveError('Invalid motor enable state')
        return dict(position=q('PX'),enabled=enabled==1,mode=mode,minimum=lo,maximum=hi,max_speed=speed,position_factor=p,velocity_factor=v,ready=mode==5 and lo<hi and speed>0)

    async def status(self,fast=False):
        axes={}
        async with self.hardware.topology.operation_lock:
            for role in ACCESSORIES:
                try:
                    drive,axis=self.hardware.drive(role)
                    binding=self.hardware.binding(role)
                    cached=self._status_cache.get(role)
                    if fast and cached and cached[0]==binding and time.monotonic()-cached[1]<10:
                        def live_read():
                            position=float(drive._query_sync(f'AX{axis.number}.PX'))
                            enabled=float(drive._query_sync(f'AX{axis.number}.MO'))
                            if not math.isfinite(position) or enabled not in (0,1):raise DriveError('Invalid live optics telemetry')
                            return {**cached[2],'position':position,'enabled':bool(enabled)}
                        values=await drive._job(live_read)
                    else:
                        values=await drive._job(lambda:self.read_sync(drive,axis))
                        self._status_cache[role]=(binding,time.monotonic(),values)
                    axes[role]={**values,'binding':binding,'unit':'counts'}
                except Exception as exc:axes[role]={'error':str(exc),'ready':False}
        snapshot=self.mount.snapshot()
        for role in ('rotator','m3'):
            a=next(a for a in snapshot['axes'] if a['id']==role)
            axes[role]={**a,'position':a['position_deg'],'minimum':a['minimum_deg'],'maximum':a['maximum_deg'],'unit':'deg','ready':a['valid'] and snapshot['drive']['connected'] and snapshot['drive']['motion_available'],'binding':self.hardware.binding(role)}
        for role in (*ACCESSORIES,'rotator'):
            axes[role]['calibration']=self.axis_settings(role)
        angle=self.settings['m3_port1']
        try:
            cached=self._status_cache.get('m3')
            binding=self.hardware.binding('m3')
            if fast and cached and cached[0]==binding and time.monotonic()-cached[1]<10:m3=cached[2]
            else:
                m3=await self.m3_settings()
                self._status_cache['m3']=(binding,time.monotonic(),m3)
        except Exception as exc:m3={'port1':angle,'port2':None,'minimum':None,'maximum':None,'in_position_window':None,'error':str(exc)}
        return {'axes':axes,'m3':m3}

    async def command(self,role,action,value=None,speed=None,target=None,step=None,lease=0.8):
        if role not in (*ACCESSORIES,'rotator','m3'):raise DriveError('Unknown optics axis')
        async with self.hardware.topology.operation_lock:
            self.hardware.verify_binding(role,target)
            if role in ('rotator','m3'):
                if action not in ('stop','disable'):self.hardware.drive(role)
                if step is not None:
                    current=next(a for a in self.mount.snapshot()['axes'] if a['id']==role)
                    if not current['valid']:raise DriveError('Position telemetry is unavailable')
                    value=current['position_deg']+step
                if action=='goto' and role=='rotator':
                    current=next(a for a in self.mount.snapshot()['axes'] if a['id']==role)
                    self.check_calibrated_target(role,value,current['counts_per_rev']/360)
                if role=='m3':
                    reference=self.settings.get('m3_jog_speed',1.0)
                    if action=='jog':
                        if value not in (-1,1):raise DriveError('Choose CW or CCW direction')
                        value=value*reference
                    elif action=='goto' and speed is None:speed=reference
                return await self.mount.command(role,action,value,speed,lease)
            if action in ('stop','disable'):
                slot=self.hardware.binding(role)
                drive=PhysicalDrive(self.hardware.topology,slot['drive'])
                axis=replace(AXES[0],id=role,number=slot['channel'])
            else:drive,axis=self.hardware.drive(role)
            def apply():
                prefix=f'AX{axis.number}.'
                q=lambda text:drive._query_sync(prefix+text)
                if action in ('stop','disable'):
                    q('ST')
                    if action=='disable':q('MO=0')
                    return {'ok':True}
                state=self.read_sync(drive,axis)
                if not state['ready']:raise DriveError('Configure position mode UM=5, scaling and valid travel/velocity limits in Hardware Config first')
                if action=='enable':
                    q('ST');q('MO=1');return {'ok':True}
                if action!='goto':raise DriveError('Unsupported optics command')
                if not state['enabled']:raise DriveError('Enable this axis before moving')
                position=state['position']+step if step is not None else value
                if position is None or not math.isfinite(position) or not state['minimum']<=position<=state['maximum']:raise DriveError('Target is outside configured drive limits')
                self.check_calibrated_target(role,position)
                if speed is None or not math.isfinite(speed) or not 0<speed<=state['max_speed']:raise DriveError('Speed exceeds configured drive limit')
                try:
                    for cmd in ('ST',f"SP={speed*state['velocity_factor']:.17g}",f"PA={position*state['position_factor']:.17g}",'BG'):q(cmd)
                except Exception:
                    try:q('ST')
                    except Exception:pass
                    raise
                return {'ok':True,'target':position}
            return await drive._job(apply)

    async def stop_accessories(self):
        for role in ACCESSORIES:
            try:await self.command(role,'stop',target=self.hardware.binding(role))
            except DriveError:pass
