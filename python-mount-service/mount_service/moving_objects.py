"""Moving targets ported from SmallBodyEphemerisCalculator and SatelliteTracker."""
import math
from functools import lru_cache

K = .01720209895


@lru_cache(maxsize=1)
def load_astro_vendor():
    from .pointing import load_vendor
    from .astronomy import ROOT
    import clr
    dependency = ROOT / 'python-mount-service/runtime/vendor/NLog.dll'
    if not dependency.exists():
        raise ValueError('Install PWLib dependency using setup-satellite-runtime.ps1')
    clr.AddReference(str(dependency))
    load_vendor()


def planets_instance(latitude, longitude, elevation):
    load_astro_vendor()
    from PWLib.Astro import Planets
    return Planets(math.radians(latitude), math.radians(longitude), elevation, 'EMBEDDED_RESOURCE')


def solar_position(astro, body, utc):
    """Same Planets and RA/Dec -> LMST -> AltAz pipeline as PlanetTrackingControl."""
    indices = {'sun':0,'moon':10,'mercury':1,'venus':2,'mars':4,'jupiter':5,'saturn':6,'uranus':7,'neptune':8}
    if body not in indices:
        raise ValueError('Unknown solar-system object')
    key = tuple(astro.site.values())
    if getattr(astro, '_planet_site', None) != key:
        astro._planet_calculator = planets_instance(*key)
        astro._planet_site = key
    calculator = astro._planet_calculator
    from PWLib.Astro import RaDec, Angle, LatLon, AstroTime
    t = astro.time(utc)
    jd = float(t.jd)
    calculator.CalculateAllPositions(jd)
    planet = calculator.All.Plan[indices[body]]
    ra, dec = float(planet.RA), float(planet.Dec)
    lmst = AstroTime.Lmst(jd, Angle.FromDegs(astro.site['longitude']))
    site = LatLon(Angle.FromDegs(astro.site['latitude']), Angle.FromDegs(astro.site['longitude']))
    horizontal = RaDec.FromRads(ra, dec).ToHaDec(lmst).ToAltAz(site)
    az, alt = float(horizontal.Az.Degs)%360, float(horizontal.Alt.Degs)
    lst = float(lmst.Rads)
    hour_angle = lst-ra
    q = math.atan2(math.sin(hour_angle), math.tan(astro.location.lat.rad)*math.cos(dec)-math.sin(dec)*math.cos(hour_angle))
    return {'utc':t.utc.isot+'Z','jd':jd,'lst_hours':lst*12/math.pi,'lst_rad':lst,
            'azimuth_deg':az,'altitude_deg':alt,'parallactic_deg':math.degrees(q),
            'apparent_ra_rad':ra,'apparent_dec_rad':dec,'ra_hours':ra*12/math.pi,
            'dec_deg':math.degrees(dec),'above_horizon':alt>0,'site':astro.site,
            'frame':'PWLib Planets · apparent RA/Dec · geometric AltAz',
            'source':'C# PlanetTrackingControl / PWLib'}


def kepler(mean, eccentricity):
    mean %= 2 * math.pi
    anomaly = mean if eccentricity < .8 else math.pi
    for _ in range(30):
        delta = (anomaly - eccentricity * math.sin(anomaly) - mean) / (1 - eccentricity * math.cos(anomaly))
        anomaly -= delta
        if abs(delta) < 1e-12:
            return anomaly
    raise ValueError('Kepler equation did not converge')


def orbit_vector(a, e, mean, inclination, node, perihelion):
    anomaly = kepler(mean, e)
    radius = a * (1 - e * math.cos(anomaly))
    true = 2 * math.atan2(math.sqrt(1 + e) * math.sin(anomaly / 2), math.sqrt(1 - e) * math.cos(anomaly / 2))
    inc, nod, arg = map(math.radians, (inclination, node, perihelion))
    arg += true
    return (radius * (math.cos(nod)*math.cos(arg)-math.sin(nod)*math.sin(arg)*math.cos(inc)),
            radius * (math.sin(nod)*math.cos(arg)+math.cos(nod)*math.sin(arg)*math.cos(inc)),
            radius * math.sin(arg)*math.sin(inc))


def small_body(elements, jd):
    # Same approximate geocentric model as C#: no light-time or perturbations.
    e = elements['eccentricity']
    a = elements.get('semi_major_axis_au')
    if a is None:
        a = elements['perihelion_distance_au'] / (1-e)
        mean = K / a**1.5 * (jd-elements['perihelion_epoch_jd'])
    else:
        mean = math.radians(elements['mean_anomaly_deg']) + K / a**1.5 * (jd-elements['epoch_jd'])
    body = orbit_vector(a, e, mean, elements['inclination_deg'], elements['ascending_node_deg'], elements['arg_perihelion_deg'])
    t = (jd-2451545)/36525
    earth_e = .01670862-.000042037*t-.0000001236*t*t
    peri = 102.937348+1.7195366*t+.00045688*t*t
    earth = orbit_vector(1.000001018, earth_e, math.radians(100.466457+35999.3728565*t-peri), 0, 0, peri)
    x, y, z = (b-v for b, v in zip(body, earth))
    ob = math.radians((84381.406-46.836769*t-.0001831*t*t+.00200340*t**3)/3600)
    y, z = y*math.cos(ob)-z*math.sin(ob), y*math.sin(ob)+z*math.cos(ob)
    return (math.atan2(y,x) % (2*math.pi))*12/math.pi, math.degrees(math.atan2(z, math.hypot(x,y)))


@lru_cache(maxsize=128)
def satellite_instance(name, line1, line2):
    load_astro_vendor()
    try:
        from PWLib.Astro import Satellite
        return Satellite(name, line1, line2)
    except Exception as exc:
        raise ValueError(f'PWLib satellite runtime unavailable or TLE invalid: {exc}') from exc


def satellite_position(astro, tle, utc):
    # Called only on Observatory's single actor: PWLib Satellite is stateful.
    sat = satellite_instance(tle['name'], tle['line1'], tle['line2'])
    from PWLib.Astro import Angle, LatLon
    t = astro.time(utc)
    site = LatLon(Angle.FromDegs(astro.site['latitude']), Angle.FromDegs(astro.site['longitude']))
    sat.RefreshPosition(site, astro.site['elevation'], float(t.jd))
    coordinate = sat.RaDecApparent
    horizontal = coordinate.ToAltAz(site, float(t.jd))
    az, alt = float(horizontal.Az.Degs)%360, float(horizontal.Alt.Degs)
    if not math.isfinite(az) or not math.isfinite(alt) or not -90 <= alt <= 90:
        raise ValueError('Satellite propagation failed')
    # Common target result carries parallactic geometry; replace horizontal with
    # the vendor's actual topocentric result, not an ICRS reinterpretation.
    result = astro.target(float(coordinate.Ra.Hours), float(coordinate.Dec.Degs), utc=utc)
    ra, dec = float(coordinate.Ra.Rads), float(coordinate.Dec.Rads)
    hour_angle = result['lst_rad']-ra
    latitude = astro.location.lat.rad
    parallactic = math.atan2(math.sin(hour_angle), math.tan(latitude)*math.cos(dec)-math.sin(dec)*math.cos(hour_angle))
    result.update(azimuth_deg=az, altitude_deg=alt, above_horizon=alt>0,
                  apparent_ra_rad=ra, apparent_dec_rad=dec, parallactic_deg=math.degrees(parallactic),
                  ra_hours=ra*12/math.pi, dec_deg=math.degrees(dec),
                  frame='PWLib SGP4 · topocentric apparent', source='C# SatelliteTracker / PWLib')
    return result
