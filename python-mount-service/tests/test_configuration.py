import json
from mount_service.configuration import ConfigurationStore
from mount_service.api import create_app
from mount_service.service import MountService
from fastapi.testclient import TestClient


def test_database_reopen_and_one_time_legacy_migration(tmp_path):
    store=ConfigurationStore(tmp_path)
    store.migrate({'location':{'latitude':18.9},'ntp':{'host':'old.ntp'}})
    store.set('ntp',{'host':'new.ntp'})
    telescope=store.save_telescope({'name':'First','focal_length_mm':1000})
    reopened=ConfigurationStore(tmp_path)
    reopened.migrate({'ntp':{'host':'old.ntp'}})
    assert reopened.get('ntp')['host']=='new.ntp'
    assert reopened.get('location')['latitude']==18.9
    assert reopened.telescopes()[0]['id']==telescope['id']
    reopened.save_telescope({'name':'Renamed','focal_length_mm':1200},telescope['id'])
    assert len(reopened.telescopes())==1
    assert reopened.telescopes()[0]['name']=='Renamed'


def test_multi_telescope_api_validation_and_restart_retention():
    app=create_app(MountService())
    with TestClient(app) as client:
        assert client.get('/api/pointing/telescopes').json()['telescopes']==[]
        assert client.post('/api/pointing/telescopes',json={'name':'Bad','focal_length_mm':0}).status_code==422
        assert client.post('/api/pointing/telescopes',json={'name':'Bad','focal_length_mm':1000,'aperture_mm':200,'obstruction_mm':200}).status_code==422
        first=client.post('/api/pointing/telescopes',json={'name':'Main','focal_length_mm':1000,'aperture_mm':200}).json()['telescope']
        second=client.post('/api/pointing/telescopes',json={'name':'Guide','focal_length_mm':400}).json()['telescope']
        assert first['id']!=second['id']
        assert client.put('/api/pointing/telescopes/'+first['id'],json={'name':'Main','focal_length_mm':1500}).status_code==200
        gps={'latitude':18.9,'longitude':99.1,'elevation':350}
        assert client.post('/api/pointing/location',json=gps).status_code==200
        app.state.pointing_workbench.ntp={'host':'test-only.ntp'};app.state.pointing_workbench.save()
    restarted=create_app(MountService())
    with TestClient(restarted) as client:
        scopes=client.get('/api/pointing/telescopes').json()['telescopes']
        assert len(scopes)==2 and scopes[0]['focal_length_mm']==1500
        assert client.get('/api/pointing/location').json()['location']==gps
        assert client.get('/api/pointing/ntp').json()['ntp']['host']=='test-only.ntp'
