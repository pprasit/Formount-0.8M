"""Single PointXP runtime; separate legacy .pxp loading and CoreDirect fitting."""
import math
import re
import uuid
import sys
import threading
from pathlib import Path

from .astronomy import ROOT


def pointing_error(expected_ra_hours,expected_dec_deg,solved_ra_hours,solved_dec_deg):
    """C# CalculatePointingError: compare coordinates in the same J2000 frame."""
    ra=((solved_ra_hours-expected_ra_hours)*15+180)%360-180
    ra*=math.cos(math.radians((expected_dec_deg+solved_dec_deg)/2))
    dec=solved_dec_deg-expected_dec_deg
    return {'ra_error_arcsec':ra*3600,'dec_error_arcsec':dec*3600,
            'total_error_arcsec':math.hypot(ra,dec)*3600}


def load_vendor():
    # Copy trusted repo assemblies into our runtime; never modify the original DLLs.
    target = ROOT / "python-mount-service/runtime/vendor"
    target.mkdir(parents=True, exist_ok=True)
    for name in ("PWLib.dll", "PXPLib.dll", "SRSLib.dll", "NLog.dll"):
        source = ROOT / "Narit-Mount-Lib/dll" / name
        if not source.exists():
            source = ROOT / "Narit-Mount-Lib/bin/Debug/net10.0-windows" / name
        if source.exists() and not (target/name).exists():
            (target/name).write_bytes(source.read_bytes())
    sys.path.append(str(target))
    import clr
    for name in ("SRSLib.dll", "PWLib.dll", "PXPLib.dll"):
        clr.AddReference(str(target/name))
    return target


