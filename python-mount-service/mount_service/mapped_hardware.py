"""Hardware parameters resolved to a physical drive/channel from active topology."""
import asyncio
from dataclasses import replace
from .hardware import HardwareWorkbench
from .domain import AXES, AXIS_MAP, DriveError
from .drive_refresh import refresh_configuration, refresh_after_flash

class PhysicalDrive:
    def __init__(self,topology,number):
        self.topology=topology;self.number=number
        self.owner=topology.mount.transport if number==1 else None
        caches=getattr(topology,'hardware_caches',{})
        topology.hardware_caches=caches
        cache=caches.setdefault(number,{k:{} for k in ('configuration_errors','resolutions','scaling','position_limits','unit_modes','velocity_limits')})
        for key,value in cache.items():setattr(self,key,getattr(self.owner,key,value) if self.owner else value)
        self.commands=getattr(self.owner,'commands',{a.id:0 for a in AXES})
        self.targets=getattr(self.owner,'targets',{a.id:None for a in AXES})
    def _query_sync(self,command,timeout_seconds=.35):
        if self.owner:return self.owner._query_sync(command,timeout_seconds,physical=True)
        if not self.topology.port:raise DriveError('Drive 2 disconnected')
        previous=self.topology.port.timeout
        try:
            self.topology.port.timeout=timeout_seconds
            return self.topology.query(command)
        finally:self.topology.port.timeout=previous
    async def _job(self,fn,timeout_seconds=6):
        if self.owner:return await self.owner._job(fn,timeout_seconds=timeout_seconds)
        def run():
            with self.topology.lock:return fn()
        return await asyncio.to_thread(run)
    async def query(self,command):return await self._job(lambda:self._query_sync(command))

class MappedHardware(HardwareWorkbench):
    def __init__(self,topology):
        super().__init__(topology.mount);self.topology=topology
    def drive(self,axis_id):
        if self.topology.axis_mapping!=self.topology.active_mapping:raise DriveError('Reconnect hardware to apply the saved mapping before editing parameters')
        slot={'drive':int(axis_id[-1]),'channel':1} if axis_id in ('drive1','drive2') else self.topology.active_mapping.get(axis_id)
        if not slot:raise DriveError('Unknown mapped axis')
        connected=self.mount.transport.connected if slot['drive']==1 else self.topology.port is not None and self.topology.port.is_open
        if not connected:raise DriveError('Selected drive is disconnected')
        axis=replace(AXIS_MAP.get(axis_id,AXES[0]),id=axis_id,number=slot['channel'])
        return PhysicalDrive(self.topology,slot['drive']),axis
    def binding(self,axis_id):
        slot={'drive':int(axis_id[-1]),'channel':1} if axis_id in ('drive1','drive2') else self.topology.active_mapping.get(axis_id)
        if not slot:raise DriveError('Unknown mapped axis')
        group=self.topology.groups['mount' if slot['drive']==1 else 'imaging']
        return dict(slot,serial_number=group['serial_number'])
    def verify_binding(self,axis_id,target):
        if target!=self.binding(axis_id):raise DriveError('Axis mapping or drive identity changed since Read; read again before writing')
    async def read(self,axis_id,group):
        result=await super().read(axis_id,group)
        result['target']=self.binding(axis_id)
        return result
    async def sensor_read(self,axis_id,socket):
        result=await super().sensor_read(axis_id,socket)
        result['target']=self.binding(axis_id)
        return result
    async def write(self,axis_id,command,value,expected):
        if command=='MOUNT_CPR' and axis_id not in AXIS_MAP:raise DriveError('Angular mount resolution is not available for this accessory axis')
        return await super().write(axis_id,command,value,expected)
    async def stop(self,axis_id):
        if axis_id in AXIS_MAP:return await self.mount.command(axis_id,'stop')
        drive,axis=self.drive(axis_id)
        await drive._job(lambda:drive._query_sync(f'AX{axis.number}.ST'))
    async def save_drive(self,number):
        drive,_=self.drive('drive'+str(number))
        def save():
            if any(float(drive._query_sync(f'AX{n}.MO'))!=0 for n in range(1,5)) or any(self.mount.transport.commands.values()) or any(v is not None for v in self.mount.transport.targets.values()):raise DriveError('Disable and stop motors before saving')
            response=drive._query_sync('SV',timeout_seconds=6)
            self.refresh_sync(drive, after_flash=True)
            self.audit(dict(drive=number,command='SV',status='acknowledged'))
            return {'acknowledged':True,'response':response,'configuration_refreshed':True}
        return await drive._job(save,timeout_seconds=12)

    def refresh_sync(self, drive, after_flash=False):
        bindings = {role:slot['channel'] for role,slot in self.topology.active_mapping.items() if slot['drive']==drive.number}
        result = (refresh_after_flash if after_flash else refresh_configuration)(drive, bindings)
        self.topology.readings.clear()
        if hasattr(self.mount,'generation'):self.mount.generation += 1
        return result
