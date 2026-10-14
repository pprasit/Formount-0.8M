"""Real ICRS/J2000 <-> horizontal coordinates. No generated catalog objects."""
import json
import math
from pathlib import Path

import astropy.units as u
from astropy.coordinates import AltAz, EarthLocation, SkyCoord, CIRS
from astropy.time import Time
from astropy.utils import iers

iers.conf.auto_download = False
ROOT = Path(__file__).resolve().parents[2]


class Astronomy:
    def __init__(self, latitude=18.853823, longitude=98.958353, elevation=350):
        self.set_site(latitude, longitude, elevation)
        self.catalog = json.loads((ROOT / "Narit-Mount-Lib/Data/BrightStars.json").read_text(encoding="utf-8-sig"))["stars"]

    def set_site(self, latitude, longitude, elevation):
        if not -90 <= latitude <= 90 or not -180 <= longitude <= 180 or not -500 <= elevation <= 10000:
            raise ValueError("Invalid observer location")
        self.site = {"latitude": latitude, "longitude": longitude, "elevation": elevation}
        self.location = EarthLocation.from_geodetic(longitude*u.deg, latitude*u.deg, elevation*u.m)

    def time(self, utc=None):
        return Time(utc, scale="utc") if utc else Time.now()

    def frame(self, t):
        # Geometric Alt/Az (pressure=0): no implicit weather/refraction assumptions.
        return AltAz(obstime=t, location=self.location, pressure=0*u.hPa)

    def target(self, ra_hours=None, dec_deg=None, body=None, utc=None, orbit=None, tle=None):
        if body:
            from .moving_objects import solar_position
            return solar_position(self, body, utc)
        if tle:
            from .moving_objects import satellite_position
            return satellite_position(self, tle, utc)
        if orbit:
            from .moving_objects import small_body
            ra_hours, dec_deg = small_body(orbit, float(self.time(utc).jd))
            result = self.target(ra_hours, dec_deg, utc=utc)
            result.update(frame='C# approximate Kepler orbit · geocentric RA/Dec', source='SmallBodyEphemerisCalculator')
            return result
        t = self.time(utc)
        if ra_hours is None or dec_deg is None or not 0 <= ra_hours < 24 or not -90 <= dec_deg <= 90:
            raise ValueError("RA must be 0–24 hours; Dec must be −90..90 degrees")
        sky = SkyCoord(ra=ra_hours*u.hourangle, dec=dec_deg*u.deg, frame="icrs")
        horizontal = sky.transform_to(self.frame(t))
        apparent = sky.transform_to(CIRS(obstime=t, location=self.location))
        lst = t.sidereal_time("apparent", longitude=self.location.lon)
        # CIRS uses CIO RA. Convert to equinox-based apparent RA via equation of origins.
        import erfa
        eo = erfa.eo06a(t.tt.jd1, t.tt.jd2)
        apparent_ra = (apparent.ra.rad - eo) % (2*math.pi)
        hour_angle = lst.rad - apparent_ra
        dec, lat = apparent.dec.rad, self.location.lat.rad
        q = math.atan2(math.sin(hour_angle), math.tan(lat)*math.cos(dec)-math.sin(dec)*math.cos(hour_angle))
        return {"utc": t.utc.isot+"Z", "jd": float(t.jd), "lst_hours": float(lst.hour),
                "lst_rad": float(lst.rad), "azimuth_deg": float(horizontal.az.deg),
                "altitude_deg": float(horizontal.alt.deg), "parallactic_deg": math.degrees(q),
                "apparent_ra_rad": float(apparent_ra), "apparent_dec_rad": float(apparent.dec.rad),
                "ra_hours": float(apparent_ra)*12/math.pi, "dec_deg": float(apparent.dec.deg),
                "above_horizon": bool(horizontal.alt.deg > 0), "site": self.site,
                "frame": "ICRS/J2000 input; geometric AltAz"}

    def from_encoder(self, azimuth, altitude, utc=None):
        t = self.time(utc)
        sky = SkyCoord(az=(azimuth%360)*u.deg, alt=altitude*u.deg, frame=self.frame(t)).icrs
        return {"ra_hours": float(sky.ra.hour), "dec_deg": float(sky.dec.deg), "utc": t.utc.isot+"Z"}

    def stars(self, magnitude=4.5, minimum_altitude=20, maximum_altitude=85, limit=60):
        stars = [s for s in self.catalog if s["magnitude"] <= magnitude]
        sky = SkyCoord(ra=[s["ra"] for s in stars]*u.hourangle,
                       dec=[s["dec"] for s in stars]*u.deg, frame="icrs")
        horizontal = sky.transform_to(self.frame(self.time()))
        result = []
        for s, az, alt in zip(stars, horizontal.az.deg, horizontal.alt.deg):
            if minimum_altitude <= alt <= maximum_altitude:
                result.append({"id": s["id"], "name": s["name"], "ra_hours": s["ra"],
                               "dec_deg": s["dec"], "magnitude": s["magnitude"],
                               "azimuth_deg": float(az), "altitude_deg": float(alt)})
        return sorted(result, key=lambda s: s["magnitude"])[:limit]

    def display_objects(self):
        """Raw J2000 catalog for client-side display propagation."""
        import csv
        rows = [dict(s, category="bright_star", ra_hours=s["ra"], dec_deg=s["dec"]) for s in self.catalog if s["magnitude"] <= 4.5]
        if not hasattr(self, "deep_catalog"):
            self.deep_catalog = []
            types = {"G": "galaxy", "OCl": "open_cluster", "GCl": "globular_cluster", "Nb": "nebula", "PN": "nebula", "HII": "nebula"}
            with (ROOT / "Narit-Mount-Lib/Data/OpenNgcCatalog.csv").open(encoding="utf-8-sig", newline="") as f:
                for obj in csv.DictReader(f, delimiter=";"):
                    if obj["Type"] not in types or not obj["RA"] or not obj["Dec"]:
                        continue
                    mag = obj["V-Mag"] or obj["B-Mag"]
                    if not obj["M"] and (not mag or float(mag) > 10):
                        continue
                    h, m, sec = map(float, obj["RA"].split(":"))
                    d, dm, ds = map(float, obj["Dec"].lstrip("+-").split(":"))
                    dec = (d + dm/60 + ds/3600) * (-1 if obj["Dec"].startswith("-") else 1)
                    name = ("M" + obj["M"] + " · " if obj["M"] else "") + obj["Name"]
                    self.deep_catalog.append(dict(id=obj["Name"], name=name, category=types[obj["Type"]], ra_hours=h+m/60+sec/3600, dec_deg=dec, magnitude=float(mag) if mag else None))
        rows += self.deep_catalog
        return rows + [dict(id=body,name=body.title(),category='planet',body=body,magnitude=None) for body in ('moon','mercury','venus','mars','jupiter','saturn','uranus','neptune')]

    def sky_catalog(self):
        """Legacy server-computed snapshot; client maps use display_objects."""
        t=self.time()
        rows=[row for row in self.display_objects() if not row.get('body')]
        sky = SkyCoord(ra=[s["ra_hours"] for s in rows]*u.hourangle, dec=[s["dec_deg"] for s in rows]*u.deg, frame="icrs")
        horizontal = sky.transform_to(self.frame(t))
        visible = []
        for obj, az, alt in zip(rows, horizontal.az.deg, horizontal.alt.deg):
            visible.append(dict(obj, azimuth_deg=float(az), altitude_deg=float(alt)))
        from astropy.coordinates import get_body
        for body in ["moon", "mercury", "venus", "mars", "jupiter", "saturn", "uranus", "neptune"]:
            position = get_body(body, t, self.location).transform_to(self.frame(t))
            visible.append(dict(id=body, name=body.title(), category="planet", body=body, azimuth_deg=float(position.az.deg), altitude_deg=float(position.alt.deg), magnitude=None))
        sun = get_body("sun", t, self.location).transform_to(self.frame(t))
        return dict(utc=t.utc.isot+"Z", site=self.site, sun_altitude_deg=float(sun.alt.deg), objects=visible, source="BSC5 · OpenNGC · Astropy ephemeris")
