"""Platinum-only parameter catalogue; no arbitrary serial command API.

Source: installed EAS II NetHelp/Documents/p*.htm and MAN-P-CommandRef.
All edits are RAM edits. Save-to-flash is a separate operation.
"""
import math
from datetime import datetime, timezone
from pathlib import Path
import json

from .domain import AXIS_MAP, AXES, DriveError
from .transports import SerialElmoDrive
from .drive_refresh import refresh_after_flash

def cache_velocity_limit(drive, axis, native):
    factors=drive.scaling.get(axis.id,[1.]*12)
    factor=(factors[6]/factors[7])*(factors[5]/factors[4])*(factors[1]/factors[0])*factors[8]/factors[9]
    value=float(native)/(drive.resolutions.get(axis.id,axis.counts_per_rev)/360*factor)
    if not math.isfinite(value) or value<0:raise DriveError('Invalid drive velocity limit')
    drive.velocity_limits[axis.id]=value


def parameter(command, label, group, unit='', minimum=None, maximum=None, *, writable=False, integer=False, choices=None, cap=None, note=''):
    return dict(command=command, label=label, group=group, unit=unit, minimum=minimum,
                maximum=maximum, writable=writable, integer=integer, choices=choices, cap=cap, note=note)


PARAMETERS = [
    parameter('MP[1]', 'Motor resistance', 'motor', 'Ω', 0, writable=True),
    parameter('MP[2]', 'Motor self-inductance', 'motor', 'H', 0, writable=True),
    parameter('MP[3]', 'Back EMF constant', 'motor', 'V / electrical Hz', 0, writable=True),
    parameter('MP[4]', 'Motor pole pairs', 'motor', '', 1, writable=True, integer=True, note='Changing this value resets commutation.'),
    parameter('MP[5]', 'Equivalent inductance ratio', 'motor', '', 0, writable=True),
    parameter('MP[6]', 'Mechanical inertia', 'motor', 'drive units', 0, writable=True),
    parameter('CL[1]', 'Continuous current limit', 'motor', 'A', 0, writable=True, cap='MC[4]'),
    parameter('PL[1]', 'Peak current limit', 'motor', 'A', 0, writable=True, cap='MC[2]'),
    parameter('PL[2]', 'Peak current duration', 'motor', 's', .1, 1000, writable=True, cap='MC[3]'),
    parameter('MC[2]', 'Hardware peak current ceiling', 'motor', 'A'),
    parameter('MC[4]', 'Hardware continuous current ceiling', 'motor', 'A'),
    parameter('CA[18]', 'Commutation feedback resolution', 'encoder', 'counts / revolution or electrical cycle', 6, 2**52-1, writable=True, integer=True, note='Changing this resolution resets commutation. This is distinct from the sensor protocol bit count.'),
    parameter('MOUNT_CPR', 'Mount encoder counts per axis revolution', 'encoder', 'counts / axis revolution', 6, 2**52-1, writable=True, integer=True, note='Software conversion used for mount telemetry and angular commands. Set according to the position sensor and mechanical transmission.'),
    parameter('SA[1]', 'Position feedback socket / signal', 'encoder', writable=True, integer=True, choices={str(n+aux*16):f'Socket {n} / {name}' for n in range(1,9) for aux,name in enumerate(['Main','Aux 1','Aux 2'])}),
    parameter('SA[2]', 'Velocity feedback socket / signal', 'encoder', writable=True, integer=True, choices={str(n+aux*16):f'Socket {n} / {name}' for n in range(1,9) for aux,name in enumerate(['Main','Aux 1','Aux 2'])}),
    parameter('PX', 'Actual position', 'encoder', 'native units'),
    parameter('VX', 'Actual velocity', 'encoder', 'native units / s'),
    parameter('CA[7]', 'Absolute commutation phase offset', 'commutation', 'stepper units', -4096, 4095, writable=True, integer=True, note='Changing this value resets commutation.'),
    parameter('CA[16]', 'Search on every Motor On', 'commutation', '', 0, 1, writable=True, integer=True, choices={'0':'Use known commutation','1':'Force search on Motor On'}, note='A future Enable can initiate a commutation search and movement.'),
    parameter('CA[17]', 'Commutation method', 'commutation', writable=True, integer=True, choices={'1':'Digital Hall','2':'Stepper search','3':'Binary search','4':'Analog Hall / resolver','5':'Serial absolute encoder','8':'Absolute + analog (Hiperface)','9':'BEMF observer'}, note='Changing this value resets commutation; search methods can move on Enable.'),
    parameter('CA[20]', 'Hall / encoder mismatch angle', 'commutation', 'stepper units', 0, 1365, writable=True, integer=True),
    parameter('CA[25]', 'Reverse stepper angle direction', 'commutation', '', 0, 1, writable=True, integer=True, choices={'0':'Normal','1':'Reverse'}, note='Equivalent to swapping M2 / M3; resets commutation.'),
    parameter('CA[28]', 'Motor phase connection bitmask', 'commutation', note='Read only: phase assignment spans the shared drive.'),
    parameter('MO', 'Motor enable state', 'drive'),
    parameter('UM', 'Drive control mode', 'drive', writable=True, integer=True, choices={'0':'Disabled','1':'Current / torque','2':'Velocity','3':'Stepper open loop','5':'Position','6':'Stepper open loop (HT / FF)','7':'Stepper closed loop'}, note='The mount Enable/Jog supports modes 2 and 5; Goto requires mode 5. Other modes can be configured but motion in those modes is not exposed.'),
    parameter('MF', 'Motion fault bitmask', 'drive'),
    parameter('SO', 'Servo ready', 'drive'),
    parameter('EE[5]', 'Enable failure reason', 'drive'),
    parameter('EE[2]', 'Extended fault reason', 'drive'),
    parameter('EC', 'Interpreter error code', 'drive'),
    parameter('SR', 'Drive status bitmask', 'drive'),
    parameter('BV', 'DC bus voltage', 'drive', 'V'),
]
PARAMETERS += [
    parameter('VL[3]', 'Lower position limit', 'limits', 'drive units', -(2**52), 2**52-1, writable=True),
    parameter('VH[3]', 'Upper position limit', 'limits', 'drive units', -(2**52), 2**52-1, writable=True),
    parameter('VH[2]', 'Velocity limit', 'limits', 'drive units / s', 0, 1e11, writable=True),
    parameter('AC[1]', 'Acceleration', 'limits', 'drive units / s2', 1, 1e14, writable=True),
    parameter('DC', 'Deceleration', 'limits', 'drive units / s2', 1, 1e14, writable=True),
    parameter('CL[1]', 'Continuous current limit', 'limits', 'A', 0, writable=True, cap='MC[4]'),
]
for i,label in {1:'Position encoder resolution numerator',2:'Position encoder resolution denominator',5:'Gear ratio numerator',6:'Gear ratio denominator',7:'Feed constant numerator',8:'Feed constant denominator',9:'Velocity factor numerator',10:'Velocity factor denominator',11:'Acceleration factor numerator',12:'Acceleration factor denominator'}.items():
    PARAMETERS.append(parameter(f'FC[{i}]',label,'scaling','',1,2**32-1,writable=True,integer=True))
