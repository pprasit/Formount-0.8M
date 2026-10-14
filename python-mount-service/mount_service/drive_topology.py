"""Persisted two-drive topology; accessory telemetry owns its own serial port."""

import asyncio

import json

import threading

from pathlib import Path

import serial

from serial.tools import list_ports

from .transports import parse_reply

from .domain import DriveError



ROLES={'mount':['azimuth','altitude','rotator','m3'],'imaging':['focuser1','focuser2','imaging_rotator']}



class DriveTopology:

    def __init__(self,mount,directory):

        self.operation_lock=asyncio.Lock()

        self.mount=mount;self.path=directory/'drive-topology.json';self.port=None;self.task=None;self.lock=threading.Lock();self.readings={};self.error=None

        self.mapping_path=directory/'axis-mapping.json'

        self.axis_mapping={role:{'drive':1 if group=='mount' else 2,'channel':n} for group,roles in ROLES.items() for n,role in enumerate(roles,1)}

        self.axis_mapping['rotator2']=self.axis_mapping.pop('imaging_rotator')

        if self.mapping_path.exists():self.axis_mapping=json.loads(self.mapping_path.read_text())

        self.groups={'mount':{'port':mount.config['port'],'serial_number':mount.config['serial_number'],'baud':mount.config['baud'],'axes':dict(zip(ROLES['mount'],range(1,5)))},'imaging':{'port':'','serial_number':'','baud':921600,'axes':dict(zip(ROLES['imaging'],range(1,4)))}}

        if self.path.exists():self.groups.update(json.loads(self.path.read_text()))

        self.active_mapping=json.loads(json.dumps(self.axis_mapping))
        mount.config['axis_channels']=self.groups['mount']['axes']

        mount.transport.axis_channels=self.groups['mount']['axes']

        g=self.groups['mount']

        mount.config.update({k:g[k] for k in ('port','serial_number','baud')})

        if not mount.transport.connected and hasattr(mount.transport,'port_name'):

            mount.transport.port_name=g['port'];mount.transport.baud=g['baud']



    def snapshot(self):

        return {'mapping_pending':self.active_mapping!=self.axis_mapping,'axis_mapping':self.axis_mapping,'groups':self.groups,'connected':{'mount':self.mount.transport.connected,'imaging':self.port is not None and self.port.is_open},'imaging_readings':self.readings,'imaging_error':self.error}



    def save(self,groups):

        if self.operation_lock.locked():raise ValueError("Drive operation in progress")

        for role,axes in ROLES.items():

            g=groups[role]

            if set(g['axes'])!=set(axes) or len(set(g['axes'].values()))!=len(axes) or any(type(n)!=int or n<1 or n>4 for n in g['axes'].values()):raise ValueError('Each axis needs a distinct AX1-AX4 channel within its drive')

            if g['port'] and not g['port'].upper().startswith('COM'):raise ValueError('Select a local COM port')

            if g['baud'] not in (9600,19200,38400,57600,115200,230400,460800,921600):raise ValueError('Unsupported baud rate')

        if groups['mount']['port'] and groups['mount']['port'].upper()==groups['imaging']['port'].upper():raise ValueError('Mount and Imaging drives must use different COM ports')

        for role in ROLES:

            connected=self.mount.transport.connected if role=='mount' else self.port is not None

            if connected and groups[role]!=self.groups[role]:raise ValueError('Disconnect the '+role+' drive before changing its mapping')

        self.groups=groups;self.path.write_text(json.dumps(groups,indent=2))

        self.mount.config['axis_channels']=groups['mount']['axes']

        self.mount.config.update({k:groups['mount'][k] for k in ('port','serial_number','baud')})

        return self.snapshot()



    def save_axis_mapping(self,mapping):

        if self.operation_lock.locked():raise ValueError('Drive operation in progress')

        roles={'azimuth','altitude','rotator','m3','focuser1','focuser2','rotator2'}

        if set(mapping)!=roles:raise ValueError('Map all seven axes')

        slots=[]

        for slot in mapping.values():

            if set(slot)!={'drive','channel'} or type(slot['drive'])!=int or slot['drive'] not in (1,2) or type(slot['channel'])!=int or slot['channel'] not in (1,2,3,4):raise ValueError('Select Drive 1 or 2 and AX1–AX4')

            slots.append((slot['drive'],slot['channel']))

        if len(set(slots))!=len(slots):raise ValueError('A drive channel cannot be assigned to two axes')

        self.mapping_path.write_text(json.dumps(mapping,indent=2))

        self.axis_mapping=mapping

        return self.snapshot()



    def configure_routes(self):
        channels={role:self.axis_mapping[role]['channel'] for role in ROLES['mount']}
        external={role:self.external_query for role in ROLES['mount'] if self.axis_mapping[role]['drive']==2}
        self.mount.config['axis_channels']=channels
        self.mount.axis_external_queries=external
        self.active_mapping=json.loads(json.dumps(self.axis_mapping))
        return channels,external

    def external_query(self,command):
        with self.lock:
            if not self.port or not self.port.is_open:raise DriveError('Mapped Drive 2 is disconnected')
            return self.query(command)

    async def prepare_mount_connection(self):
        if any(self.axis_mapping[r]['drive']==2 for r in ROLES['mount']) and not self.port:
            await self.connect('imaging')
        return self.configure_routes()

    def query(self,command):

        self.port.write((command+'\r').encode('ascii'))

        return parse_reply(command,self.port.read_until(b';',size=1024))



    async def connect(self,role):

        async with self.operation_lock:return await self._connect(role)



    async def _connect(self,role):

        g=self.groups[role]

        if not g['port'] or not g['serial_number']:raise ValueError('Select a COM port and expected serial number first')

        if role=='mount':

            if self.port and self.port.port.upper()==g['port'].upper():raise ValueError('COM port already owned by Imaging drive')

            if self.mount.transport.connected:raise ValueError('Mount drive is already connected')

            if any(self.axis_mapping[r]['drive']==2 for r in ROLES['mount']) and not self.port:await self._connect('imaging')
            self.configure_routes()
            await self.mount.connect('hardware',g['port'],g['baud'],g['serial_number'],self.mount.config['poll_interval_ms'])

            return self.snapshot()

        if self.port:raise ValueError('Imaging drive is already connected')

        if self.mount.transport.connected and self.mount.transport.port_name.upper()==g['port'].upper():raise ValueError('COM port already owned by Mount drive')

        def open_drive():

            self.port=serial.Serial(g['port'],g['baud'],timeout=.35,write_timeout=.35)

            try:

                if self.query('SN[4]')!=g['serial_number']:raise DriveError('Imaging drive serial number does not match')

            except Exception:

                self.port.close();self.port=None;raise

        await asyncio.to_thread(open_drive)

        self.task=asyncio.create_task(self.poll())

        return self.snapshot()



    async def poll(self):
        while self.port:
            try:
                readings={}
                for role in ('focuser1','focuser2','rotator2'):
                    slot=self.active_mapping[role];channel=slot['channel']
                    async def read(command):
                        if slot['drive']==1:return await self.mount.transport.query_physical(command)
                        return await asyncio.to_thread(self.external_query,command)
                    readings[role]={'position_counts':float(await read(f'AX{channel}.PX')),'enabled':await read(f'AX{channel}.MO')=='1','drive':slot['drive'],'channel':channel}
                self.readings=readings;self.error=None
            except Exception as exc:self.error=str(exc)
            await asyncio.sleep(.3)

    async def disconnect(self,role):

        async with self.operation_lock:return await self._disconnect(role)



    async def _disconnect(self,role):

        if role=='mount':await self.mount.disconnect()

        else:

            if self.task:

                self.task.cancel()

                try:await self.task

                except asyncio.CancelledError:pass

                self.task=None

            def close():

                with self.lock:

                    if self.port:self.port.close();self.port=None

            await asyncio.to_thread(close);self.readings={}

        return self.snapshot()



    def scan(self):

        occupied={getattr(self.mount.transport,'port_name',self.groups['mount']['port']).upper()} if self.mount.transport.connected else set()

        if self.port:occupied.add(self.groups['imaging']['port'].upper())

        results=[]

        for p in list_ports.comports():

            row={'port':p.device,'description':p.description,'usb_serial':p.serial_number,'identity':None,'baud':None,'in_use':p.device.upper() in occupied}

            if row['in_use']:

                group=next((g for g in self.groups.values() if g['port'].upper()==p.device.upper()),self.groups['mount']);row.update(identity=group['serial_number'],baud=group['baud'])

            else:

                for baud in dict.fromkeys([self.groups['mount']['baud'],self.groups['imaging']['baud'],921600,115200]):

                    try:

                        with serial.Serial(p.device,baud,timeout=.25,write_timeout=.25) as port:

                            port.write(b'SN[4]\r');identity=parse_reply('SN[4]',port.read_until(b';',size=1024))

                            if identity.isdigit():row.update(identity=identity,baud=baud);break

                    except Exception:continue

            results.append(row)

        return {'ports':results,'drive_count':sum(bool(p['identity']) for p in results)}

