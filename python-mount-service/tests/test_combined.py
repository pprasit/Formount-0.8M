import asyncio
import time
from fastapi.testclient import TestClient
import pytest

from mount_service.api import create_app
from mount_service.domain import AXES,DriveError,Reading
from mount_service.service import MountService
from mount_service.transports import SerialElmoDrive
from mount_service.regions import travel_region,input_snapshot


def test_region_preserves_two_mechanical_turns_and_limits():
    assert travel_region(50,20)['turn']==0
    assert travel_region(410,20)['turn']==1
    assert travel_region(410,20)['azimuth_in_range']
    assert not travel_region(12,20)['azimuth_in_range']
    assert not travel_region(701,20)['azimuth_in_range']
    assert not travel_region(50,91)['altitude_in_range']
    assert travel_region(None,None)['name']=='Unknown'


def test_bitmask_supports_both_sensors_and_unknown_on_read_failure():
    s=input_snapshot({'1':(1<<16)|(1<<17)|1},time.perf_counter())
    assert s['azimuth']['cw'] and s['azimuth']['ccw']
    assert s['active_bits']['1']==[0,16,17]
    assert not s['azimuth']['mapping_verified']
    assert input_snapshot({'1':1},time.perf_counter(),'timeout')['azimuth']['cw'] is None


def prepared():
    d=SerialElmoDrive('OFFLINE-MOCK',921600);d.azimuth_position.update(0,0);commands=[]
    for axis in AXES[:2]:
        d.unit_modes[axis.id]=5;d._motor_states[axis.id]=True
        d.latest[axis.id]=Reading(100,0,0,True,time.perf_counter(),0)
    d._query_sync=lambda s:commands.append(s) or '0'
    async def job(fn,**kwargs):return fn()
    d._job=job
    return d,commands


def test_combined_stages_both_axes_before_either_bg():
    d,commands=prepared();asyncio.run(d.goto_pair(100,40,2,3))
    assert commands[-2:]==['AX1.BG','AX2.BG']
    assert any(s.startswith('AX1.PA=') for s in commands[:-2])
    assert any(s.startswith('AX2.PA=') for s in commands[:-2])
    assert d.targets['azimuth']==(100,2) and d.targets['altitude']==(40,3)


def test_combined_partial_failure_stops_both_axes():
    d,commands=prepared()
    def query(s):
        commands.append(s)
        if s.startswith('AX2.PA='):raise DriveError('rejected')
        return '0'
    d._query_sync=query
    with pytest.raises(DriveError):asyncio.run(d.goto_pair(100,40,2,3))
    assert not any('.BG' in s for s in commands)
    assert commands[-2:]==['AX1.ST','AX2.ST']
    assert all(d.targets[a.id] is None for a in AXES[:2])


def test_combined_endpoint_validates_both_targets_before_writes():
    with TestClient(create_app(MountService(simulation=True))) as c:
        body=dict(azimuth=100,altitude=40,azimuth_speed=2,altitude_speed=3)
        assert c.post('/api/axes/combined/goto',json={**body,'altitude':91}).status_code==409
        assert c.app.state.mount.transport.targets['azimuth'] is None
        c.post('/api/axes/altitude/command',json={'action':'disable'})
        assert c.post('/api/axes/combined/goto',json=body).status_code==409
        assert c.app.state.mount.transport.targets['azimuth'] is None
        c.post('/api/axes/altitude/command',json={'action':'enable'})
        assert c.post('/api/axes/combined/goto',json=body).status_code==200
        assert c.app.state.mount.transport.targets['azimuth']==(100,2)
