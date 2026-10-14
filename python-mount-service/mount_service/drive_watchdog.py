"""Reconnect stale hardware telemetry without rebooting or restoring motor commands."""
import asyncio
import time

class DriveWatchdog:
    def __init__(self, topology, before_reconnect):
        self.topology=topology
        self.before_reconnect=before_reconnect
        self.states={r:dict(status='idle',attempt=0,error=None) for r in ('mount','imaging')}
        self.disabled=set()
        self.bad_since={}
        self.next_attempt={}

    def manual_disconnect(self, role):
        self.disabled.add(role)
        self.states[role]=dict(status='idle',attempt=0,error=None)
        self.bad_since.pop(role,None)

    def manual_connect(self, role):
        self.disabled.discard(role)
        self.states[role]=dict(status='idle',attempt=0,error=None)
        self.bad_since.pop(role,None)

    def health(self, role):
        t=self.topology
        if role=='mount':
            s=t.mount.snapshot()
            return s['drive']['mode']=='hardware' and s['drive']['connected'],all(a['valid'] for a in s['axes'])
        return bool(t.port), bool(t.readings) and not t.error

    async def tick(self, now=None):
        live_clock=now is None
        now=time.monotonic() if live_clock else now
        for role,state in self.states.items():
            if role in self.disabled:continue
            connected,healthy=self.health(role)
            if healthy and connected:
                if state['status']=='reconnecting':self.topology.mount.event('info','Drive connection recovered',role)
                state.update(status='healthy',attempt=0,error=None)
                self.bad_since.pop(role,None)
                continue
            if state['status']=='lost':continue
            if state['status']=='idle' and not connected:continue
            since=self.bad_since.setdefault(role,now)
            if now-since<5 or now<self.next_attempt.get(role,0):continue
            if state['attempt']>=5:
                state.update(status='lost')
                self.topology.mount.event('error','Drive lost connection',f'{role}: check cable and drive power')
                continue
            state.update(status='reconnecting',attempt=state['attempt']+1)
            try:
                await self.before_reconnect()
                async with self.topology.operation_lock:
                    if role in self.disabled:continue
                    await self.topology._disconnect(role)
                    await self.topology._connect(role)
                state['error']=None
            except Exception as exc:state['error']=str(exc)
            self.next_attempt[role]=time.monotonic()+5 if live_clock else now+5

    async def run(self):
        while True:
            try:await self.tick()
            except Exception as exc:self.topology.mount.event('error','Drive watchdog error',str(exc))
            await asyncio.sleep(1)
