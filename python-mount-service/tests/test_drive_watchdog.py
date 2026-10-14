import asyncio
from types import SimpleNamespace
from mount_service.drive_watchdog import DriveWatchdog

class Topology:
    def __init__(self):
        self.connected=True;self.valid=False;self.calls=[];self.fail=False
        self.port=None;self.readings={};self.error=None
        self.operation_lock=asyncio.Lock()
        self.mount=SimpleNamespace(snapshot=lambda:{'drive':{'mode':'hardware','connected':self.connected},'axes':[{'valid':self.valid}]},event=lambda *a:None)
    async def _disconnect(self,role):self.calls.append(('disconnect',role));self.connected=False
    async def _connect(self,role):
        self.calls.append(('connect',role))
        if self.fail:raise ValueError('No reply')
        self.connected=True
async def prepare():pass

def test_retry_exhaustion_and_spacing():
    async def run():
        t=Topology();t.fail=True;w=DriveWatchdog(t,prepare)
        await w.tick(0);await w.tick(4);assert not t.calls
        for n in range(5):
            await w.tick(5+n*5)
            assert w.states['mount']['attempt']==n+1
            await w.tick(6+n*5)
            assert len(t.calls)==(n+1)*2
        await w.tick(30)
        assert w.states['mount']['status']=='lost'
        await w.tick(100);assert len(t.calls)==10
    asyncio.run(run())

def test_recovery_and_manual_disconnect():
    async def run():
        t=Topology();w=DriveWatchdog(t,prepare)
        await w.tick(0);await w.tick(5);assert len(t.calls)==2
        t.valid=True;await w.tick(6);assert w.states['mount']['status']=='healthy'
        w.manual_disconnect('mount');t.connected=False;t.valid=False
        await w.tick(100);assert len(t.calls)==2
        w.manual_connect('mount');t.connected=True
        await w.tick(101);await w.tick(106);assert len(t.calls)==4
    asyncio.run(run())