class PointingModel:
    def __init__(self, astronomy):
        self.astro = astronomy
        self.lock = threading.RLock()
        self.model = None
        self.active = False
        self.path = None
        self.backend = "none"
        self.error = None

    def initialize(self):
        with self.lock:
            if self.model is not None:
                return
            try:
                load_vendor()
                from PXPLib import PointXP
                self.model = PointXP()
                self.model.MountType = 0
                self.model.Latitude = self.astro.site["latitude"]
                self.model.Longitude = self.astro.site["longitude"]
                self.model.ThetaCountsPerRev = self.model.PhiCountsPerRev = 2*math.pi
                self.model.EnableTermsForAltAz()
                self.model.EnableRefraction = False
                self.error = None
            except Exception as exc:
                self.error = str(exc)
                raise ValueError(f"PXPLib initialization failed: {exc}") from exc

    def status(self):
        with self.lock:
            metrics = {}
            if self.active:
                core = self.model.Core
                metrics = {"points": int(core.NumCalPoints), "used": int(core.NumPointsUsed),
                           "terms": int(core.NumErrorTermsInUse), "rms_arcsec": float(core.RMSErrorRad)*206264.806247,
                           "max_error_arcsec": float(core.MaxErrorRad)*206264.806247,
                           "rms_all_arcsec":float(core.RMSErrorAllRad)*206264.806247,
                           "max_all_arcsec":float(core.MaxErrorAllRad)*206264.806247}
            return {"available": self.model is not None, "active": self.active, "path": self.path,
                    "backend": self.backend, "error": self.error, **metrics}

    def load(self, filename):
        with self.lock:
            self.initialize()
            path = Path(filename).expanduser().resolve()
            if path.suffix.lower() != ".pxp" or not path.is_file():
                raise ValueError("Select an existing .pxp model")
            content = path.read_text(encoding="utf-8-sig")
            if "Model Terms" not in content or "Cal Star 9:" not in content:
                raise ValueError("Runtime model needs at least 9 solved Cal Stars and fitted Model Terms")
            previous = self.path
            try:
                self.model.LoadFile(str(path))
                self._validate()
                self.active, self.path, self.backend = True, str(path), "legacy-pxp"
                self.error = None
            except Exception as exc:
                if previous:
                    self.model.LoadFile(previous)
                else:
                    self.active = False
                raise ValueError(f"Model load failed: {exc}") from exc
            return self.status()

    def _validate(self):
        core = self.model.Core
        if core.NumPointsUsed < 9 or not math.isfinite(float(core.RMSErrorRad)):
            raise ValueError("PXPLib did not produce a valid fitted model")

    def fit(self, points, backend="experimental-core-direct", activate=True):
        with self.lock:
            self.initialize()
            included = [p for p in points if p.get("included", True)]
            if len(included) < 9:
                raise ValueError("Need at least 9 solved calibration points")
            positions = {(round(p['encoder_azimuth_deg'],2),round(p['encoder_altitude_deg'],2)) for p in included}
            if len(positions) < 9:
                raise ValueError("Need at least 9 distinct measured sky positions")
            from PXPLib import CalPoint
            if backend not in ('legacy-pxp','experimental-core-direct'):raise ValueError('Unknown pointing backend')
            previous = self.path
            previous_meta=(self.active,self.path,self.backend)
            try:
                self.model.Core.ClearCalPoints()
                for p in points:
                    c = CalPoint()
                    c.Name = p["name"]
                    c.RA = math.radians(p["solved_ra_hours"]*15)
                    c.Dec = math.radians(p["solved_dec_deg"])
                    c.ThetaEnc = math.radians(p["encoder_altitude_deg"])
                    c.PhiEnc = math.radians(p["encoder_azimuth_deg"])
                    t = self.astro.time(p["capture_utc"])
                    c.LSTMark = float(t.sidereal_time("apparent", longitude=self.astro.location.lon).rad)
                    c.JDMark = float(t.jd)
                    c.DateStr, c.TimeStr = t.utc.isot.split("T")
                    c.Used = p.get("included",True)
                    self.model.Core.AddCalPoint(c)
                    self.model.Core.UseCalPoint(c, c.Used)
                if backend=='legacy-pxp':
                    directory=ROOT/'python-mount-service/runtime/models';directory.mkdir(parents=True,exist_ok=True)
                    input_path=directory/f'legacy-input-{uuid.uuid4().hex}.pxp'
                    self.model.SaveFile(str(input_path))
                    self.model.LoadFile(str(input_path))
                self.model.Core.Calibrate()
                self._validate()
                directory = ROOT/"python-mount-service/runtime/models"
                directory.mkdir(parents=True, exist_ok=True)
                filename = directory/f"calibration-{self.astro.time().utc.isot.replace(':','').replace('.','')}.pxp"
                self.model.SaveFile(str(filename))
                content = filename.read_text(encoding="utf-8-sig")
                # This vendor SaveFile writes stars/enables, but omits fitted coefficients.
                # Preserve its native file and append the actual Core coefficients.
                terms = [float(self.model.Core.GetMountError(i)) for i in range(15)]
                if not all(math.isfinite(v) for v in terms):
                    raise ValueError("PXPLib fitted coefficients are not finite")
                content += "\nModel Terms:\n" + "\n".join(format(v, '.17g') for v in terms) + "\n"
                filename.write_text(content, encoding="utf-8")
                if "Model Terms" not in content or "Cal Star" not in content:
                    raise ValueError("PXPLib export is missing fitted terms or stars")
                self.active, self.path, self.backend = True, str(filename), backend
                self.error=None
                result={**self.status(),**self.diagnostics()}
                # Completion offers Use This Model / Keep Current Model, as in C#.
                if not activate:
                    if previous:self.model.LoadFile(previous)
                    self.active,self.path,self.backend=previous_meta
                return result
            except Exception:
                self.active,self.path,self.backend=previous_meta
                if previous:
                    self.model.LoadFile(previous)
                else:
                    self.active = False
                raise
            return self.status()

    def unload(self):
        with self.lock:
            self.active=False;self.path=None;self.backend='none';self.error=None
            return self.status()

    def inspect_file(self, filename):
        """Inspect an existing file without changing the operator's runtime model."""
        with self.lock:
            previous=(self.active,self.path,self.backend,self.error)
            try:
                self.load(filename)
                return {**self.status(),**self.diagnostics()}
            finally:
                if previous[1]:self.model.LoadFile(previous[1])
                self.active,self.path,self.backend,self.error=previous

    def diagnostics(self):
        with self.lock:
            if not self.active:return {'residuals':[],'coefficients':[]}
            core=self.model.Core;scale=206264.80624709636
            residuals=[]
            for i in range(int(core.NumCalPoints)):
                c=core.GetCalPoint(i+1)
                residuals.append({'index':i+1,'name':str(c.Name),'used':bool(c.Used),
                                  'azimuth_deg':math.degrees(float(c.PhiEnc)), 'altitude_deg':math.degrees(float(c.ThetaEnc)),
                                  'ra_error_arcsec':float(c.RASkyErrRad)*scale,'dec_error_arcsec':float(c.DecErrRad)*scale,
                                  'total_error_arcsec':float(c.DistErrRad)*scale})
            coefficients=[{'index':i,'enabled':bool(core.IsParamEnabled(i)),'value_arcsec':float(core.GetMountError(i))*scale} for i in range(15)]
            return {'residuals':residuals,'coefficients':coefficients}

    def corrected(self, azimuth, altitude):
        with self.lock:
            if not self.active:
                return azimuth, altitude
            alt, az = self.model.Core.EncToAltAz(math.radians(altitude), math.radians(azimuth), 0., 0.)
            if not all(math.isfinite(float(v)) for v in (az, alt)):
                raise ValueError("PointXP returned invalid coordinates")
            return math.degrees(az)%360, math.degrees(alt)

    def encoder_target(self, target):
        with self.lock:
            if not self.active:
                return target["azimuth_deg"], target["altitude_deg"]
            theta, phi = self.model.ApparentToEncoder(target["lst_rad"], target["apparent_ra_rad"],
                                                    target["apparent_dec_rad"], 1, 0., 0.)
            # The installed wrapper drops its out arguments (returns 0,0).
            # Still exercise it, then use the underlying API and validate the result.
            if theta == 0 and phi == 0:
                theta, phi = self.model.Core.AppToEnc(target["lst_rad"], target["apparent_ra_rad"],
                                                     target["apparent_dec_rad"], 1, 0., 0.)
            az, alt = math.degrees(phi)%360, math.degrees(theta)
            # Verify the target conversion against Core correction, not wrapper zero output.
            check_az, check_alt = self.corrected(az, alt)
            angular = abs((check_az-target["azimuth_deg"]+180)%360-180)
            if angular > 1 or abs(check_alt-target["altitude_deg"]) > 1:
                raise ValueError("PointXP target conversion failed its round-trip check")
            return az, alt


