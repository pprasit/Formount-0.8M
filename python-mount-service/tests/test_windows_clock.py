import asyncio
from types import SimpleNamespace
import pytest
from mount_service import windows_clock as clock


def test_requires_privilege_before_any_mutation(monkeypatch):
    monkeypatch.setattr(clock,'is_admin',lambda:False)
    monkeypatch.setattr(clock,'run',lambda *args:pytest.fail('OS command must not run'))
    with pytest.raises(ValueError,match='Administrator'):clock.configure('time.example.com')


def test_configuration_uses_argument_arrays_and_persists_native_service(monkeypatch):
    commands=[]
    monkeypatch.setattr(clock,'is_admin',lambda:True)
    monkeypatch.setattr(clock,'run',lambda *args:commands.append(args) or '')
    monkeypatch.setattr(clock,'inspect',lambda host:{'configured':True,'server':host})
    assert clock.configure('TIME.EXAMPLE.COM')['configured']
    assert commands==[('sc.exe','config','w32time','start=','auto'),('sc.exe','start','w32time'),
                      ('w32tm','/config','/manualpeerlist:time.example.com,0x8','/syncfromflags:manual','/update'),
                      ('w32tm','/resync','/rediscover')]


def test_rejects_injected_peer_without_os_commands(monkeypatch):
    monkeypatch.setattr(clock,'run',lambda *args:pytest.fail('invalid host must not run'))
    with pytest.raises(ValueError):clock.configure('time.example.com & bad')


@pytest.mark.parametrize('offset,state',[(1,'synchronized'),(1000,'sync_unconfirmed')])
def test_monitor_compares_saved_os_source_and_actual_offset(monkeypatch,offset,state):
    from mount_service import ntp
    monkeypatch.setattr(clock,'inspect',lambda host:{'configured':True,'source':host,'source_matches':True,'os_synchronized':True})
    async def probe(host):return {'ok':True,'offset_ms':offset,'estimated_error_ms':5}
    monkeypatch.setattr(ntp,'probe',probe)
    monitor=clock.ClockMonitor(SimpleNamespace(ntp={'host':'time.example.com'}),None)
    asyncio.run(monitor.check())
    assert monitor.status['state']==state


def test_monitor_does_not_claim_sync_with_wrong_os_peer(monkeypatch):
    monkeypatch.setattr(clock,'inspect',lambda host:{'configured':False,'state':'configuration_mismatch','source':'other','source_matches':False})
    monitor=clock.ClockMonitor(SimpleNamespace(ntp={'host':'time.example.com'}),None)
    asyncio.run(monitor.check())
    assert monitor.status['state']=='configuration_mismatch'