for p in PARAMETERS:
    if p['group']=='commutation': p['writable']=False
CATALOGUE = {p['command']: p for p in PARAMETERS}


def sensor_catalogue(socket, kind):
    if socket not in range(1,9):raise DriveError('Socket must be 1–8')
    types={'1':'Quadrature','3':'Analog Sin/Cos','5':'BiSS','6':'Panasonic','9':'EnDAT','18':'SSI'}
    params=[parameter(f'S{socket}[1]','Sensor type','sensor',writable=True,integer=True,choices=types),
            parameter(f'S{socket}[2]','Port / direction / protocol flags','sensor',note='Raw diagnostic; configure type and protocol fields below.'),
            parameter(f'S{socket}[3]','FIR filter window','sensor','',0,8,writable=True,integer=True)]
    if kind in (5,6,9,18):
        params += [parameter(f'S{socket}_PORT','Sensor hardware port','sensor',writable=True,integer=True,choices={'0':'Port A1','8':'Port A2','16':'Port A3','24':'Port A4'}),
                   parameter(f'S{socket}_REVERSE','Encoder counting direction','sensor',writable=True,integer=True,choices={'0':'Normal','1':'Reverse'})]
        clocks={5:[1250000,2500000,5000000,10000000],6:[2500000],9:[156000,1250000,2500000,5000000],18:[312500,625000,1250000,2500000]}[kind]
        bits=[17,20,23] if kind==6 else list(range(8,49))
        params += [parameter(f'S{socket}[6]','Serial clock frequency','sensor','Hz',writable=True,integer=True,choices={str(v):str(v) for v in clocks}),
                   parameter(f'S{socket}[7]','Single-turn encoder resolution','sensor','bits',writable=True,integer=True,choices={str(v):f'{v} bits' for v in bits}),
                   parameter(f'S{socket}[8]','Multi-turn encoder resolution','sensor','bits',0,24,writable=True,integer=True,choices={'0':'0 bits','16':'16 bits'} if kind==6 else None),
                   parameter(f'S{socket}[10]','Reduced position bits','sensor','bits',0,12,writable=True,integer=True)]
        if kind in (5,18):
            params += [parameter(f'S{socket}[9]','Protocol frame length','sensor','bits',14 if kind==5 else 8,64,writable=True,integer=True),
                       parameter(f'S{socket}[11]','Position LSB bit','sensor','bit index',0,63,writable=True,integer=True)]
    elif kind in (1,3):
        params.append(parameter(f'S{socket}[5]','Glitch filter','sensor','ns',40,5140,writable=True,integer=True))
        if kind==3:params.append(parameter(f'S{socket}[6]','Sin/Cos multiplication factor','sensor','',2,16,writable=True,integer=True))
    order={7:0,1:1,8:2,6:3,3:6,9:7,10:8,11:9,2:10,5:5}
    params.sort(key=lambda p:4 if p['command'].endswith('_PORT') else 5 if p['command'].endswith('_REVERSE') else order.get(int(p['command'].split('[')[1][:-1]),20))
    return params


