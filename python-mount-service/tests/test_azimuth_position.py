import pytest
from mount_service.azimuth_position import AzimuthPosition
from mount_service.domain import DriveError, AXIS_MAP
from mount_service.transports import SerialElmoDrive

@pytest.mark.parametrize('raw,inputs,expected',[(350,1<<17,-10),(12,1<<16,12),(350,0,350),(0,1<<17,0),(720+10,1<<16,10)])
def test_boot_region(raw,inputs,expected):
    p=AzimuthPosition();assert p.update(raw,inputs)==pytest.approx(expected)

def test_continuous_crossings_do_not_reset_with_sensor_changes():
    p=AzimuthPosition();assert p.update(350,1<<17)==-10
    assert p.update(359,0)==-1
    assert p.update(1,1<<16)==1
    assert p.update(359,1<<17)==-1
    assert p.boot_region=='CCW'

def test_multiple_positive_turns():
    p=AzimuthPosition();p.update(10,0)
    for angle in [100,190,280,10,100,190,280,10]:p.update(angle,0)
    assert p.position==730

@pytest.mark.parametrize('inputs',[None,(1<<16)|(1<<17)])
def test_unknown_or_contradictory_boot_sensors_block_initialization(inputs):
    with pytest.raises(DriveError):AzimuthPosition().update(350,inputs)

def test_negative_logical_target_maps_back_to_native_absolute():
    d=SerialElmoDrive('TEST',921600);a=AXIS_MAP['azimuth']
    d.digital_inputs={'1':1<<17}
    d.position_degrees(a,a.counts_per_rev*350/360)
    assert d.position_units(a,-20)==pytest.approx(round(a.counts_per_rev*340/360))

@pytest.mark.parametrize('target',[0.,-100.,100.])
def test_service_goto_accepts_signed_targets_within_live_drive_limits(target):
    import asyncio,time
    from mount_service.service import MountService
    from mount_service.domain import Reading
    s=MountService(simulation=True);d=SerialElmoDrive('TEST',921600);s.transport=d;d.connected=True
    d.azimuth_position.update(16,1<<16)
    axis=AXIS_MAP['azimuth']
    s.readings['azimuth']=Reading(16,0,0,True,time.perf_counter(),axis.deg_to_counts(16))
    commands=[]
    d._query_sync=lambda c:str((-290 if c.endswith('VL[3]') else 290)*axis.counts_per_rev/360)
    async def job(fn,**kwargs):return fn()
    async def goto(a,value,speed):commands.append(value)
    d._job=job;d.goto=goto
    asyncio.run(s.command('azimuth','goto',target))
    assert commands==[target]

@pytest.mark.parametrize('target',[-291.,291.])
def test_service_goto_rejects_outside_live_drive_limits_before_motion(target):
    import asyncio,time
    from mount_service.service import MountService
    from mount_service.domain import Reading
    s=MountService(simulation=True);d=SerialElmoDrive('TEST',921600);s.transport=d;d.connected=True
    d.azimuth_position.update(16,1<<16);axis=AXIS_MAP['azimuth']
    s.readings['azimuth']=Reading(16,0,0,True,time.perf_counter(),axis.deg_to_counts(16))
    d._query_sync=lambda c:str((-290 if c.endswith('VL[3]') else 290)*axis.counts_per_rev/360)
    async def job(fn,**kwargs):return fn()
    async def goto(*args):raise AssertionError('Motion must not be sent')
    d._job=job;d.goto=goto
    with pytest.raises(DriveError,match='drive limits'):asyncio.run(s.command('azimuth','goto',target))

@pytest.mark.parametrize('inputs',[1<<16,1<<17,0])
def test_startup_near_zero_does_not_latch_an_extra_turn(inputs):
    p=AzimuthPosition()
    assert p.update(-.01,inputs)==pytest.approx(-.01)
    assert p.update(.011,inputs)==pytest.approx(.011)
    assert p.raw_to_logical_offset==pytest.approx(0,abs=1e-9)

@pytest.mark.parametrize('inputs',[1<<16,1<<17,0])
def test_startup_just_positive_zero_does_not_become_minus_360(inputs):
    p=AzimuthPosition()
    assert p.update(.01,inputs)==pytest.approx(.01)
    assert p.update(-.011,inputs)==pytest.approx(-.011)
