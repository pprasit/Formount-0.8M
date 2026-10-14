import asyncio
import copy
from types import SimpleNamespace
from unittest.mock import AsyncMock
import pytest
from mount_service.drive_topology import DriveTopology
from mount_service.transports import SerialElmoDrive

def make(tmp_path):
    mount=SimpleNamespace(config={'port':'COM3','serial_number':'123','baud':921600,'poll_interval_ms':5},transport=SimpleNamespace(connected=True,port_name='COM3'),connect=AsyncMock(),disconnect=AsyncMock())
    return DriveTopology(mount,tmp_path)

def test_mapping_validation_and_persistence(tmp_path):
    t=make(tmp_path);g=copy.deepcopy(t.groups);g['imaging'].update(port='COM5',serial_number='456');t.save(g)
    assert DriveTopology(t.mount,tmp_path).groups['imaging']['port']=='COM5'
    bad=copy.deepcopy(g);bad['imaging']['port']='COM3'
    with pytest.raises(ValueError):t.save(bad)
    bad=copy.deepcopy(g);bad['mount']['axes']['azimuth']=2
    with pytest.raises(ValueError):t.save(bad)
    bad=copy.deepcopy(g);bad['mount']['port']='COM7'
    with pytest.raises(ValueError):t.save(bad)

def test_scan_skips_owned_port_and_only_queries_identity(tmp_path,monkeypatch):
    t=make(tmp_path);opened=[];commands=[]
    class Port:
        def __init__(self,port,*a,**kw):opened.append(port)
        def __enter__(self):return self
        def __exit__(self,*a):pass
        def write(self,data):commands.append(data)
        def read_until(self,*a,**kw):return b'SN[4]\r456;'
    monkeypatch.setattr('mount_service.drive_topology.list_ports.comports',lambda:[SimpleNamespace(device=p,description='Elmo',serial_number=None) for p in ['COM3','COM5']])
    monkeypatch.setattr('mount_service.drive_topology.serial.Serial',Port)
    result=t.scan();assert result['drive_count']==2;assert opened==['COM5'];assert commands==[b'SN[4]\r']

def test_second_drive_identity_and_independent_disconnect(tmp_path,monkeypatch):
    t=make(tmp_path);t.groups['imaging'].update(port='COM5',serial_number='456')
    class Port:
        def __init__(self,port,*a,**kw):self.port=port;self.is_open=True
        def write(self,data):pass
        def read_until(self,*a,**kw):return b'456;'
        def close(self):self.is_open=False
    monkeypatch.setattr('mount_service.drive_topology.serial.Serial',Port)
    async def run():
        await t.connect('imaging');assert t.snapshot()['connected']=={'mount':True,'imaging':True}
        await t.disconnect('imaging');t.mount.disconnect.assert_not_awaited()
        t.groups['imaging']['serial_number']='wrong'
        with pytest.raises(Exception):await t.connect('imaging')
        assert t.port is None
    asyncio.run(run())

def test_axis_commands_use_mapped_channel():
    class Port:
        in_waiting=0
        def reset_input_buffer(self):pass
        def write(self,data):self.sent=data
        def read_until(self,*a,**kw):return self.sent+b'123;'
    d=SerialElmoDrive('COM3',921600);d.connected=True;d._port=Port();d.axis_channels={'azimuth':4}
    assert d._query_sync('AX1.PX')=='123';assert d._port.sent==b'AX4.PX\r'


def test_cross_drive_mapping_unique_and_persisted(tmp_path):
    t=make(tmp_path);mapping=copy.deepcopy(t.axis_mapping)
    mapping['azimuth'],mapping['focuser1']=mapping['focuser1'],mapping['azimuth']
    t.save_axis_mapping(mapping)
    assert t.snapshot()['mapping_pending']
    restored=make(tmp_path)
    assert restored.axis_mapping['azimuth']=={'drive':2,'channel':1}
    bad=copy.deepcopy(mapping);bad['m3']=bad['azimuth']
    with pytest.raises(ValueError):t.save_axis_mapping(bad)
    channels,external=restored.configure_routes()
    assert channels['azimuth']==1 and 'azimuth' in external

def test_cross_drive_command_and_physical_query_routing():
    class Port:
        timeout=.35
        def write(self,data):self.sent=data
        def read_until(self,*a,**kw):return self.sent+b'123;'
    d=SerialElmoDrive('COM3',921600);d.connected=True;d._port=Port()
    sent=[];d.axis_channels={'azimuth':2};d.axis_external_queries={'azimuth':lambda command:sent.append(command) or '456'}
    assert d._query_sync('AX1.PX')=='456'
    assert sent==['AX2.PX']
    assert d._query_sync('AX1.PX',physical=True)=='123'
    assert d._port.sent==b'AX1.PX\r'
    assert d._query_sync('IP[1]')=='456'
    d.connected=False
    with pytest.raises(Exception):d._query_sync('AX1.MO=1')
    assert len(sent)==2
