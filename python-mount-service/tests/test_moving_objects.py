import math
import pytest
from fastapi.testclient import TestClient
from mount_service.api import create_app, Target
from mount_service.service import MountService
from mount_service.astronomy import Astronomy
from mount_service.moving_objects import kepler, small_body


def tle_line(text):
    text = text[:68].ljust(68)
    return text + str(sum(int(c) if c.isdigit() else c == '-' for c in text) % 10)


TLE = dict(name='ISS test epoch',
           line1=tle_line('1 25544U 98067A   24001.00000000  .00016717  00000-0  30138-3 0  999'),
           line2=tle_line('2 25544  51.6416  21.0019 0006703  65.0965  65.0965 15.5000000043000'))
ORBIT = dict(name='Test orbit', eccentricity=.1, inclination_deg=10,
             ascending_node_deg=80, arg_perihelion_deg=73,
             semi_major_axis_au=2.7, epoch_jd=2460000, mean_anomaly_deg=40)


def test_kepler_residual_and_comet_asteroid_equivalence():
    for e in (0, .1, .8, .99):
        anomaly = kepler(1.3, e)
        assert abs(anomaly-e*math.sin(anomaly)-1.3) < 1e-11
    orbit = {**ORBIT, 'mean_anomaly_deg': 0}
    comet = {k:v for k,v in orbit.items() if k not in ('semi_major_axis_au','epoch_jd','mean_anomaly_deg')}
    comet.update(perihelion_distance_au=2.7*.9, perihelion_epoch_jd=2460000)
    assert small_body(orbit,2460100) == pytest.approx(small_body(comet,2460100),abs=1e-10)


def test_invalid_inputs():
    for body in ({'orbit':{**ORBIT,'eccentricity':1}}, {'orbit':{'eccentricity':.1,'inclination_deg':0,'ascending_node_deg':0,'arg_perihelion_deg':0}}, {'body':'moon','orbit':ORBIT}, {'tle':{**TLE,'line2':TLE['line2'][:-1]+'x'}}):
        with pytest.raises(ValueError):
            Target.model_validate(body)


def test_vendor_satellite_matches_csharp_topocentric_conversion():
    astro = Astronomy()
    result = astro.target(tle=TLE, utc='2024-01-01T00:00:00')
    assert result['frame'].startswith('PWLib SGP4')
    assert 0 <= result['azimuth_deg'] < 360
    assert -90 <= result['altitude_deg'] <= 90
    from mount_service.moving_objects import satellite_instance
    from PWLib.Astro import Angle, LatLon
    sat = satellite_instance(TLE['name'],TLE['line1'],TLE['line2'])
    site = LatLon(Angle.FromDegs(astro.site['latitude']),Angle.FromDegs(astro.site['longitude']))
    jd = float(astro.time('2024-01-01T00:00:00').jd)
    sat.RefreshPosition(site,astro.site['elevation'],jd)
    horizontal = sat.RaDecApparent.ToAltAz(site,jd)
    assert result['apparent_ra_rad'] == pytest.approx(float(sat.RaDecApparent.Ra.Rads))
    assert result['apparent_dec_rad'] == pytest.approx(float(sat.RaDecApparent.Dec.Rads))
    assert result['azimuth_deg'] == pytest.approx(float(horizontal.Az.Degs)%360)
    assert result['altitude_deg'] == pytest.approx(float(horizontal.Alt.Degs))


def test_preview_and_disabled_axes_guard(monkeypatch):
    # Isolate the disabled-axis guard from date-dependent horizon/travel rejection.
    from mount_service.observatory import Observatory
    original_preview = Observatory.preview
    async def observable_preview(self, payload):
        result = await original_preview(self, payload)
        return {**result, 'slew_allowed': True}
    monkeypatch.setattr(Observatory, 'preview', observable_preview)
    with TestClient(create_app(MountService(simulation=True))) as client:
        assert client.post('/api/axes/azimuth/command',json={'action':'disable'}).status_code == 200
        import time
        time.sleep(.1)
        for target in ({'body':'moon'}, {'orbit':ORBIT}, {'tle':TLE}):
            response = client.post('/api/astronomy/moving/preview',json=target)
            assert response.status_code == 200, response.text
            data = response.json()
            assert math.isfinite(data['azimuth_rate_deg_s'])
            assert 0 <= data['ra_hours'] < 24
            assert -90 <= data['dec_deg'] <= 90
            assert 'encoder_azimuth_deg' in data
            response = client.post('/api/astronomy/tracking/start',json=target)
            assert response.status_code == 409
            assert 'Enable both' in response.text
        assert client.post('/api/astronomy/moving/preview',json={'orbit':{**ORBIT,'eccentricity':1}}).status_code == 422


def test_live_stream_calculates_selected_object_and_waits_for_ack():
    with TestClient(create_app(MountService(simulation=True))) as client:
        with client.websocket_connect('/ws/astronomy/moving') as ws:
            ws.send_json({'body':'mercury'})
            first = ws.receive_json()
            assert first['target']['body'] == 'mercury'
            assert first['target_interval_ms'] == 5
            assert first['source'] == 'C# PlanetTrackingControl / PWLib'
            assert first['ra_hours'] == pytest.approx(first['apparent_ra_rad']*12/math.pi)
            assert first['dec_deg'] == pytest.approx(math.degrees(first['apparent_dec_rad']))
            import time
            time.sleep(.03)
            ws.send_text('ack')
            second = ws.receive_json()
            assert second['utc'] > first['utc']
            assert second['sample_interval_ms'] >= 25
            assert math.isfinite(second['altitude_rate_deg_s'])


def test_stream_rejects_invalid_target_and_foreign_origin():
    with TestClient(create_app(MountService(simulation=True))) as client:
        with client.websocket_connect('/ws/astronomy/moving') as ws:
            ws.send_json({'body':'invalid'})
            assert 'error' in ws.receive_json()
        from starlette.websockets import WebSocketDisconnect
        with pytest.raises(WebSocketDisconnect):
            with client.websocket_connect('/ws/astronomy/moving',headers={'origin':'https://other.example'}):
                pass
