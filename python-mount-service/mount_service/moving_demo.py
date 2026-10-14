"""Moving-target demo using the server tracking servo and browser lease."""
import asyncio
import math
import random
import time
from .sky_demo import SkyDemo


def dwell_seconds(payload):
    tle = payload.get('tle')
    if tle:
        # TLE mean motion, revolutions/day; LEO orbital periods are below 225 min.
        motion = float(tle['line2'][52:63])
        return 15 if motion > 6.4 else 5
    return 5


class MovingDemo(SkyDemo):
    def __init__(self, observatory):
        super().__init__(observatory)
        self.window = 10.0
        self.elapsed = 0.0
        self.dwell = 0

    def snapshot(self):
        return {**super().snapshot(), 'in_position_arcsec': self.window,
                'on_target_seconds': self.elapsed, 'tracking_seconds': self.dwell}

    async def start(self, objects, window=10):
        if self.active or self.o.demo.active or self.o.tracking:
            raise ValueError('Stop tracking or the current demo before starting Demo')
        self.ready()
        self.window = window
        self.token = __import__('uuid').uuid4().hex
        self.deadline = time.monotonic() + 10
        self.completed = 0
        self.task = asyncio.create_task(self.run(objects), name='moving-demo')
        self.watchdog = asyncio.create_task(self.watch(self.token), name='moving-demo-watchdog')
        return {**self.snapshot(), 'token': self.token}

    async def run(self, objects):
        previous = None
        try:
            while True:
                self.ready()
                candidates = [o for o in objects if o != previous] or objects[:]
                random.shuffle(candidates)
                chosen = None
                for payload in candidates:
                    try:
                        target, rates = await self.o.tracking_rates(payload)
                        limits = await self.o.tracking_velocity_limits()
                        if target['slew_allowed'] and all(abs(rates[k]) <= limits[k] for k in limits):
                            chosen = payload
                            break
                    except ValueError:
                        continue
                if chosen is None:
                    raise ValueError('No displayed objects are currently safe to track')
                previous = chosen
                self.target = {'payload': chosen, 'name': (chosen.get('tle') or chosen.get('orbit') or {}).get('name', chosen.get('body', 'Object'))}
                self.dwell = dwell_seconds(chosen)
                self.elapsed = 0
                self.state, self.message = 'acquiring', 'Acquiring ' + self.target['name']
                await self.o.start_tracking(chosen)
                stable = None
                timeout = time.monotonic() + 180
                while self.elapsed < self.dwell:
                    self.ready()
                    if not self.o.tracking:
                        raise ValueError('Demo stopped because tracking ended')
                    now = time.monotonic()
                    quality = self.o.position_rms
                    inside = quality.get('samples', 0) >= 5 and all(quality.get(k, math.inf) <= self.window for k in ('azimuth_arcsec', 'altitude_arcsec'))
                    stable = (stable if stable is not None else now) if inside else None
                    self.elapsed = now - stable if stable is not None else 0
                    self.state = 'on_target' if inside else 'acquiring'
                    self.message = f"{self.target['name']} · {self.elapsed:.1f} / {self.dwell} s on target"
                    if now > timeout:
                        raise ValueError('Demo target did not remain in the position window within 180 seconds')
                    await asyncio.sleep(.1)
                await self.o.stop_tracking()
                self.completed += 1
        except asyncio.CancelledError:
            raise
        except Exception as exc:
            self.state, self.message = 'error', str(exc)
            self.o.mount.event('error', 'Moving demo stopped', str(exc))
        finally:
            await self.o.stop_tracking()
            await self.o.mount.stop_all()
            if self.state != 'error':
                self.state = 'stopped'
