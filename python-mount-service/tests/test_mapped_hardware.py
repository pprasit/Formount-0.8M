import asyncio
from types import SimpleNamespace
import pytest
from mount_service.mapped_hardware import MappedHardware
from mount_service.drive_topology import DriveTopology
from mount_service.transports import SerialElmoDrive

class Port:
    is_open=True;timeout=.35
    def __init__(self):self.commands=[];self.values={'AX3.MO':0,'AX3.MP[1]':7,'AX1.MO':0,'AX2.MO':0,'AX4.MO':0}
    def write(self,data):
        self.command=data.decode().strip();self.commands.append(self.command)
        if '=' in self.command:
            key,value=self.command.split('=');self.values[key]=float(value)
    def read_until(self,*a,**kw):return (self.command+'\r'+str(self.values.get(self.command,0))+';').encode()

def setup(tmp_path):
    drive=SerialElmoDrive('COM3',921600);drive.connected=True;drive._port=Port()
    async def job(fn,**kwargs):return fn()
    drive._job=job
    for n in range(1,5):
        drive._port.values.update({f'AX{n}.FC[{i}]':1 for i in range(1,13)})
        drive._port.values.update({f'AX{n}.VH[2]':1000000,f'AX{n}.UM':5,f'AX{n}.VL[3]':-1000,f'AX{n}.VH[3]':1000})
    mount=SimpleNamespace(config={'port':'COM3','serial_number':'123','baud':921600,'poll_interval_ms':5},transport=drive,_lock=asyncio.Lock(),event=lambda *args:None)
    topology=DriveTopology(mount,tmp_path);topology.groups['imaging'].update(port='COM5',serial_number='456');topology.port=Port()
    topology.port.values.update(drive._port.values)
    return topology,MappedHardware(topology)

def test_mapped_accessory_reads_and_writes_only_drive2_channel(tmp_path):
    t,hw=setup(tmp_path);t.active_mapping['focuser1']={'drive':2,'channel':3};t.axis_mapping=t.active_mapping
    async def run():
        result=await hw.read('focuser1','motor')
        assert result['target']=={'drive':2,'channel':3,'serial_number':'456'}
        assert all(c.startswith('AX3.') for c in t.port.commands)
        hw.audit=lambda record:None
        await hw.write('focuser1','MP[1]',8,7)
        assert t.port.values['AX3.MP[1]']==8
        assert t.mount.transport._port.commands==[]
        t.port.values['AX3.MO']=1
        with pytest.raises(Exception):await hw.write('focuser1','MP[1]',9,8)
    asyncio.run(run())

def test_physical_mapping_does_not_remap_twice_and_rejects_stale_target(tmp_path):
    t,hw=setup(tmp_path);t.active_mapping['azimuth']={'drive':1,'channel':3};t.axis_mapping=t.active_mapping;t.mount.transport.axis_channels={'rotator':4}
    asyncio.run(hw.read('azimuth','motor'))
    assert all(c.startswith('AX3.') for c in t.mount.transport._port.commands)
    with pytest.raises(Exception):hw.verify_binding('azimuth',{'drive':1,'channel':1,'serial_number':'123'})

def test_flash_save_targets_only_selected_drive(tmp_path):
    t,hw=setup(tmp_path);hw.audit=lambda record:None
    asyncio.run(hw.save_drive(2))
    assert t.port.commands[:5]==['AX1.MO','AX2.MO','AX3.MO','AX4.MO','SV']
    assert 'AX1.FC[1]' in t.port.commands
    assert t.mount.transport._port.commands==[]
