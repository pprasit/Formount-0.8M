"""Random catalog demonstration using the normal slew and tracking guards."""
import asyncio
import math
import random
import time
import uuid


class SkyDemo:
    def __init__(self, observatory):
        self.o = observatory
        self.task = None
        self.watchdog = None
        self.token = None
        self.deadline = 0
        self.state = 'idle'
        self.message = 'Ready'
        self.target = None
        self.completed = 0

    @property
    def active(self):
        return self.task is not None and not self.task.done()

    def snapshot(self):
        return dict(active=self.active, state=self.state, message=self.message,
                    target=self.target, completed=self.completed, tracking_seconds=4)

    def ready(self):
        axes = {a['id']: a for a in self.o.mount.snapshot()['axes']}
        for name in ('azimuth', 'altitude'):
            a = axes[name]
            if not a['valid'] or not a['enabled']:
                raise ValueError('Enable Azimuth and Altitude with fresh telemetry before Run Demo')
        return axes

    async def start(self, identifiers):
        if self.active:
            raise ValueError('Demo is already running')
        self.ready()
        if self.o.tracking:
            raise ValueError('Stop tracking before Run Demo')
        catalog = await self.o.call(self.o.astro.sky_catalog)
        ids = set(identifiers)
        objects = [o for o in catalog['objects'] if o['id'] in ids and o['altitude_deg'] > 0]
        if not objects:
            raise ValueError('No above-horizon objects displayed on the sky chart')
        self.ready()
        if self.active:
            raise ValueError('Demo is already running')
        self.token = uuid.uuid4().hex
        self.deadline = time.monotonic() + 10
        self.state, self.message, self.completed = 'selecting', 'Choosing a visible target…', 0
        self.task = asyncio.create_task(self.run(objects), name='sky-demo')
        self.watchdog = asyncio.create_task(self.watch(self.token), name='sky-demo-watchdog')
        return {**self.snapshot(), 'token': self.token}

    def heartbeat(self, token):
        if not self.active or token != self.token:
            raise ValueError('Demo session is no longer active')
        self.deadline = time.monotonic() + 10
        return self.snapshot()

    async def watch(self, token):
        while self.active and token == self.token:
            if time.monotonic() > self.deadline:
                await self.stop('Demo stopped · browser connection lost')
                break
            await asyncio.sleep(.5)

    async def stop(self, message='Demo stopped'):
        task = self.task
        if task and not task.done():
            self.state, self.message = 'stopping', message
            task.cancel()
            try:
                await task
            except asyncio.CancelledError:
                await self.o.stop_tracking()
                await self.o.mount.stop_all()
            if self.state != 'error':
                self.state, self.message = 'stopped', message
        return self.snapshot()

    async def settle(self, payload):
        deadline, stable = time.monotonic() + 180, None
        while time.monotonic() < deadline:
            axes = self.ready()
            target = await self.o.preview(payload)
            if not target['slew_allowed']:
                raise ValueError('Demo target moved outside the allowed travel')
            arrived = all(math.isfinite(axes[name]['position_deg']) and
                          math.isfinite(axes[name]['velocity_deg_s']) and
                          abs(axes[name]['position_deg'] - target[key]) <= .25 and
                          abs(axes[name]['velocity_deg_s']) <= .05
                          for name, key in (('azimuth', 'encoder_azimuth_deg'), ('altitude', 'encoder_altitude_deg')))
            now = time.monotonic()
            stable = (stable if stable is not None else now) if arrived else None
            if stable is not None and now - stable >= 1:
                return
            await asyncio.sleep(.1)
        raise ValueError('Demo slew did not settle within 180 seconds')

    async def run(self, objects):
        previous = None
        try:
            while True:
                self.ready()
                candidates = [o for o in objects if o['id'] != previous] or objects[:]
                random.shuffle(candidates)
                chosen = None
                for obj in candidates:
                    payload = {'body': obj['body']} if obj.get('body') else dict(ra_hours=obj['ra_hours'], dec_deg=obj['dec_deg'])
                    try:
                        target, _ = await self.o.tracking_rates(payload)
                    except ValueError:
                        continue
                    if target['slew_allowed']:
                        chosen = obj
                        break
                if chosen is None:
                    raise ValueError('No displayed targets are within the current travel limits')
                previous = chosen['id']
                self.target = dict(id=previous, name=chosen.get('name') or str(previous), payload=payload)
                self.state, self.message = 'slewing', 'Slewing to ' + self.target['name']
                await self.o.goto(payload)
                self.state, self.message = 'settling', 'Waiting for mount to settle · ' + self.target['name']
                await self.settle(payload)
                await self.o.start_tracking(payload)
                self.state, self.message = 'tracking', 'Tracking for 4 seconds · ' + self.target['name']
                until = time.monotonic() + 4
                while time.monotonic() < until:
                    self.ready()
                    if not self.o.tracking:
                        raise ValueError('Demo stopped because tracking ended')
                    await asyncio.sleep(.1)
                await self.o.stop_tracking()
                self.completed += 1
        except asyncio.CancelledError:
            raise
        except Exception as exc:
            self.state, self.message = 'error', str(exc)
            self.o.mount.event('error', 'Sky demo stopped', str(exc))
        finally:
            try:
                await self.o.stop_tracking()
                await self.o.mount.stop_all()
            except Exception as exc:
                self.state, self.message = 'error', 'Demo stop failed: ' + str(exc)
                self.o.mount.event('error', 'Sky demo stop failed', str(exc))
            if self.state != 'error':
                self.state = 'stopped'
