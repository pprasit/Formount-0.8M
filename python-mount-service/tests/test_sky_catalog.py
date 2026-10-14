from astropy.time import Time
from mount_service.astronomy import Astronomy


def test_sky_catalog_uses_current_site_and_full_sphere():
    astro = Astronomy()
    astro.time = lambda utc=None: Time("2026-10-09T05:44:00", scale="utc")
    result = astro.sky_catalog()
    assert result["utc"].startswith("2026-10-09T05:44:00")
    assert result["site"] == astro.site
    assert {o["category"] for o in result["objects"]} == {"bright_star", "planet", "galaxy", "open_cluster", "globular_cluster", "nebula"}
    assert any(o["altitude_deg"] < 0 for o in result["objects"])
    assert any(o["altitude_deg"] > 0 for o in result["objects"])
    assert all(-90 <= o["altitude_deg"] <= 90 and 0 <= o["azimuth_deg"] < 360 for o in result["objects"])
    star = next(o for o in result["objects"] if o["category"] == "bright_star")
    expected = astro.target(star["ra_hours"], star["dec_deg"])
    assert abs(expected["altitude_deg"] - star["altitude_deg"]) < 1e-8
    assert abs(expected["azimuth_deg"] - star["azimuth_deg"]) < 1e-8
