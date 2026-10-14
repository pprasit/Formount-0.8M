import asyncio
import struct
import pytest
from mount_service import ntp


def reply(token, t2, t3):
    packet=bytearray(48);packet[0]=0x24;packet[1]=2
    packet[8:12]=struct.pack('!I',int(.001*65536))
    packet[24:32]=token;packet[32:40]=ntp.timestamp(t2);packet[40:48]=ntp.timestamp(t3)
    return packet


def test_four_timestamp_offset_delay_and_error():
    t1=1791620000.;token=ntp.timestamp(t1)
    result=ntp.decode_reply(reply(token,t1+.03,t1+.032),token,t1,t1+.022,.022)
    assert result['offset_ms']==pytest.approx(20,abs=.001)
    assert result['rtt_ms']==pytest.approx(20,abs=.001)
    assert result['estimated_error_ms']==pytest.approx(11,abs=.02)


@pytest.mark.parametrize('change',[lambda p:p.__setitem__(0,0xe4),lambda p:p.__setitem__(1,0),lambda p:p.__setitem__(24,99),lambda p:p.__setitem__(0,0x23)])
def test_reject_unsynchronized_kod_wrong_origin_and_mode(change):
    t1=1791620000.;token=ntp.timestamp(t1);packet=reply(token,t1+.03,t1+.032);change(packet)
    with pytest.raises(ValueError):ntp.decode_reply(packet,token,t1,t1+.022,.022)


@pytest.mark.parametrize('value,host',[('ntp://192.168.1.10','192.168.1.10'),('https://time.cloudflare.com/','time.cloudflare.com'),('::1','::1'),('TIME.CLOUDFLARE.COM','time.cloudflare.com')])
def test_server_input(value,host):assert ntp.host_from_input(value)==host


@pytest.mark.parametrize('value',['https://example.com/api/time','x:80','ntp://user:pass@host','host?q=secret','bad host'])
def test_invalid_service_input(value):
    with pytest.raises(ValueError):ntp.host_from_input(value)


def test_find_ranks_uncertainty_not_local_offset(monkeypatch):
    async def probe(host):
        return {'host':host,'ok':True,'offset_ms':100 if host=='a' else 0,'estimated_error_ms':2 if host=='a' else 8,'rtt_ms':3}
    monkeypatch.setattr(ntp,'probe',probe);monkeypatch.setattr(ntp,'CANDIDATES',('a','b'))
    assert asyncio.run(ntp.discover())['best']['host']=='a'


def test_ntp_api_requires_verified_recent_server_and_persists_draft(tmp_path,monkeypatch):
    from fastapi.testclient import TestClient
    from mount_service.api import create_app
    from mount_service.service import MountService
    from datetime import datetime,timezone
    import json
    async def discover(host=None):
        item={'host':host or 'test-only.ntp','ok':True,'estimated_error_ms':1.,'rtt_ms':2.,'offset_ms':4.,'jitter_ms':.1,'samples':3,'stratum':2,'checked_utc':datetime.now(timezone.utc).isoformat()}
        return {'results':[item],'best':item}
    monkeypatch.setattr(ntp,'discover',discover)
    from mount_service import windows_clock
    applied=[]
    monkeypatch.setattr(windows_clock,'configure',lambda host:applied.append(host) or {'configured':True})
    monkeypatch.setattr(windows_clock,'inspect',lambda host:{'configured':True,'source':host,'source_matches':True})
    async def probe(host):return {'ok':True,'offset_ms':1,'estimated_error_ms':5}
    monkeypatch.setattr(ntp,'probe',probe)
    app=create_app(MountService())
    with TestClient(app) as client:
        assert client.get('/api/pointing/ntp').json()['ntp'] is None
        assert client.post('/api/pointing/ntp',json={'server':'test-only.ntp'}).status_code==400
        result=client.post('/api/pointing/ntp/find');assert result.status_code==200
        assert client.post('/api/pointing/ntp/find').status_code==429
        saved=client.post('/api/pointing/ntp',json={'server':'ntp://test-only.ntp'})
        assert saved.status_code==200
        assert applied==['test-only.ntp']
        assert saved.json()['ntp']['windows_time_configured']
        assert json.loads(app.state.pointing_workbench.path.read_text())['ntp']['host']=='test-only.ntp'
        assert client.post('/api/pointing/ntp',json={'server':'https://host/api/time'}).status_code==409