def validate(command, value):
    value = float(value)
    p = CATALOGUE.get(command)
    if p is None or not p['writable']:
        raise DriveError('Parameter is not editable in this workbench')
    if not math.isfinite(value):
        raise DriveError('Value must be finite')
    if p['integer'] and not value.is_integer():
        raise DriveError('Value must be an integer')
    if p['minimum'] is not None and value < p['minimum'] or p['maximum'] is not None and value > p['maximum']:
        raise DriveError('Value is outside the documented range')
    if p['choices'] and str(int(value)) not in p['choices']:
        raise DriveError('Unsupported parameter option')
    return p


class HardwareWorkbench:
    def __init__(self, mount):
        self.mount = mount

    def drive(self, axis_id):
        if axis_id not in AXIS_MAP:
            raise DriveError('Unknown axis')
        drive = self.mount.transport
        if not isinstance(drive, SerialElmoDrive) or not drive.connected:
            raise DriveError('Connect to the real Platinum drive first')
        return drive, AXIS_MAP[axis_id]

    async def sensor_read(self, axis_id, socket):
        async with self.mount._lock:
            drive,axis=self.drive(axis_id)
            if socket not in range(1,9):raise DriveError('Invalid sensor socket')
            kind=int(float(await drive.query(f'S{socket}[1]')))
            catalogue=sensor_catalogue(socket,kind)
            values=[]
            for p in catalogue:
                try:
                    command=p['command']
                    raw=await drive.query(f'S{socket}[2]' if '_' in command else command)
                    value=str((int(float(raw))>>1)&63) if command.endswith('_PORT') else str(int(float(raw))&1) if command.endswith('_REVERSE') else raw
                    values.append(dict(command=command,value=value,error=None))
                except DriveError as exc:values.append(dict(command=p['command'],value=None,error=str(exc)))
            return dict(axis=axis_id,socket=socket,sensor_type=kind,catalogue=catalogue,parameters=values,read_at=datetime.now(timezone.utc).isoformat())

    async def sensor_write(self,axis_id,socket,command,value,expected):
        if socket not in range(1,9):raise DriveError('Invalid sensor socket')
        async with self.mount._lock:
            drive,axis=self.drive(axis_id)
            def apply():
                if any(drive.configuration_errors.values()):raise DriveError('Reconnect to verify uncertain configuration before another edit')
                for a in AXES:
                    if float(drive._query_sync(f'AX{a.number}.MO'))!=0 or drive.commands[a.id] or drive.targets[a.id] is not None:
                        raise DriveError('Sensor sockets are shared: disable and stop all four motors before editing')
                kind=int(float(drive._query_sync(f'S{socket}[1]')))
                p=next((p for p in sensor_catalogue(socket,kind) if p['command']==command),None)
                if not p or not p['writable']:raise DriveError('Unsupported sensor parameter')
                if not math.isfinite(value) or not math.isfinite(expected) or not value.is_integer():raise DriveError('Sensor parameter must be a finite integer')
                if p['choices'] and str(int(value)) not in p['choices']:raise DriveError('Unsupported sensor option')
                if p['minimum'] is not None and value<p['minimum'] or p['maximum'] is not None and value>p['maximum']:raise DriveError('Sensor value outside range')
                native=f'S{socket}[2]' if '_' in command else command
                original=int(float(drive._query_sync(native))) if '_' in command else None
                before=(original>>1)&63 if command.endswith('_PORT') else original&1 if command.endswith('_REVERSE') else float(drive._query_sync(command))
                if before!=expected:raise DriveError('Sensor value changed; Read again')
                flags=int(float(drive._query_sync(f'S{socket}[2]')))
                if kind in (5,18) and not (kind==5 and flags & (1<<12)) and command in [f'S{socket}[{i}]' for i in (7,8,9,11)]:
                    fields={i:int(float(drive._query_sync(f'S{socket}[{i}]'))) for i in (7,8,9,11)}
                    fields[int(command.split('[')[1][:-1])]=int(value)
                    if fields[7]+fields[8]+fields[11]>fields[9]:raise DriveError('Position bits, multi-turn bits and LSB exceed protocol frame length')
                self.audit(dict(command=command,before=before,requested=value,status='requested'))
                for a in AXES:drive.configuration_errors[a.id]='Shared feedback configuration uncertain; reconnect and verify before motion'
                requested=(original & ~(63<<1))|(int(value)<<1) if command.endswith('_PORT') else (original & ~1)|int(value) if command.endswith('_REVERSE') else value
                drive._query_sync(native+'='+format(requested,'.17g'))
                raw=float(drive._query_sync(native))
                after=(int(raw)>>1)&63 if command.endswith('_PORT') else int(raw)&1 if command.endswith('_REVERSE') else raw
                if after!=value:raise DriveError('Sensor readback mismatch')
                for a in AXES:drive.configuration_errors.pop(a.id,None)
                self.audit(dict(command=command,before=before,value=after,status='verified'))
                return dict(command=command,value=after,saved_to_flash=False)
            return await drive._job(apply,timeout_seconds=6)

    async def read(self, axis_id, group):
        if group not in {'motor', 'encoder', 'commutation', 'drive', 'scaling', 'limits'}:
            raise DriveError('Unknown parameter group')
        values = []
        async with self.mount._lock:
            drive, axis = self.drive(axis_id)
            for p in PARAMETERS:
                if p['group'] != group:
                    continue
                try:
                    value = str(drive.resolutions.get(axis_id,axis.counts_per_rev)) if p["command"]=="MOUNT_CPR" else await drive.query(f"AX{axis.number}.{p['command']}")
                    if p['command']=='VH[2]':cache_velocity_limit(drive,axis,value)
                    values.append(dict(command=p['command'], value=value, error=None))
                except DriveError as exc:
                    values.append(dict(command=p['command'], value=None, error=str(exc)))
        return dict(axis=axis_id, group=group, parameters=values, read_at=datetime.now(timezone.utc).isoformat())

    def audit(self, record):
        if getattr(self,'audit_store',None):self.audit_store.enqueue('hardware.'+record.get('status','event'),record)
        path = Path(__file__).parent.parent / 'runtime' / 'hardware-audit.jsonl'
        path.parent.mkdir(exist_ok=True)
        with path.open('a', encoding='utf-8') as f:
            f.write(json.dumps(dict(time=datetime.now(timezone.utc).isoformat(), **record))+'\n')

    async def write(self, axis_id, command, value, expected):
        p = validate(command, value)
        if not math.isfinite(expected):
            raise DriveError('Previous value must be finite')
        async with self.mount._lock:
            drive, axis = self.drive(axis_id)
            prefix = f'AX{axis.number}.'
            if command in ('VL[3]', 'VH[3]'):
                f=drive.scaling.get(axis_id,[1.]*12)
                step=(f[6]/f[7])*(f[5]/f[4])*(f[1]/f[0])
                if not math.isfinite(step) or step<=0:raise DriveError('Read valid encoder scaling before setting limits')
                value=(math.ceil(value/step) if command=='VL[3]' else math.floor(value/step))*step
                validate(command,value)
            def number(command):
                try:
                    result = float(drive._query_sync(command))
                except ValueError as exc:
                    raise DriveError('Non-numeric parameter response') from exc
                if not math.isfinite(result):
                    raise DriveError('Non-finite parameter response')
                return result
            def edit():
                if drive.configuration_errors.get(axis_id):raise DriveError('Reconnect to verify uncertain configuration before another edit')
                # Recheck in the serial owner, immediately before the write.
                if number(prefix+'MO') != 0:
                    raise DriveError('Disable this motor before editing its configuration')
                if drive.commands.get(axis_id, 0) or drive.targets.get(axis_id) is not None:
                    raise DriveError('Stop the axis before editing configuration')
                before = drive.resolutions.get(axis_id,axis.counts_per_rev) if command=='MOUNT_CPR' else number(prefix+command)
                if before != expected:
                    raise DriveError('Value changed since Read; refresh before writing')
                if command in ('VL[3]', 'VH[3]'):
                    other = number(prefix + ('VH[3]' if command == 'VL[3]' else 'VL[3]'))
                    if (command == 'VL[3]' and value >= other) or (command == 'VH[3]' and value <= other):
                        raise DriveError('CCW limit must be lower than CW limit' if axis_id == 'azimuth' else 'Lower limit must be lower than Upper limit')
                if p['cap']:
                    cap = number(prefix+p['cap'])
                    if not math.isfinite(cap) or value > cap:
                        raise DriveError(f"Value exceeds drive ceiling {p['cap']} = {cap}")
                self.audit(dict(axis=axis_id, command=command, before=before, requested=value, status='requested'))
                if command=='MOUNT_CPR':
                    updated=json.loads(json.dumps(self.mount.config))
                    updated.setdefault('axis_resolutions',{})[axis_id]=int(value)
                    path=Path(__file__).parent.parent/'config.json'
                    temp=path.with_suffix('.tmp')
                    temp.write_text(json.dumps(updated,indent=2),encoding='utf-8')
                    temp.replace(path)
                    self.mount.config=updated
                    drive.resolutions[axis_id]=int(value)
                    after=value
                else:
                    sensitive = command.startswith(('FC[','SA[')) or command in ('UM','CA[18]')
                    if sensitive: drive.configuration_errors[axis_id]='Configuration write pending or uncertain; Read this configuration before motion'
                    serialized=str(int(value)) if value.is_integer() else format(value, '.12g' if command in ('VL[3]','VH[3]') else '.17g')
                    drive._query_sync(prefix+command+'='+serialized)
                    after = number(prefix+command)
                if not math.isfinite(after) or not math.isclose(after, value, rel_tol=1e-6, abs_tol=1e-8):
                    raise DriveError(f'Write readback mismatch: received {after}')
                if command.startswith('FC['):
                    factors=drive.scaling.setdefault(axis_id,[1.] * 12)
                    factors[int(command[3:-1])-1]=after
                if command in ('VL[3]', 'VH[3]'):
                    drive.position_limits[axis_id] = (after, other) if command == 'VL[3]' else (other, after)
                if command=='UM':drive.unit_modes[axis_id]=int(after)
                if command=='VH[2]':cache_velocity_limit(drive,axis,after)
                drive.configuration_errors.pop(axis_id,None)
                return dict(axis=axis_id, command=command, before=before, value=after, saved_to_flash=False)
            try:
                result = await drive._job(edit)
            except Exception as exc:
                self.audit(dict(axis=axis_id, command=command, requested=value, status='failed', error=str(exc)))
                raise
            self.audit(dict(**result, status='verified'))
            self.mount.event('info', 'Hardware parameter updated', f'{prefix}{command}: {result["before"]} → {result["value"]} (RAM)')
            return result

    async def save(self):
        async with self.mount._lock:
            drive, _ = self.drive('azimuth')
            def save():
                for axis in AXES:
                    if float(drive._query_sync(f'AX{axis.number}.MO')) != 0 or drive.commands.get(axis.id, 0) or drive.targets.get(axis.id) is not None:
                        raise DriveError('All four motors must be disabled and stopped before saving')
                self.audit(dict(command='SV', status='requested'))
                # EAS II's Platinum SaveParametersToFlash uses a 6000 ms timeout.
                response = drive._query_sync('SV', timeout_seconds=6)
                refresh_after_flash(drive)
                return response
            try:
                response = await drive._job(save, timeout_seconds=12)
            except Exception as exc:
                self.audit(dict(command='SV', status='failed', error=str(exc)))
                raise
            self.audit(dict(command='SV', status='acknowledged', response=response))
            self.mount.event('info', 'Drive parameters saved', 'SV acknowledged · shared drive flash')
            return dict(acknowledged=True, response=response, configuration_refreshed=True)
