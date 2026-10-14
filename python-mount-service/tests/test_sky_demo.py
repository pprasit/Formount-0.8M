import asyncio
import time
from types import SimpleNamespace
import pytest
from mount_service.sky_demo import SkyDemo


class FakeObservatory:
    def __init__(self):
        self.axes = [dict(id=name, valid=True, enabled=True, position_deg=10., velocity_deg_s=0.) for name in ('azimuth', 'altitude')]
        self.stops = 0
        self.slews = []
        self.track_times = []
        self.tracking = False
        self.mount = SimpleNamespace(snapshot=lambda: {'axes': self.axes}, stop_all=self.stop_all, event=lambda *args: None)
        self.astro = SimpleNamespace(sky_catalog=lambda: {'objects': [dict(id=str(i), name=str(i), altitude_deg=20, ra_hours=i, dec_deg=20) for i in (1, 2)]})

    async def call(self, fn): return fn()
    async def stop_all(self): self.stops += 1
    async def stop_tracking(self): self.tracking = False
    async def preview(self, payload): return dict(slew_allowed=True, encoder_azimuth_deg=10., encoder_altitude_deg=10.)
    async def tracking_rates(self, payload): return await self.preview(payload), {}
    async def goto(self, payload): self.slews.append(payload)
    async def start_tracking(self, payload): self.tracking = True; self.track_times.append(time.monotonic())


def test_disabled_axis_rejects_demo_without_motion():
    async def check():
        o = FakeObservatory(); o.axes[1]['enabled'] = False
        with pytest.raises(ValueError, match='Enable'):
            await SkyDemo(o).start(['1'])
        assert not o.slews
    asyncio.run(check())


def test_demo_tracks_four_seconds_then_selects_different_object_and_stops():
    async def check():
        o = FakeObservatory(); demo = SkyDemo(o)
        async def settled(payload): pass
        demo.settle = settled
        await demo.start(['1', '2'])
        deadline = time.monotonic() + 6
        while len(o.slews) < 2 and time.monotonic() < deadline:
            await asyncio.sleep(.05)
        assert len(o.slews) == 2
        assert o.slews[0] != o.slews[1]
        assert o.track_times[1] - o.track_times[0] >= 4
        await demo.stop()
        count = len(o.slews)
        await asyncio.sleep(.15)
        assert not demo.active and not o.tracking and o.stops
        assert len(o.slews) == count
    asyncio.run(check())


def test_browser_lease_expiry_stops_during_slew():
    async def check():
        o = FakeObservatory(); demo = SkyDemo(o)
        async def blocked(payload): await asyncio.sleep(100)
        demo.settle = blocked
        result = await demo.start(['1'])
        with pytest.raises(ValueError): demo.heartbeat('wrong')
        demo.heartbeat(result['token'])
        demo.deadline = 0
        await asyncio.sleep(.6)
        assert not demo.active and o.stops
        assert 'connection lost' in demo.message
    asyncio.run(check())


def test_settle_waits_for_low_velocity_and_continuous_stability():
    async def check():
        o = FakeObservatory(); demo = SkyDemo(o)
        for a in o.axes: a['velocity_deg_s'] = .2
        task = asyncio.create_task(demo.settle({}))
        await asyncio.sleep(.15)
        assert not task.done()
        for a in o.axes: a['velocity_deg_s'] = 0
        await asyncio.sleep(.4)
        assert not task.done()
        await asyncio.wait_for(task, 2)
    asyncio.run(check())
