import asyncio
from types import SimpleNamespace
from unittest.mock import patch
import pytest
from mount_service.moving_demo import MovingDemo, dwell_seconds
from mount_service.observatory import Observatory


def test_orbit_dwell_classification():
    def tle(n): return {'tle': {'line2': ' ' * 52 + f'{n:11.8f}'}}
    assert dwell_seconds(tle(15.5)) == 15
    assert dwell_seconds(tle(2)) == 5
    assert dwell_seconds(tle(1)) == 5


def test_demo_resets_dwell_on_departure_and_stops():
    async def check():
        clock=[0.0]; starts=[]; stops=[]
        axes=[dict(id=n,valid=True,enabled=True) for n in ('azimuth','altitude')]
        o=SimpleNamespace(tracking=False,demo=SimpleNamespace(active=False),position_rms={},mount=SimpleNamespace(snapshot=lambda:{'axes':axes},event=lambda *a:None))
        async def stop(): o.tracking=False;stops.append(clock[0])
        async def rates(p): return {'slew_allowed':True},{'azimuth':0,'altitude':0}
        async def limits():return {'azimuth':1,'altitude':1}
        async def start(p):
            starts.append(clock[0]);o.tracking=True
            if len(starts)==2:raise asyncio.CancelledError()
        async def sleep(t):
            clock[0]+=t
            error=20 if 1.9<clock[0]<2.1 else 1
            o.position_rms=dict(samples=10,azimuth_arcsec=error,altitude_arcsec=error)
        o.stop_tracking=stop;o.mount.stop_all=stop;o.tracking_rates=rates;o.tracking_velocity_limits=limits;o.start_tracking=start
        d=MovingDemo(o)
        with patch('mount_service.moving_demo.time.monotonic',lambda:clock[0]),patch('mount_service.moving_demo.asyncio.sleep',sleep):
            with pytest.raises(asyncio.CancelledError):await d.run([{'body':'moon'},{'body':'mars'}])
        assert len(starts)==2
        assert starts[1]>=7.0 # five continuous seconds AFTER the departure
        assert not o.tracking and stops
    asyncio.run(check())


def test_disabled_axis_blocks_demo():
    async def check():
        o=SimpleNamespace(demo=SimpleNamespace(active=False),tracking=False,mount=SimpleNamespace(snapshot=lambda:{'axes':[dict(id=n,valid=True,enabled=False) for n in ('azimuth','altitude')]}))
        with pytest.raises(ValueError,match='Enable'):await MovingDemo(o).start([{'body':'moon'}])
    asyncio.run(check())


def test_position_rms_uses_squared_samples():
    from collections import deque
    o=SimpleNamespace(position_samples=deque(maxlen=10),track_servos={n:SimpleNamespace(maximum_velocity=1,velocity=lambda *a:0) for n in ('azimuth','altitude')},mount=SimpleNamespace(snapshot=lambda:{'axes':[dict(id=n,valid=True,enabled=True,position_deg=0,velocity_deg_s=0) for n in ('azimuth','altitude')]}))
    for error in (3,4):Observatory.tracking_commands(o,dict(encoder_azimuth_deg=error/3600,encoder_altitude_deg=0),dict(azimuth=0,altitude=0),.1)
    assert o.position_rms['azimuth_arcsec']==pytest.approx((12.5)**.5)