def parse_pxp_points(path):
    """Read native Cal Star blocks, retaining solved J2000 and measured encoders."""
    from astropy.time import Time
    content=Path(path).read_text(encoding='utf-8-sig')
    blocks=re.split(r'Cal Star\s+\d+:',content)[1:]
    points=[]
    def pair(block,heading):
        match=re.search(re.escape(heading)+r'\s*\n\s*([^\n]+)\n\s*([^\n]+)',block)
        if not match:raise ValueError('Missing '+heading+' in Cal Star')
        return float(match[1].strip()),float(match[2].strip())
    for i,block in enumerate(blocks):
        ra,dec=pair(block,'RA, Dec:');alt,az=pair(block,'ThetaEnc, PhiEnc:');lst,jd=pair(block,'LST, JD:')
        if not all(math.isfinite(v) for v in (ra,dec,alt,az,lst,jd)):raise ValueError('Non-finite Cal Star values')
        points.append({'id':uuid.uuid4().hex,'name':block.strip().splitlines()[0],'included':True,
                       'solved_ra_hours':math.degrees(ra)/15,'solved_dec_deg':math.degrees(dec),
                       'encoder_altitude_deg':math.degrees(alt),'encoder_azimuth_deg':math.degrees(az),
                       'capture_utc':Time(jd,format='jd').utc.isot,'origin':'loaded-pxp'})
    used=re.search(r'Cal Points Used:\s*\n((?:\s*[01]\s*\n)+)',content)
    if used:
        for point,flag in zip(points,used[1].split()):point['included']=flag=='1'
    return points
