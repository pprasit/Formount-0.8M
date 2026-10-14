import asyncio
import pytest
from fastapi.testclient import TestClient
from mount_service.audit import AuditStore
from mount_service.api import create_app
from mount_service.service import MountService

@pytest.fixture
def app(tmp_path,monkeypatch):
    monkeypatch.setenv('NARIT_AUDIT_DB',str(tmp_path/'audit.sqlite3'))
    return create_app(MountService(simulation=True))


def test_click_batch_persists_and_deduplicates(app):
    batch={'session_id':'session-a','events':[{'event_id':'click-1','kind':'ui.click','utc':'2026-10-10T02:00:00Z','action_id':'action-a','page':'non-celestial','details':{'id':'moving-track','password':'hidden'}}]}
    with TestClient(app) as client:
        assert client.post('/api/audit/events',json=batch).status_code==200
        assert client.post('/api/audit/events',json=batch).status_code==200
        events=client.get('/api/audit/events?session_id=session-a').json()['events']
        assert len(events)==1
        assert events[0]['details']['password']=='[redacted]'
        assert events[0]['client_utc']==batch['events'][0]['utc']
        assert events[0]['utc'].endswith('+00:00')
    reopened=AuditStore(app.state.audit.path)
    assert len(reopened.query(session_id='session-a'))==1


def test_rejected_command_keeps_parameters_result_and_correlation(app):
    with TestClient(app) as client:
        response=client.post('/api/axes/azimuth/command',json={'action':'not-a-command'},headers={'X-Audit-Session':'s','X-Audit-Action':'a'})
        assert response.status_code>=400
        app.state.audit.writer.submit(lambda:None).result()
        events=client.get('/api/audit/events?action_id=a').json()['events']
        assert {e['kind'] for e in events}=={'request.started','request.failed'}
        done=events[0]
        assert done['details']['request']['action']=='not-a-command'
        assert done['details']['status']==response.status_code
        assert done['request_id']==response.headers['x-request-id']


def test_invalid_batch_and_pagination(app):
    with TestClient(app) as client:
        assert client.post('/api/audit/events',json={'events':[{}]}).status_code==422
        app.state.audit.record('test.a');app.state.audit.record('test.b')
        first=client.get('/api/audit/events?limit=1').json()
        second=client.get('/api/audit/events',params={'before':first['next_before'],'limit':1}).json()
        assert second['events'][0]['id']<first['events'][0]['id']


def test_background_job_records_actual_failure(app):
    async def fail():raise ValueError('camera test failure')
    async def run():
        job=app.state.observatory.new_job('test camera',fail)
        await app.state.observatory.jobs[job['id']]['_task']
    asyncio.run(run())
    events=app.state.audit.query()
    failed=next(e for e in events if e['kind']=='job.failed')
    assert failed['details']['error']=='camera test failure'
