from datetime import datetime, timezone
import threading
import pytest
from fastapi.testclient import TestClient

from mount_service.astronomy import Astronomy
from mount_service.visibility import forecast
from mount_service.api import create_app
from mount_service.service import MountService


def test_three_day_window_and_horizon_crossings():
    astro = Astronomy()
    center = datetime(2026,10,9,5,tzinfo=timezone.utc)
    result = forecast({'body':'sun'},astro.site,center)
    assert result['end_ms']-result['start_ms'] == 3*86400000
    assert result['center_ms']-result['start_ms'] == 1.5*86400000
    assert len(result['samples']) == 433
    assert len(result['events']) == 6
    assert min(p[1] for p in result['samples']) < -50
    assert max(p[1] for p in result['samples']) > 50
    for event in result['events']:
        at=datetime.fromtimestamp(event['timestamp_ms']/1000,tz=timezone.utc)
        altitude=astro.target(body='sun',utc=at)['altitude_deg']
        assert abs(altitude)<.01
        assert (altitude>0) == (event['kind']=='rise')


def test_cancelled_forecast_stops_before_sampling():
    stop=threading.Event();stop.set()
    with pytest.raises(InterruptedError):
        forecast({'body':'sun'},Astronomy().site,cancelled=stop)


def test_visibility_api_provides_samples_and_rejects_bad_elements():
    with TestClient(create_app(MountService(simulation=True))) as client:
        response=client.post('/api/astronomy/moving/visibility',json={'body':'mercury'})
        assert response.status_code == 200,response.text
        result=response.json()
        assert len(result['samples']) == 433
        assert result['step_seconds'] == 600
        assert result['horizon_deg'] == 0
        assert client.post('/api/astronomy/moving/visibility',json={'body':'invalid'}).status_code == 422
