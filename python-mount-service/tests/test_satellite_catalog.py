import pytest
from astropy.time import Time
from mount_service.satellite_catalog import SatelliteFrame, trajectory, catalog
from mount_service.moving_objects import load_astro_vendor, satellite_instance
from test_moving_objects import TLE

SITE = {'latitude':18.853823,'longitude':98.958353,'elevation':350}

def test_accelerated_positions_match_original_csharp_model():
    load_astro_vendor()
    from PWLib.Astro import Angle, LatLon
    frame=SatelliteFrame([TLE],SITE)
    dates=Time(['2024-01-01T00:00:00','2024-01-01T01:00:00'])
    errors,az,alt=frame.positions(dates)
    satellite=satellite_instance(**TLE)
    site=LatLon(Angle.FromDegs(SITE['latitude']),Angle.FromDegs(SITE['longitude']))
    for i,t in enumerate(dates):
        satellite.RefreshPosition(site,SITE['elevation'],float(t.jd))
        original=satellite.RaDecApparent.ToAltAz(site,float(t.jd))
        assert errors[0,i]==0
        assert az[0,i]==pytest.approx(float(original.Az.Degs)%360,abs=.002)
        assert alt[0,i]==pytest.approx(float(original.Alt.Degs),abs=.002)

@pytest.mark.parametrize('starts_below', [False, True])
def test_trajectory_ends_two_orbits_after_setting(monkeypatch, starts_below):
    import numpy as np
    from sgp4.api import Satrec
    start = 1704067200
    monkeypatch.setattr('mount_service.satellite_catalog.time.time', lambda: start)
    def positions(self, times):
        seconds = np.asarray(times.unix) - start
        altitude = 10 - seconds / 60
        if starts_below:
            altitude = np.where(seconds < 300, -10, altitude)
        return np.zeros((1, len(times))), np.full((1, len(times)), 359.0), altitude.reshape(1, -1)
    monkeypatch.setattr(SatelliteFrame, 'positions', positions)
    result = trajectory(TLE, SITE)
    period = 2 * np.pi / Satrec.twoline2rv(TLE['line1'], TLE['line2']).no_kozai * 60
    assert result['horizon_seconds'] == pytest.approx(600, abs=.001)
    assert result['points'][0]['seconds'] == 0
    assert result['points'][-1]['seconds'] == pytest.approx(600 + 2 * period)
    assert not result['closed']
    assert any(p['altitude_deg'] == 0 for p in result['points'])


def test_trajectory_without_setting_has_bounded_search(monkeypatch):
    import numpy as np
    def positions(self, times):
        return np.zeros((1, len(times))), np.zeros((1, len(times))), np.full((1, len(times)), 20)
    monkeypatch.setattr(SatelliteFrame, 'positions', positions)
    result = trajectory(TLE, SITE)
    assert result['horizon_seconds'] is None
    assert result['points'][-1]['seconds'] <= 86400

def test_catalog_rejects_unknown_category_without_network():
    with pytest.raises(ValueError,match='Unknown satellite category'):
        catalog('unknown')
