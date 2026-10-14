import asyncio
import time
import csv
import io
import orjson
import gzip
from contextlib import asynccontextmanager
from pathlib import Path
from typing import Literal

from fastapi.responses import JSONResponse
from fastapi import FastAPI, HTTPException, Request, WebSocket, WebSocketDisconnect
from fastapi.responses import FileResponse, Response
from fastapi.staticfiles import StaticFiles
from pydantic import BaseModel, ConfigDict, Field, model_validator

from .domain import AXES, AXIS_MAP, DriveError
from .service import MountService
from .observatory import Observatory
from .hardware import HardwareWorkbench, PARAMETERS


class CameraSeriesName(BaseModel):
    model_config=ConfigDict(extra='forbid',str_strip_whitespace=True)
    name:str=Field(min_length=1,max_length=120)


class CameraCooling(BaseModel):
    model_config = ConfigDict(extra='forbid', strict=True)
    enabled: bool | None = None
    target_c: int | None = None


class SkyDemoStart(BaseModel):
    model_config = ConfigDict(extra='forbid')
    object_ids: list[str | int] = Field(min_length=1, max_length=5000)


class SkyDemoHeartbeat(BaseModel):
    model_config = ConfigDict(extra='forbid')
    token: str = Field(min_length=32, max_length=32)


class TopologyGroup(BaseModel):
    model_config=ConfigDict(extra='forbid')
    port:str=Field(max_length=30)
    serial_number:str=Field(max_length=40)
    baud:int
    axes:dict[str,int]

class TopologyMapping(BaseModel):
    mount:TopologyGroup
    imaging:TopologyGroup

class TopologyAction(BaseModel):
    group:Literal['mount','imaging']

class CCTVConfig(BaseModel):
    model_config=ConfigDict(extra='forbid')
    host:str=Field(min_length=1,max_length=64)
    scheme:Literal['http','https']='http'
    channel:int=Field(default=1,ge=1,le=16)
    username:str=Field(default='',max_length=120)
    password:str=Field(default='',max_length=200)


class ModelSave(BaseModel):
    model_config=ConfigDict(extra='forbid')
    directory:str=Field(min_length=1,max_length=1024)
    name:str=Field(min_length=1,max_length=180)


class ParameterEdit(BaseModel):
    model_config = ConfigDict(extra='forbid', allow_inf_nan=False)
    command: str = Field(max_length=20)
    value: float
    expected: float


class MappedParameterEdit(ParameterEdit):
    target:dict

class ZeroPositionConfirmation(BaseModel):
    model_config = ConfigDict(extra='forbid', allow_inf_nan=False)
    target: dict
    socket: int = Field(ge=1, le=8)
    sensor_type: int
    position: float
    feedback: float
    offset_low: int
    offset_high: int

class AxisCommand(BaseModel):
    model_config = ConfigDict(extra="forbid", allow_inf_nan=False)
    action: Literal["enable", "disable", "stop", "jog", "goto"]
    value: float | None = None
    speed: float | None = Field(default=None, gt=0)
    lease: float = Field(default=0.8, ge=0.1, le=2)


class OpticsCommand(BaseModel):
    model_config=ConfigDict(extra='forbid',allow_inf_nan=False)
    action:Literal['enable','disable','stop','goto','jog']
    value:float|None=None
    speed:float|None=Field(default=None,gt=0)
    step:float|None=None
    lease:float=Field(default=0.8,ge=0.1,le=2)
    target:dict

class OpticsAxisSettings(BaseModel):
    model_config=ConfigDict(extra='forbid',allow_inf_nan=False)
    minimum:float
    maximum:float
    unit_per_count:float=Field(gt=0)
    target:dict

class TrackingTolerance(BaseModel):
    model_config=ConfigDict(extra='forbid',allow_inf_nan=False)
    tolerance_arcsec_s:float=Field(gt=0)

class M3Preset(BaseModel):
    model_config=ConfigDict(extra='forbid',allow_inf_nan=False)
    port1:float
    minimum:float=0
    maximum:float=359
    in_position_window:float=Field(default=0.01,gt=0,lt=90)
    jog_speed:float|None=Field(default=None,gt=0)
    target:dict
    expected:dict[str,float]

class Connection(BaseModel):
    model_config = ConfigDict(extra="forbid")
    mode: Literal["simulation", "hardware"] = "hardware"
    port: str = Field(default="", max_length=30)
    baud: int = Field(default=921600, ge=9600, le=921600)
    poll_interval_ms: float = Field(default=5, ge=5, le=1000)
    serial_number: str = Field(default="", max_length=40)


class OrbitalElements(BaseModel):
    model_config = ConfigDict(extra='forbid', allow_inf_nan=False)
    name: str = Field(default='', max_length=120)
    eccentricity: float = Field(ge=0, lt=1)
    inclination_deg: float = Field(ge=0, le=180)
    ascending_node_deg: float = Field(ge=0, lt=360)
    arg_perihelion_deg: float = Field(ge=0, lt=360)
    semi_major_axis_au: float | None = Field(default=None, gt=0)
    epoch_jd: float | None = Field(default=None, ge=2000000, le=3000000)
    mean_anomaly_deg: float | None = Field(default=None, ge=0, lt=360)
    perihelion_distance_au: float | None = Field(default=None, gt=0)
    perihelion_epoch_jd: float | None = Field(default=None, ge=2000000, le=3000000)

    @model_validator(mode='after')
    def complete_orbit(self):
        asteroid = self.semi_major_axis_au is not None
        required = (self.epoch_jd, self.mean_anomaly_deg) if asteroid else (self.perihelion_distance_au, self.perihelion_epoch_jd)
        if any(v is None for v in required):
            raise ValueError('Provide complete asteroid or comet elements')
        if asteroid and (self.perihelion_distance_au is not None or self.perihelion_epoch_jd is not None):
            raise ValueError('Choose either asteroid or comet elements')
        if not asteroid and (self.epoch_jd is not None or self.mean_anomaly_deg is not None):
            raise ValueError('Choose either asteroid or comet elements')
        return self


class SatelliteElements(BaseModel):
    model_config = ConfigDict(extra='forbid')
    name: str = Field(min_length=1, max_length=120)
    line1: str = Field(min_length=69, max_length=69)
    line2: str = Field(min_length=69, max_length=69)

    @model_validator(mode='after')
    def valid_tle(self):
        for prefix, line in [('1 ', self.line1), ('2 ', self.line2)]:
            checksum = sum(int(c) if c.isdigit() else 1 if c == '-' else 0 for c in line[:68]) % 10
            if not line.startswith(prefix) or not line[-1].isdigit() or checksum != int(line[-1]):
                raise ValueError('Invalid TLE line or checksum')
        if self.line1[2:7] != self.line2[2:7]:
            raise ValueError('TLE satellite IDs do not match')
        return self


class Target(BaseModel):
    model_config = ConfigDict(extra="forbid", allow_inf_nan=False)
    ra_hours: float = Field(default=0, ge=0, lt=24)
    dec_deg: float = Field(default=0, ge=-90, le=90)
    body: Literal["sun","moon","mercury","venus","mars","jupiter","saturn","uranus","neptune"] | None = None
    orbit: OrbitalElements | None = None
    tle: SatelliteElements | None = None

    @model_validator(mode='after')
    def one_source(self):
        if sum(v is not None for v in (self.body, self.orbit, self.tle)) > 1:
            raise ValueError('Choose one target source')
        return self


class MovingDemoStart(BaseModel):
    objects: list[Target] = Field(min_length=1, max_length=500)
    in_position_arcsec: float = Field(default=10, gt=0, le=3600, allow_inf_nan=False)


class Exposure(BaseModel):
    model_config = ConfigDict(extra="forbid", allow_inf_nan=False)
    exposure_s: float = Field(default=1, ge=.001, le=600)
    binning: int = Field(default=1, ge=1, le=8)
    gain: int = Field(default=0, ge=0, le=1000)
    dark: bool = False
    frame_type:Literal["Light","Dark","Bias"]|None=None
    readout:dict[str,str]=Field(default_factory=dict)


class CameraROI(BaseModel):
    model_config=ConfigDict(extra="forbid")
    x:int=Field(ge=0)
    y:int=Field(ge=0)
    width:int=Field(ge=1)
    height:int=Field(ge=1)

class CameraExposure(Exposure):
    roi:CameraROI|None=None
    telescope_id: str | None = Field(default=None, min_length=1, max_length=100)


class VideoExposure(CameraExposure):
    frames:int=Field(default=100,ge=1,le=1000)
    record:bool=True
    image_format:Literal["RAW8","RAW16"]="RAW16"
    preview_fps:int=Field(default=15,ge=1,le=30)
    video_controls:dict[str,int]=Field(default_factory=dict)
    auto_exposure:bool=False
    auto_gain:bool=False

class ImagingSetup(Exposure):
    model_config = ConfigDict(extra='forbid', allow_inf_nan=False, str_strip_whitespace=True)
    name: str = Field(min_length=1, max_length=100)
    telescope_id: str = Field(min_length=1, max_length=100)
    device_key: str = Field(min_length=1, max_length=256)
    pixel_um: float = Field(gt=0, le=100)
    notes: str = Field(default='', max_length=2000)


class Solve(Target):
    pixel_scale: float = Field(default=1, ge=.01, le=120)


class ModelFile(BaseModel):
    path: str = Field(min_length=1,max_length=1000)


class PointSelection(BaseModel):
    model_config = ConfigDict(extra="forbid")
    included: bool


class CombinedSlew(BaseModel):
    model_config=ConfigDict(extra='forbid',allow_inf_nan=False)
    azimuth:float
    altitude:float
    azimuth_speed:float | None=Field(default=None,gt=0)
    altitude_speed:float | None=Field(default=None,gt=0)


class TelescopeSpecification(BaseModel):
    model_config=ConfigDict(extra='forbid',allow_inf_nan=False,str_strip_whitespace=True)
    name:str=Field(min_length=1,max_length=100)
    focal_length_mm:float=Field(gt=0,le=100000)
    aperture_mm:float|None=Field(default=None,gt=0,le=10000)
    optical_design:str=Field(default='',max_length=100)
    manufacturer_model:str=Field(default='',max_length=150)
    optical_multiplier:float=Field(default=1,gt=0,le=20)
    backfocus_mm:float|None=Field(default=None,ge=0,le=10000)
    obstruction_mm:float|None=Field(default=None,ge=0,le=10000)
    tube_length_mm:float|None=Field(default=None,gt=0,le=10000)
    weight_kg:float|None=Field(default=None,gt=0,le=10000)
    mounting_position:str=Field(default='',max_length=100)
    notes:str=Field(default='',max_length=2000)

    @model_validator(mode='after')
    def valid_obstruction(self):
        if self.aperture_mm is not None and self.obstruction_mm is not None and self.obstruction_mm>=self.aperture_mm:
            raise ValueError('Central obstruction must be smaller than aperture')
        return self


class NtpServer(BaseModel):
    model_config=ConfigDict(extra='forbid')
    server:str=Field(min_length=1,max_length=300)


class ModelLocation(BaseModel):
    model_config=ConfigDict(extra='forbid',allow_inf_nan=False)
    latitude:float=Field(ge=-90,le=90)
    longitude:float=Field(ge=-180,le=180)
    elevation:float=Field(ge=-500,le=10000)

class CalibrationSettings(BaseModel):
    model_config=ConfigDict(extra='forbid',allow_inf_nan=False)
    backend:Literal['legacy-pxp','experimental-core-direct']='legacy-pxp'
    azimuth_points:int=Field(default=8,ge=1,le=24)
    altitude_points:int=Field(default=5,ge=1,le=12)
    point_count:int|None=Field(default=None,ge=1,le=288)
    min_azimuth:float=Field(default=0,ge=0,lt=360)
    max_azimuth:float=Field(default=360,gt=0,le=360)
    min_altitude:float=Field(default=30,ge=10,le=85)
    max_altitude:float=Field(default=85,ge=10,le=85)
    exposure_s:float=Field(default=3,ge=.001,le=600)
    binning:int=Field(default=2,ge=1,le=8)
    gain:int=Field(default=0,ge=0,le=1000)
    pixel_scale:float=Field(default=.6,gt=0,le=120)
    tracking_seconds:float=Field(default=8,ge=0,le=120)
    arrival_deg:float=Field(default=.5,gt=0,le=1)
    rms_threshold_arcsec:float=Field(default=3,gt=0,le=300)
    stable_seconds:float=Field(default=1,ge=.1,le=30)
    minimum_samples:int=Field(default=6,ge=3,le=100)
    extra_settle_s:float=Field(default=.75,ge=0,le=30)
    post_exposure_s:float=Field(default=.5,ge=0,le=10)
    rms_timeout_s:float=Field(default=20,ge=1,le=180)
    @model_validator(mode='after')
    def ranges(self):
        if self.min_azimuth>=self.max_azimuth:raise ValueError('Azimuth start must be less than azimuth end')
        if self.min_altitude>self.max_altitude:raise ValueError('Minimum altitude must not exceed maximum altitude')
        if self.tracking_seconds+self.stable_seconds+self.extra_settle_s>self.rms_timeout_s:raise ValueError('RMS timeout is shorter than requested settling time')
        return self

class CalibrationAction(BaseModel):
    model_config=ConfigDict(extra='forbid')
    action:Literal['new','grid','start','pause','resume','stop','reset','rerun','add-current','fit','use','keep','delete']
    identifiers:list[str]=Field(default_factory=list,max_length=288)

class ImageStretch(BaseModel):
    model_config=ConfigDict(extra='forbid',allow_inf_nan=False)
    profile:Literal['Auto','Low','Medium','High','BrightStar','Moon','Planet','DeepSky','Manual']
    black:float=Field(ge=0,le=65534)
    white:float=Field(gt=0,le=65535)
    tone:float=Field(gt=0,le=30)
    zoom:float=Field(default=1,ge=1,le=8)
    pan_x:float=Field(default=0,ge=-10000,le=10000)
    pan_y:float=Field(default=0,ge=-10000,le=10000)
    @model_validator(mode='after')
    def ordered(self):
        if self.white<=self.black:raise ValueError('White point must exceed black point')
        return self

class AnalysisRequest(BaseModel):
    model_config=ConfigDict(extra='forbid')
    kind:Literal['seeing-tracking','pointing-stability']
    frames:int=Field(default=9,ge=2,le=100)
    interval_ms:int=Field(default=250,ge=0,le=10000)
    positions:int=Field(default=5,ge=1,le=40)

def create_app(service: MountService | None = None) -> FastAPI:
    from .audit import AuditStore, AuditMiddleware
    audit = AuditStore()
    mount = service or MountService()
    mount.audit = audit
    observatory = Observatory(mount)
    observatory.audit = audit
    from .pointing_workbench import PointingWorkbench
    pointing_workbench=PointingWorkbench(observatory)
    observatory.pointing_workbench=pointing_workbench
    from .cctv import CCTV
    cctv=CCTV(observatory.directory)
    from .drive_topology import DriveTopology
    topology=DriveTopology(mount,observatory.directory)
    from .mapped_hardware import MappedHardware
    mapped_hardware=MappedHardware(topology)
    from .optics import OpticsControl
    optics=OpticsControl(mount,mapped_hardware,observatory.directory)

    from .drive_watchdog import DriveWatchdog
    async def prepare_recovery():
        await observatory.moving_demo.stop()
        await observatory.demo.stop('Demo stopped · drive connection recovery')
        await pointing_workbench.stop(False)
        await observatory.stop_tracking()
    watchdog=DriveWatchdog(topology,prepare_recovery)

    from .windows_clock import ClockMonitor
    clock_monitor=ClockMonitor(pointing_workbench,observatory)

    @asynccontextmanager
    async def lifespan(app):
        await asyncio.to_thread(audit.record, 'service.started', {})
        try:channels,external=await topology.prepare_mount_connection()
        except Exception as exc:
            topology.error=str(exc)
            channels,external=topology.configure_routes()
        mount.transport.axis_channels=channels
        mount.transport.axis_external_queries=external
        await mount.start()
        await observatory.start()
        watchdog_task=asyncio.create_task(watchdog.run(),name="drive-watchdog")
        clock_task=asyncio.create_task(clock_monitor.run(),name='server-clock-monitor')
        yield
        clock_task.cancel()
        try:await clock_task
        except asyncio.CancelledError:pass
        watchdog_task.cancel()
        try:await watchdog_task
        except asyncio.CancelledError:pass
        await pointing_workbench.stop(False)
        await topology.disconnect('imaging')
        await observatory.close()
        await mount.close()
        await asyncio.to_thread(audit.record, 'service.stopped', {})
        await asyncio.to_thread(audit.close)

    app = FastAPI(title="NARIT Mount Service", version="0.1.0", lifespan=lifespan)
    app.state.audit = audit
    app.add_middleware(AuditMiddleware, store=audit)

    @app.post('/api/audit/events')
    async def audit_events(request: Request):
        if int(request.headers.get('content-length', '0')) > 131072:
            raise HTTPException(413, 'Audit batch too large')
        raw = await request.body()
        if len(raw)>131072:raise HTTPException(413,'Audit batch too large')
        try: batch=orjson.loads(raw)
        except ValueError:raise HTTPException(422,'Invalid audit batch')
        if not isinstance(batch,dict) or not isinstance(batch.get('events'),list) or len(batch['events'])>100:
            raise HTTPException(422,'Expected at most 100 events')
        for event in batch['events']:
            if not isinstance(event,dict) or not isinstance(event.get('kind'),str) or not isinstance(event.get('event_id'),str):
                raise HTTPException(422,'Invalid audit event')
        def save():
            for event in batch['events']:
                saved=audit.record(event['kind'][:80],event.get('details',{}),source='browser',event_id=event['event_id'][:128],client_utc=str(event.get('utc',''))[:40],session_id=str(batch.get('session_id',''))[:128],action_id=str(event.get('action_id',''))[:128],page=str(event.get('page',''))[:128])
                if not saved:raise HTTPException(503,'Audit storage unavailable')
        await asyncio.to_thread(save)
        return {'accepted':len(batch['events'])}

    @app.get('/api/audit/events')
    async def audit_read(limit: int=200, before: int | None=None, session_id: str | None=None, action_id: str | None=None):
        rows=await asyncio.to_thread(audit.query,max(1,min(limit,1000)),before,session_id,action_id)
        return {'events':rows,'next_before':rows[-1]['id'] if rows else None}

    @app.middleware('http')
    async def refresh_dashboard_assets(request, call_next):
        response = await call_next(request)
        if request.url.path == '/' or request.url.path.startswith('/assets/'):
            response.headers['Cache-Control'] = 'no-cache, must-revalidate'
        display_versions={'/assets/sky-display-worker.js':'20261013-client-3',
                          '/assets/vendor/satellite.js-satellite.min.js':'6.0.1',
                          '/assets/vendor/astronomy-engine-astronomy.browser.min.js':'2.1.19'}
        if request.query_params.get('v')==display_versions.get(request.url.path) and request.url.path in display_versions:
            response.headers['Cache-Control']='private, max-age=31536000, immutable'
        return response

    app.state.drive_topology = topology
    app.state.mount = mount
    app.state.observatory = observatory
    app.state.pointing_workbench=pointing_workbench
    hardware = HardwareWorkbench(mount)
    hardware.audit_store = audit

    @app.get('/api/hardware/catalogue')
    async def hardware_catalogue():
        return dict(family='Platinum', parameters=PARAMETERS, source='EAS II Platinum Command Reference',
                    auto_phasing_available=False)

    @app.get('/api/hardware/{axis_id}/parameters')
    async def hardware_read(axis_id: str, group: str):
        try:
            return await hardware.read(axis_id, group)
        except DriveError as exc:
            raise HTTPException(409, str(exc)) from exc

    @app.post('/api/hardware/{axis_id}/parameter')
    async def hardware_write(axis_id: str, body: ParameterEdit):
        if observatory.status().get('tracking'):
            raise HTTPException(409, 'Stop tracking before editing hardware configuration')
        try:
            return await hardware.write(axis_id, body.command, body.value, body.expected)
        except DriveError as exc:
            raise HTTPException(409, str(exc)) from exc

    @app.get('/api/hardware/{axis_id}/sensors/{socket}')
    async def sensor_read(axis_id: str,socket: int):
        try:return await hardware.sensor_read(axis_id,socket)
        except DriveError as exc:raise HTTPException(409,str(exc)) from exc

    @app.post('/api/hardware/{axis_id}/sensors/{socket}')
    async def sensor_write(axis_id: str,socket: int,body: ParameterEdit):
        if observatory.status().get('tracking'):raise HTTPException(409,'Stop tracking before configuring feedback')
        try:return await hardware.sensor_write(axis_id,socket,body.command,body.value,body.expected)
        except DriveError as exc:raise HTTPException(409,str(exc)) from exc

    @app.post('/api/hardware/save')
    async def hardware_save():
        if observatory.status().get('tracking'):
            raise HTTPException(409, 'Stop tracking before saving hardware configuration')
        try:
            async with topology.operation_lock:
                result = await hardware.save()
                optics._status_cache.clear()
                return result
        except DriveError as exc:
            raise HTTPException(409, str(exc)) from exc
    web = Path(__file__).parent / "web"

    @app.middleware("http")
    async def same_origin_commands(request: Request, call_next):
        # Local/Tailscale UI sends same-origin commands; reject cross-site form/fetch writes.
        if request.method in ("POST", "PUT", "DELETE", "PATCH"):
            origin = request.headers.get("origin")
            if origin and origin != f"{request.url.scheme}://{request.headers.get('host')}":
                return Response("Cross-origin commands are not allowed", status_code=403)
        return await call_next(request)

    @app.get("/")
    async def dashboard():
        return FileResponse(web / "index.html")

    @app.get("/api/health")
    async def health():
        return {"ok": True, "service": "narit-mount-service", "version": "0.1.0", "mode": mount.transport.mode}

    @app.get("/api/state")
    async def state():
        return {**mount.snapshot(), "observatory": observatory.status(), "drive_watchdog":watchdog.states}

    @app.get("/api/history")
    async def history(request: Request, seconds: float = 60):
        seconds = max(1, min(seconds, 600))
        cutoff = mount.snapshot()["elapsed_s"] - seconds
        # Avoid recursive encoding of tens of thousands of real acquisition frames.
        payload = orjson.dumps({"samples": [s for s in mount.history if s["t"] >= cutoff]})
        headers = {"Vary": "Accept-Encoding"}
        if "gzip" in request.headers.get("accept-encoding", ""):
            payload = await asyncio.to_thread(gzip.compress, payload, compresslevel=1)
            headers["Content-Encoding"] = "gzip"
        return Response(payload, media_type="application/json", headers=headers)

    @app.get("/api/history.csv")
    async def history_csv():
        output = io.StringIO()
        writer = csv.writer(output)
        writer.writerow(["elapsed_s", "axis", "mode", "position_deg", "velocity_deg_s", "current_a", "command_velocity_deg_s", "valid"])
        for sample in list(mount.history):
            for key, axis in sample["axes"].items():
                writer.writerow([sample["t"], key, mount.transport.mode, axis["position_deg"],
                                 axis["velocity_deg_s"], axis["current_a"], axis["command_velocity"], axis["valid"]])
        return Response(output.getvalue(), media_type="text/csv",
                        headers={"Content-Disposition": 'attachment; filename="mount-telemetry.csv"'})

    @app.post("/api/axes/{axis_id}/command")
    async def axis_command(axis_id: str, command: AxisCommand):
        if axis_id not in AXIS_MAP:
            raise HTTPException(404, "Unknown axis")
        try:
            if command.action in ('stop','disable'):
                await observatory.moving_demo.stop()
                await observatory.demo.stop()
            if command.action in ('stop','disable'):await pointing_workbench.stop(False)
            else:pointing_workbench.idle_required()
            await observatory.stop_tracking()
            return await mount.command(axis_id, command.action, command.value, command.speed, command.lease)
        except DriveError as exc:
            raise HTTPException(409, str(exc)) from exc

    @app.post("/api/stop")
    async def stop_all():
        try:
            await observatory.moving_demo.stop()
            await observatory.demo.stop()
            await pointing_workbench.stop(False)
            await observatory.stop_tracking()
            result=await mount.stop_all()
            await optics.stop_accessories()
            return result
        except DriveError as exc:
            raise HTTPException(409, str(exc)) from exc

    @app.get('/api/inputs')
    async def inputs():
        return mount.snapshot()['inputs']

    @app.post('/api/axes/combined/goto')
    async def combined_goto(body:CombinedSlew):
        pointing_workbench.idle_required()
        await observatory.stop_tracking()
        try:return await mount.goto_pair(**body.model_dump())
        except DriveError as exc:raise HTTPException(409,str(exc)) from exc

    @app.post("/api/connect")
    async def connect(body: Connection):
        watchdog.manual_connect("mount")
        pointing_workbench.idle_required()
        try:
            await observatory.stop_tracking()
            if topology.port and topology.port.port.upper()==body.port.upper():
                raise DriveError('COM port already owned by Imaging drive')
            if body.mode=='hardware':await topology.prepare_mount_connection()
            return await mount.connect(body.mode, body.port, body.baud, body.serial_number, body.poll_interval_ms)
        except DriveError as exc:
            raise HTTPException(409, str(exc)) from exc

    @app.post("/api/disconnect")
    async def disconnect():
        await observatory.moving_demo.stop()
        await observatory.demo.stop()
        watchdog.manual_disconnect("mount")
        await pointing_workbench.stop(False)
        await observatory.stop_tracking()
        try:return await mount.disconnect()
        except DriveError as exc:raise HTTPException(409,str(exc)) from exc

    @app.exception_handler(DriveError)
    @app.exception_handler(ValueError)
    async def observatory_error(request, exc):
        return Response(orjson.dumps({"detail":str(exc)}),status_code=409,media_type="application/json")

    @app.post("/api/enable-all")
    async def enable_all():
        pointing_workbench.idle_required()
        before=mount.snapshot()
        if not all(a['valid'] for a in before['axes']):
            raise HTTPException(409,"All four axes need fresh telemetry before Enable All")
        changed=[]
        try:
            for a in before['axes']:
                if not a['enabled']:
                    await mount.command(a['id'],'enable')
                    changed.append(a['id'])
        except DriveError as exc:
            for axis in changed:
                try:await mount.command(axis,'disable')
                except DriveError:pass
            raise HTTPException(409,str(exc)) from exc
        return {"ok":True,"state":mount.snapshot()}

    @app.post("/api/observatory/tracking-tolerance")
    async def tracking_tolerance(body:TrackingTolerance):
        return observatory.set_tracking_tolerance(body.tolerance_arcsec_s)

    @app.get("/api/observatory")
    async def observatory_state():
        return observatory.status()

    @app.get("/api/astronomy/stars")
    async def stars():
        return {"source":"BSC5 · J2000","stars":await observatory.call(observatory.astro.stars)}

    @app.get("/api/astronomy/sky-catalog")
    async def sky_catalog():
        return await observatory.call(observatory.astro.sky_catalog)

    @app.get('/api/astronomy/display-context')
    async def display_context():
        def context():
            now=observatory.astro.time()
            return {'server_unix_ms':float(now.unix)*1000,'site':dict(observatory.astro.site),
                    'dut1_s':float(now.delta_ut1_utc)}
        return await observatory.call(context)

    @app.get('/api/astronomy/display-catalog/{category}')
    async def display_catalog(category:Literal['astronomy','asteroid','comet']):
        if category=='astronomy':
            return {'objects':await observatory.call(observatory.astro.display_objects),'source':'BSC5 · OpenNGC · J2000'}
        from .small_body_catalog import catalog
        try:return await asyncio.to_thread(catalog,category)
        except Exception as exc:raise HTTPException(502,'Object catalog unavailable: '+str(exc)) from exc

    @app.post("/api/astronomy/preview")
    async def preview(target: Target):
        return await observatory.preview(target.model_dump(exclude_none=True))

    @app.post('/api/astronomy/moving/demo/start')
    async def moving_demo_start(settings: MovingDemoStart):
        pointing_workbench.idle_required()
        return await observatory.moving_demo.start([o.model_dump(exclude_none=True) for o in settings.objects], settings.in_position_arcsec)

    @app.post('/api/astronomy/moving/demo/heartbeat')
    async def moving_demo_heartbeat(settings: SkyDemoHeartbeat):
        return observatory.moving_demo.heartbeat(settings.token)

    @app.post('/api/astronomy/moving/demo/stop')
    async def moving_demo_stop():
        return await observatory.moving_demo.stop()

    @app.post('/api/astronomy/demo/start')
    async def sky_demo_start(settings: SkyDemoStart):
        pointing_workbench.idle_required()
        if observatory.moving_demo.active:raise ValueError('Stop the moving-object demo first')
        return await observatory.demo.start(settings.object_ids)

    @app.post('/api/astronomy/demo/heartbeat')
    async def sky_demo_heartbeat(settings: SkyDemoHeartbeat):
        return observatory.demo.heartbeat(settings.token)

    @app.get('/api/astronomy/demo/status')
    async def sky_demo_status():
        return observatory.demo.snapshot()

    @app.post('/api/astronomy/demo/stop')
    async def sky_demo_stop():
        return await observatory.demo.stop()

    @app.post("/api/astronomy/goto")
    async def sky_goto(target: Target):
        pointing_workbench.idle_required()
        try:return await observatory.goto(target.model_dump(exclude_none=True))
        except DriveError as exc:raise HTTPException(409,str(exc)) from exc

    @app.post('/api/astronomy/moving/preview')
    async def moving_preview(target: Target):
        return await moving_sample(target.model_dump(exclude_none=True))

    @app.get('/api/astronomy/satellites/catalog/{category}')
    async def satellite_catalog(category: str):
        from .satellite_catalog import catalog
        try:return {'objects':await asyncio.to_thread(catalog, category)}
        except Exception as exc:raise HTTPException(502, 'NARIT catalog unavailable: '+str(exc)) from exc

    @app.post('/api/astronomy/satellites/trajectory')
    async def satellite_trajectory(tle: SatelliteElements):
        from .satellite_catalog import trajectory
        try:return await asyncio.to_thread(trajectory,tle.model_dump(),dict(observatory.astro.site))
        except Exception as exc:raise HTTPException(422, 'Trajectory unavailable') from exc

    @app.get('/api/astronomy/small-bodies/{category}')
    async def small_body_sky(category: Literal['asteroid','comet']):
        from .small_body_catalog import sky_frame
        from .astronomy import Astronomy
        site=dict(observatory.astro.site)
        try:return await asyncio.to_thread(sky_frame,Astronomy(**site),category)
        except Exception as exc:
            await asyncio.to_thread(audit.record,'small_bodies.catalog.failed',{'category':category,'exception':type(exc).__name__,'message':str(exc)})
            raise HTTPException(502,'Small-body catalog unavailable; retry or enter orbital elements') from exc

    @app.websocket('/ws/astronomy/satellites')
    async def satellite_stream(websocket: WebSocket):
        from concurrent.futures import ThreadPoolExecutor
        from .satellite_catalog import SatelliteFrame
        import time
        origin,host=websocket.headers.get('origin'),websocket.headers.get('host')
        if origin and origin not in (f'http://{host}',f'https://{host}'):
            await websocket.close(code=1008);return
        await websocket.accept()
        stream_id=__import__('uuid').uuid4().hex
        stage='catalog validation'
        await asyncio.to_thread(audit.record,'satellites.stream.started',{'stream_id':stream_id})
        executor=ThreadPoolExecutor(max_workers=1)
        loop=asyncio.get_running_loop()
        try:
            raw=await asyncio.wait_for(websocket.receive_json(),30)
            if not isinstance(raw,list) or len(raw)>20000:raise ValueError('Invalid catalog')
            rows=[SatelliteElements.model_validate(row).model_dump() for row in raw]
            stage='frame initialization'
            frame=await loop.run_in_executor(executor,SatelliteFrame,rows,dict(observatory.astro.site))
            previous=None
            while True:
                started=time.perf_counter()
                stage='propagation'
                sample=await loop.run_in_executor(executor,frame.sample)
                sample['sample_interval_ms']=None if previous is None else (started-previous)*1000
                previous=started
                stage='send frame'
                await asyncio.wait_for(websocket.send_json(sample),15)
                stage='wait for acknowledgement'
                await asyncio.wait_for(websocket.receive_text(),30)
                # This is a catalog display, independent of 100 ms motor tracking.
                sample_interval=1.0
                await asyncio.sleep(max(0,sample_interval-(time.perf_counter()-started)))
        except WebSocketDisconnect:
            await asyncio.to_thread(audit.record,'satellites.stream.disconnected',{'stream_id':stream_id,'stage':stage})
        except Exception as exc:
            await asyncio.to_thread(audit.record,'satellites.stream.failed',{'stream_id':stream_id,'stage':stage,'exception':type(exc).__name__,'message':str(exc)})
            try:await websocket.send_json({'error':'Satellite stream interrupted; reconnecting','stream_id':stream_id})
            except Exception:pass
        finally:
            executor.shutdown(wait=False,cancel_futures=True)
            try:await websocket.close()
            except (RuntimeError,WebSocketDisconnect):pass

    forecast_slots = asyncio.Semaphore(2)

    @app.post('/api/astronomy/moving/visibility')
    async def moving_visibility(target: Target, request: Request):
        from .visibility import forecast
        import threading
        cancelled = threading.Event()
        async with forecast_slots:
            if await request.is_disconnected():
                return Response(status_code=499)
            job = asyncio.create_task(asyncio.to_thread(forecast,
                target.model_dump(exclude_none=True), dict(observatory.astro.site), None, cancelled))
            try:
                while not job.done():
                    await asyncio.sleep(.1)
                    if await request.is_disconnected():
                        cancelled.set()
                        try:await job
                        except InterruptedError:pass
                        return Response(status_code=499)
                return await job
            finally:
                cancelled.set()

    async def moving_sample(payload):
        import astropy.units as u
        utc = observatory.astro.time()
        current = await observatory.preview({**payload, 'utc': utc.utc.isot})
        future = await observatory.preview({**payload, 'utc': (utc+1*u.s).utc.isot})
        return {**current,
                'azimuth_rate_deg_s': (future['azimuth_deg']-current['azimuth_deg']+180)%360-180,
                'altitude_rate_deg_s': future['altitude_deg']-current['altitude_deg']}

    @app.websocket('/ws/astronomy/moving')
    async def moving_stream(websocket: WebSocket):
        import time
        origin, host = websocket.headers.get('origin'), websocket.headers.get('host')
        if origin and origin not in (f'http://{host}', f'https://{host}'):
            await websocket.close(code=1008)
            return
        await websocket.accept()
        try:
            target = Target.model_validate(await asyncio.wait_for(websocket.receive_json(), timeout=5))
            payload = target.model_dump(exclude_none=True)
            previous = None
            while True:
                started = time.perf_counter()
                sample = await moving_sample(payload)
                finished = time.perf_counter()
                sample.update(target=payload, target_interval_ms=5,
                              calculation_ms=(finished-started)*1000,
                              sample_interval_ms=None if previous is None else (started-previous)*1000)
                previous = started
                await asyncio.wait_for(websocket.send_json(sample), timeout=2)
                # One outstanding sample: a slow/hidden browser cannot accumulate
                # old target positions or create an unbounded actor work queue.
                await asyncio.wait_for(websocket.receive_text(), timeout=2)
                await asyncio.sleep(max(0, .005-(time.perf_counter()-started)))
        except ValueError as exc:
            await websocket.send_json({'error':str(exc)})
            await websocket.close(code=1008)
        except (WebSocketDisconnect, asyncio.TimeoutError, RuntimeError):
            pass

    @app.post("/api/astronomy/tracking/start")
    async def tracking_start(target: Target):
        pointing_workbench.idle_required()
        return await observatory.start_tracking(target.model_dump(exclude_none=True))

    @app.post("/api/astronomy/tracking/stop")
    async def tracking_stop():
        await observatory.moving_demo.stop()
        await observatory.demo.stop()
        await pointing_workbench.stop(False)
        await observatory.stop_tracking()
        return observatory.status()

    @app.post("/api/pointing/load")
    async def model_load(model: ModelFile):
        pointing_workbench.idle_required()
        return pointing_workbench.launch('load model',pointing_workbench.load,model.path)

    @app.post("/api/pointing/fit")
    async def model_fit():
        pointing_workbench.idle_required()
        return pointing_workbench.launch('fit model',pointing_workbench.build,False)

    ntp_lock=asyncio.Lock()
    ntp_last_scan=0.
    ntp_verified={}

    @app.get('/api/pointing/ntp')
    async def ntp_settings():
        return {'ntp':pointing_workbench.ntp,'clock':clock_monitor.status}

    async def run_ntp_probe(server=None):
        import time
        from .ntp import discover, host_from_input
        nonlocal ntp_last_scan
        host=host_from_input(server) if server else None
        if ntp_lock.locked() or time.monotonic()-ntp_last_scan<5:
            raise HTTPException(429,'NTP measurement is busy; please wait a few seconds')
        async with ntp_lock:
            ntp_last_scan=time.monotonic()
            result=await discover(host)
            for item in result['results']:
                if item['ok']:ntp_verified[item['host']]=item
            return result

    @app.post('/api/pointing/ntp/find')
    async def find_ntp():
        return await run_ntp_probe()

    @app.post('/api/pointing/ntp/test')
    async def test_ntp(server:NtpServer):
        return await run_ntp_probe(server.server)

    @app.post('/api/pointing/ntp')
    async def save_ntp(server:NtpServer):
        from .ntp import host_from_input
        from datetime import datetime, timezone
        pointing_workbench.idle_required()
        host=host_from_input(server.server)
        if observatory.tracking or observatory.camera.busy:
            raise HTTPException(409,'Stop tracking and acquisition before synchronizing the server clock')
        result=ntp_verified.get(host)
        if not result or (datetime.now(timezone.utc)-datetime.fromisoformat(result['checked_utc'])).total_seconds()>300:
            raise HTTPException(400,'Test this server or use Find One before continuing')
        from .windows_clock import configure
        clock=await asyncio.to_thread(configure,host)
        pointing_workbench.ntp={**result,'windows_time_configured':True};pointing_workbench.save()
        await clock_monitor.check()
        return {'ntp':pointing_workbench.ntp,'clock':clock_monitor.status}

    from .interesting_ai_jobs import InterestingAIJobs
    interesting_ai_jobs=InterestingAIJobs()

    @app.get('/api/astronomy/interesting-ai/options')
    async def interesting_ai_options():
        from .satellite_catalog import CATEGORIES
        from .ai_credential import credential
        from .interesting_ai import SELECTOR_VERSION
        return {'satellite_catalogs':list(CATEGORIES),'configured':bool(credential()),'selector_version':SELECTOR_VERSION}

    @app.get('/api/astronomy/interesting-ai/status')
    async def interesting_ai_status():
        return JSONResponse(interesting_ai_jobs.snapshot(),headers={'Cache-Control':'no-store'})

    @app.post('/api/astronomy/interesting-ai/build')
    async def interesting_ai_build(settings:dict):
        from .interesting_ai import build
        return interesting_ai_jobs.start(build,pointing_workbench.config.path,dict(observatory.astro.site),settings)

    @app.get('/api/astronomy/interesting')
    async def interesting_objects():
        from .interesting_objects import list_objects
        return {'objects':await asyncio.to_thread(list_objects,pointing_workbench.config.path)}

    @app.post('/api/astronomy/interesting')
    async def save_interesting_object(row:dict):
        from .interesting_objects import save_object
        if not isinstance(row.get('interest_id'),str) or not row['interest_id'] or len(str(row))>65536:
            raise HTTPException(400,'Invalid interesting object')
        await asyncio.to_thread(save_object,pointing_workbench.config.path,row)
        return {'saved':True}

    @app.delete('/api/astronomy/interesting/{identifier:path}')
    async def delete_interesting_object(identifier:str):
        from .interesting_objects import delete_object
        await asyncio.to_thread(delete_object,pointing_workbench.config.path,identifier)
        return {'deleted':True}

    @app.get('/api/pointing/telescopes')
    async def telescopes():
        return {'telescopes':pointing_workbench.config.telescopes()}

    @app.post('/api/pointing/telescopes')
    async def create_telescope(specifications:TelescopeSpecification):
        pointing_workbench.idle_required()
        return {'telescope':pointing_workbench.config.save_telescope(specifications.model_dump())}

    @app.put('/api/pointing/telescopes/{identifier}')
    async def edit_telescope(identifier:str,specifications:TelescopeSpecification):
        pointing_workbench.idle_required()
        if not any(t['id']==identifier for t in pointing_workbench.config.telescopes()):raise HTTPException(404,'Telescope not found')
        return {'telescope':pointing_workbench.config.save_telescope(specifications.model_dump(),identifier)}

    @app.get('/api/pointing/location')
    async def pointing_location():
        return {'location':pointing_workbench.location or dict(observatory.astro.site)}

    @app.post('/api/pointing/review')
    async def review_pointing_file(model:ModelFile):
        pointing_workbench.idle_required()
        result=await observatory.call(observatory.pointing.inspect_file,model.path)
        pointing_workbench.candidate=result
        return result

    @app.post('/api/pointing/location')
    async def save_pointing_location(location:ModelLocation):
        pointing_workbench.idle_required()
        await observatory.call(observatory.set_site,location.model_dump())
        pointing_workbench.location=location.model_dump();pointing_workbench.save()
        return {'location':pointing_workbench.location}

    @app.get('/api/pointing/map-config')
    async def pointing_map_config():
        import os
        key=os.environ.get('NARIT_GOOGLE_MAPS_API_KEY','')
        return {'provider':'google' if key else os.environ.get('NARIT_POINTING_MAP_PROVIDER','esri'),'key':key}

    @app.get('/api/pointing/workbench')
    async def pointing_status():
        diagnostics=await observatory.call(observatory.pointing.diagnostics)
        return {**pointing_workbench.snapshot(),'model':observatory.model_status,'points':observatory.points,'diagnostics':diagnostics}

    @app.post('/api/pointing/settings')
    async def pointing_settings(settings:CalibrationSettings):
        return pointing_workbench.configure(settings.model_dump())

    @app.get('/api/pointing/blind-spots')
    async def get_blind_spots():
        return {'polygons':pointing_workbench.config.get('blind_spots',[])}

    @app.post('/api/pointing/blind-spots')
    async def save_blind_spots(payload:dict):
        from .blind_spots import validate
        pointing_workbench.idle_required()
        rows=validate(payload.get('polygons'))
        pointing_workbench.config.set('blind_spots',rows)
        return {'polygons':rows}

    @app.post('/api/pointing/image-session')
    async def pointing_image_session():
        return pointing_workbench.new_image_session()

    @app.post('/api/pointing/action')
    async def pointing_action(command:CalibrationAction):
        action=command.action
        if action=='new':return await pointing_workbench.new_session()
        if action=='grid':return pointing_workbench.create_grid()
        if action in ('start','resume'):return pointing_workbench.start()
        if action in ('pause','stop'):return await pointing_workbench.stop(action=='pause')
        if action=='reset':
            await pointing_workbench.stop(False)
            return pointing_workbench.reset()
        if action=='rerun':return pointing_workbench.rerun()
        if action=='add-current':return pointing_workbench.add_current()
        if action=='fit':
            pointing_workbench.idle_required()
            return pointing_workbench.launch('build pointing model',pointing_workbench.build,False)
        if action=='use':return await pointing_workbench.use_candidate()
        if action=='keep':
            pointing_workbench.idle_required();pointing_workbench.candidate=None;return pointing_workbench.snapshot()
        if action=='delete':return pointing_workbench.delete(command.identifiers)

    @app.post('/api/pointing/points/{identifier}')
    async def pointing_point(identifier:str,selection:PointSelection):
        return pointing_workbench.select(identifier,selection.included)

    @app.post('/api/pointing/analysis')
    async def pointing_analysis(settings:AnalysisRequest):
        from .pointing_analysis import run_analysis
        pointing_workbench.camera_ready(settings.kind=='pointing-stability')
        if settings.kind=='pointing-stability' and len([p for p in pointing_workbench.plan if p.get('included',True)])<settings.positions:raise ValueError('Create enough grid positions for the pointing stability test')
        return pointing_workbench.launch('pointing '+settings.kind,run_analysis,pointing_workbench,settings.kind,settings.frames,settings.interval_ms,settings.positions)

    @app.get('/api/pointing/analysis/download')
    async def pointing_analysis_export():
        return Response(orjson.dumps(pointing_workbench.analysis),media_type='application/json',headers={'Content-Disposition':'attachment; filename="pointing-analysis.json"'})

    @app.get('/api/pointing/model-folders')
    async def pointing_model_folders(path:str|None=None):
        directory=Path(path).expanduser().resolve() if path else observatory.directory/'models'
        if not path:directory.mkdir(parents=True,exist_ok=True)
        if not directory.is_dir():raise ValueError('Select an existing controller folder')
        folders=await asyncio.to_thread(lambda:sorted([{'name':p.name,'path':str(p)} for p in directory.iterdir() if p.is_dir()],key=lambda p:p['name'].lower()))
        return {'path':str(directory),'parent':str(directory.parent),'folders':folders}

    @app.post('/api/pointing/model-save')
    async def pointing_model_save(settings:ModelSave):
        pointing_workbench.idle_required()
        return pointing_workbench.launch('save pointing model',pointing_workbench.save_model,settings.directory,settings.name)

    @app.get('/api/pointing/candidate/download')
    async def pointing_candidate_download():
        if not pointing_workbench.candidate:raise HTTPException(404,'No candidate model')
        return FileResponse(pointing_workbench.candidate['path'],filename=Path(pointing_workbench.candidate['path']).name)

    @app.get('/api/mapped-hardware/{axis_id}/parameters')
    async def mapped_read(axis_id:str,group:str):return await mapped_hardware.read(axis_id,group)

    from .zero_position import ZeroPosition
    zero_position = ZeroPosition(mapped_hardware)

    @app.get('/api/axes/{axis_id}/zero-position')
    async def zero_preview(axis_id: str):
        pointing_workbench.idle_required()
        if observatory.status().get('tracking'):raise DriveError('Stop tracking before setting zero')
        return await zero_position.preview(axis_id)

    @app.post('/api/axes/{axis_id}/zero-position')
    async def zero_apply(axis_id: str, body: ZeroPositionConfirmation):
        pointing_workbench.idle_required()
        if observatory.status().get('tracking'):raise DriveError('Stop tracking before setting zero')
        result = await zero_position.apply(axis_id, body.model_dump())
        if axis_id in ('azimuth', 'altitude'):
            observatory.model_status = await observatory.call(observatory.pointing.unload)
        optics._status_cache.clear()
        mount.event('info', 'Zero position saved', f'{axis_id} · absolute encoder offset · Flash acknowledged')
        return result

    @app.post('/api/mapped-hardware/{axis_id}/parameter')
    async def mapped_write(axis_id:str,body:MappedParameterEdit):
        pointing_workbench.idle_required()
        if observatory.status().get('tracking'):raise DriveError('Stop tracking before editing hardware')
        async with topology.operation_lock:
            mapped_hardware.verify_binding(axis_id,body.target)
            return await mapped_hardware.write(axis_id,body.command,body.value,body.expected)

    @app.get('/api/mapped-hardware/{axis_id}/sensors/{socket}')
    async def mapped_sensor_read(axis_id:str,socket:int):return await mapped_hardware.sensor_read(axis_id,socket)

    @app.post('/api/mapped-hardware/{axis_id}/sensors/{socket}')
    async def mapped_sensor_write(axis_id:str,socket:int,body:MappedParameterEdit):
        pointing_workbench.idle_required()
        if observatory.status().get('tracking'):raise DriveError('Stop tracking before editing hardware')
        async with topology.operation_lock:
            mapped_hardware.verify_binding(axis_id,body.target)
            return await mapped_hardware.sensor_write(axis_id,socket,body.command,body.value,body.expected)

    @app.post('/api/mapped-hardware/{axis_id}/stop')
    async def mapped_stop(axis_id:str):
        await mapped_hardware.stop(axis_id)
        return {'stopped':True}

    @app.post('/api/mapped-hardware/drive/{number}/save')
    async def mapped_save(number:int):
        if number not in (1,2):raise DriveError('Unknown drive')
        pointing_workbench.idle_required()
        if observatory.status().get('tracking'):raise DriveError('Stop tracking before saving hardware')
        async with topology.operation_lock:
            result = await mapped_hardware.save_drive(number)
            optics._status_cache.clear()
            return result

    @app.get('/api/optics/status')
    async def optics_status():return await optics.status()

    @app.websocket('/ws/optics')
    async def optics_stream(websocket:WebSocket):
        import time
        origin,host=websocket.headers.get('origin'),websocket.headers.get('host')
        if origin and origin not in (f'http://{host}',f'https://{host}'):
            await websocket.close(code=1008);return
        await websocket.accept()
        previous=None
        try:
            while True:
                started=time.perf_counter()
                sample=await optics.status(fast=True)
                sample['interval_ms']=None if previous is None else (started-previous)*1000
                sample['read_ms']=(time.perf_counter()-started)*1000
                previous=started
                await asyncio.wait_for(websocket.send_text(orjson.dumps(sample).decode()),2)
                await asyncio.wait_for(websocket.receive_text(),2)
                await asyncio.sleep(max(0,.005-(time.perf_counter()-started)))
        except (WebSocketDisconnect,asyncio.TimeoutError):pass

    @app.post('/api/optics/{role}/settings')
    async def optics_settings(role:str,body:OpticsAxisSettings):
        pointing_workbench.idle_required()
        return await optics.configure_axis(role,**body.model_dump())

    @app.post('/api/optics/{role}/command')
    async def optics_command(role:str,body:OpticsCommand):
        if body.action not in ('stop','disable'):pointing_workbench.idle_required()
        return await optics.command(role,**body.model_dump())

    @app.get('/api/optics/m3/preset')
    async def optics_m3_read():return await optics.m3_settings()

    @app.post('/api/optics/m3/preset')
    async def optics_m3_preset(body:M3Preset):
        pointing_workbench.idle_required()
        return await optics.save_m3(body.port1,body.minimum,body.maximum,body.in_position_window,body.target,body.expected,body.jog_speed)

    @app.post('/api/optics/m3/port/{number}')
    async def optics_m3_port(number:int,body:OpticsCommand):
        pointing_workbench.idle_required()
        if number not in (1,2):raise HTTPException(404,'Unknown telescope port')
        angle=optics.settings['m3_port1']
        if angle is None:raise ValueError('Set the M3 Port 1 position first')
        preset=await optics.m3_settings()
        position=preset[f'port{number}']
        if position is None:raise ValueError('The opposite port is outside the configured position limits')
        return await optics.command('m3','goto',value=position,target=body.target)

    @app.get('/api/drives/topology')
    async def drive_topology():return topology.snapshot()

    @app.post('/api/drives/topology')
    async def drive_topology_save(mapping:TopologyMapping):
        pointing_workbench.idle_required()
        return topology.save(mapping.model_dump())

    @app.post('/api/drives/axis-mapping')
    async def drive_axis_mapping(mapping:dict[str,dict[str,int]]):
        pointing_workbench.idle_required()
        return topology.save_axis_mapping(mapping)

    @app.get('/api/drives/scan')
    async def drive_scan():
        async with topology.operation_lock:return await asyncio.to_thread(topology.scan)

    @app.post('/api/drives/connect')
    async def drive_group_connect(action:TopologyAction):
        pointing_workbench.idle_required()
        watchdog.manual_connect(action.group)
        return await topology.connect(action.group)

    @app.post('/api/drives/disconnect')
    async def drive_group_disconnect(action:TopologyAction):
        if action.group=='mount':
            await observatory.moving_demo.stop()
            await observatory.demo.stop()
        await pointing_workbench.stop(False)
        watchdog.manual_disconnect(action.group)
        if action.group=='mount':await observatory.stop_tracking()
        return await topology.disconnect(action.group)

    @app.get('/api/cctv/config')
    async def cctv_config():return cctv.config

    @app.post('/api/cctv/config')
    async def cctv_config_save(settings:CCTVConfig):return cctv.configure(settings.model_dump())

    @app.get('/api/cctv/scan')
    async def cctv_scan(subnet:str='192.168.70.0/24'):return await asyncio.to_thread(cctv.scan,subnet)

    @app.get('/api/cctv/snapshot')
    async def cctv_snapshot():
        return Response(await asyncio.to_thread(cctv.snapshot),media_type='image/jpeg',headers={'Cache-Control':'no-store'})

    @app.get("/api/pointing/download")
    async def model_download():
        path=observatory.model_status.get('path')
        if not path:raise HTTPException(404,'No fitted model')
        return FileResponse(path,filename=Path(path).name)

    def camera_idle():
        pointing_workbench.idle_required()
        if observatory.camera.busy or any(j['status']=='running' and j['kind'] in
                ('camera scan','camera connect','camera disconnect','capture','camera video') for j in observatory.jobs.values()):
            raise HTTPException(409,'Camera operation in progress; wait or abort the exposure')

    @app.get('/api/camera/status')
    async def camera_status():
        return observatory.camera.status()

    @app.get('/api/camera/cooling')
    async def camera_cooling_status():
        if observatory.camera.busy or any(j['status']=='running' and j['kind'] in ('camera scan','camera connect','camera disconnect','capture','camera video') for j in observatory.jobs.values()):
            cached = (observatory.camera.connected or {}).get('cooling', {})
            return {**cached, 'stale': True, 'state': 'Acquisition in progress · temperature updates paused'}
        try:
            result=await observatory.camera_call(observatory.camera.cooling_status)
            device=observatory.camera.connected or {}
            saved=await asyncio.to_thread(pointing_workbench.config.get,'camera.cooling.'+device.get('key',''))
            return {**result,'saved_target_c':saved.get('target_c') if saved else None}
        except ValueError as exc:
            raise HTTPException(409, str(exc)) from exc

    @app.post('/api/camera/cooling')
    async def camera_cooling_control(settings: CameraCooling):
        camera_idle()
        if not observatory.camera.connected or observatory.camera.connected.get('sdk') != 'sdk2':
            raise HTTPException(409, 'Connect an Andor CCD to control cooling')
        try:
            result = await observatory.camera_call(observatory.camera.cooling_status, settings.enabled, settings.target_c)
            if settings.target_c is not None:
                await asyncio.to_thread(pointing_workbench.config.set,'camera.cooling.'+observatory.camera.connected['key'],{'target_c':settings.target_c})
            saved=await asyncio.to_thread(pointing_workbench.config.get,'camera.cooling.'+observatory.camera.connected['key'])
            result['saved_target_c']=saved.get('target_c') if saved else None
            await asyncio.to_thread(audit.record, 'camera.cooling', settings.model_dump(exclude_none=True))
            return result
        except ValueError as exc:
            raise HTTPException(422, str(exc)) from exc

    @app.get('/api/camera/jobs/{identifier}')
    async def camera_job(identifier: str):
        job=observatory.jobs.get(identifier)
        if not job:raise HTTPException(404,'Camera job not found')
        return {k:v for k,v in job.items() if not k.startswith('_')}

    @app.get('/api/pointing/imaging')
    async def imaging_setup():
        return {'imaging':pointing_workbench.config.get('imaging')}

    @app.post('/api/pointing/imaging')
    async def save_imaging(settings: ImagingSetup):
        camera_idle()
        telescope=next((t for t in pointing_workbench.config.telescopes() if t['id']==settings.telescope_id),None)
        if not telescope:raise HTTPException(404,'Telescope not found')
        device=observatory.camera.connected
        if not device or device['key']!=settings.device_key:
            raise HTTPException(409,'Connect the selected camera before saving imaging equipment')
        observatory.camera.validate_settings(settings.exposure_s,settings.binning,settings.gain)
        scale=206.264806247*settings.pixel_um*settings.binning/(telescope['focal_length_mm']*telescope.get('optical_multiplier',1))
        if not .01 <= scale <= 120:
            raise HTTPException(422,'Plate scale must be between 0.01 and 120 arcsec/pixel for calibration')
        profile={**settings.model_dump(),'vendor':device['vendor'],'model':device['name'],
                 'serial':device.get('serial'),'width':device['width'],'height':device['height'],
                 'pixel_scale':scale}
        pointing_workbench.configure({'exposure_s':settings.exposure_s,'binning':settings.binning,
                                     'gain':settings.gain,'pixel_scale':scale/settings.binning})
        pointing_workbench.config.set('imaging',profile)
        mount.event('info','Imaging equipment saved',settings.name+' · '+device['name'])
        return {'imaging':profile}

    @app.post("/api/camera/scan")
    async def camera_scan():
        camera_idle()
        return observatory.new_job('camera scan',observatory.camera_call,observatory.camera.scan)

    @app.post("/api/camera/connect")
    async def camera_connect(model: ModelFile):
        camera_idle()
        return observatory.new_job('camera connect',observatory.camera_call,observatory.camera.connect,model.path)

    @app.post("/api/camera/disconnect")
    async def camera_disconnect():
        pointing_workbench.idle_required()
        if any(j['status']=='running' and j['kind'] in ('camera scan','camera connect','camera disconnect') for j in observatory.jobs.values()):
            raise HTTPException(409,'Camera connection operation in progress')
        observatory.camera.abort()
        return observatory.new_job('camera disconnect',observatory.camera_call,observatory.camera.disconnect)

    @app.post("/api/camera/capture")
    async def camera_capture(exposure: CameraExposure):
        camera_idle()
        observatory.camera.validate_settings(exposure.exposure_s,exposure.binning,exposure.gain)
        observatory.camera.validate_roi(exposure.roi.model_dump() if exposure.roi else None,exposure.binning)
        epoch=observatory.camera.prepare_capture()
        return observatory.new_job('capture',observatory.capture,exposure.exposure_s,exposure.binning,exposure.gain,exposure.dark,epoch,exposure.telescope_id,exposure.roi.model_dump() if exposure.roi else None,exposure.frame_type,exposure.readout)

    @app.post('/api/camera/video/start')
    async def camera_video_start(settings:VideoExposure):
        camera_idle()
        observatory.camera.validate_settings(settings.exposure_s,settings.binning,settings.gain)
        if not observatory.camera.connected.get('video_supported'):raise ValueError('Connected camera does not support video')
        epoch=observatory.camera.prepare_capture()
        return observatory.new_job('camera video',observatory.camera_call,observatory.camera.video_capture,settings.exposure_s,settings.binning,settings.gain,settings.roi.model_dump() if settings.roi else None,settings.frames,epoch,settings.record,settings.image_format,settings.preview_fps,settings.video_controls,settings.auto_exposure,settings.auto_gain)

    @app.get('/api/camera/video/bandwidth-probe')
    async def video_bandwidth_probe():
        import os
        return Response(os.urandom(65536),media_type='application/octet-stream',headers={'Cache-Control':'no-store'})

    @app.websocket('/ws/camera/video')
    async def camera_video_stream(websocket:WebSocket):
        # One acknowledged frame at a time gives telemetry room on a slow link.
        origin=websocket.headers.get('origin');host=websocket.headers.get('host')
        if origin and origin not in (f'http://{host}',f'https://{host}'):
            await websocket.close(code=1008);return
        await websocket.accept()
        outstanding=[];last_sent=-1;delivery_fps=3;next_frame=0;credit_limit=1;byte_budget=8192;sent_at=0;sent_size=0;loop_tick=asyncio.get_running_loop().time()
        async def acknowledgements():
            nonlocal delivery_fps,credit_limit,byte_budget
            while True:
                value=await websocket.receive_text()
                if value.startswith("fps:"):
                    try:
                        delivery_fps=max(.1,min(3,float(value[4:])));credit_limit=1
                    except ValueError:pass
                    continue
                if value.startswith("budget:"):
                    try:byte_budget=max(1024,min(100_000_000,float(value[7:])))
                    except ValueError:pass
                    continue
                try:sequence=int(value)
                except ValueError:continue
                if 0<=sequence<=last_sent:
                    if byte_budget and sequence==last_sent and sent_at:
                        elapsed=asyncio.get_running_loop().time()-sent_at
                        if elapsed>.25:byte_budget=max(1024,min(byte_budget,sent_size/elapsed*.5))
                    outstanding[:]=[number for number in outstanding if number>sequence]
        receiver=asyncio.create_task(acknowledgements())
        try:
            while not receiver.done():
                now=asyncio.get_running_loop().time()
                if now-loop_tick>.15:
                    delivery_fps=max(.1,delivery_fps*.5);byte_budget=max(1024,byte_budget*.5)
                loop_tick=now
                # Video yields before drive samples approach the one-second safety timeout.
                if mount.transport.connected and mount.readings and any(time.perf_counter()-r.monotonic_at>.25 for r in mount.readings.values()):
                    await asyncio.sleep(.05);continue
                packet=observatory.camera.video_packet
                if packet and packet[0]>last_sent and len(outstanding)<credit_limit and asyncio.get_running_loop().time()>=next_frame:
                    sequence,data=packet
                    next_frame=asyncio.get_running_loop().time()+max(1/delivery_fps,len(data)/byte_budget if byte_budget else 0)
                    # Prefix is a uint32 sequence; JPEG bytes retain the existing preview encoding.
                    outstanding.append(sequence);last_sent=sequence;sent_at=asyncio.get_running_loop().time();sent_size=len(data)
                    await asyncio.wait_for(websocket.send_bytes(sequence.to_bytes(4,'big')+data),timeout=3)
                await asyncio.sleep(.005)
        except (WebSocketDisconnect,RuntimeError,asyncio.TimeoutError):
            pass
        finally:
            receiver.cancel()
            await asyncio.gather(receiver,return_exceptions=True)

    @app.get('/api/camera/video/frame')
    async def camera_video_frame():
        packet=observatory.camera.video_packet
        sequence,frame=packet if packet else (0,None)
        return Response(frame or b'',status_code=200 if frame else 204,media_type='image/jpeg',headers={'Cache-Control':'no-store','X-Frame-Sequence':str(sequence)})

    @app.get('/api/camera/video/file')
    async def camera_video_file():
        result=observatory.camera.video_status or {}
        if not result.get('fits'):raise HTTPException(404,'No recorded video cube')
        return FileResponse(result['fits'],filename=result['filename'])

    @app.post("/api/camera/abort")
    async def camera_abort():
        await pointing_workbench.stop(False)
        observatory.camera.abort()
        return {"ok":True}

    @app.get("/api/camera/preview/{filename}")
    async def camera_preview(filename: str):
        if Path(filename).name!=filename or not filename.endswith('.png'):
            raise HTTPException(404,'Preview not found')
        path=observatory.directory/'captures'/filename
        record=observatory.image_archive.get(Path(filename).with_suffix('.fits').name)
        if record:
            content,_,_,_=await asyncio.to_thread(observatory.image_archive.source,record['filename'],True)
            return Response(content,media_type='image/png',headers={'Cache-Control':'private, no-cache, must-revalidate'})
        if not path.exists():raise HTTPException(404,'Preview not found')
        return FileResponse(path)

    async def archived_image(filename):
        try:
            last=observatory.camera.last
            metadata=last if last and last.get('filename')==filename else None
            return await asyncio.to_thread(observatory.image_archive.ensure,filename,metadata)
        except (FileNotFoundError,ValueError):raise HTTPException(404,'Image not found')

    @app.get('/api/camera/series/latest')
    async def camera_latest_series():
        return await asyncio.to_thread(observatory.image_archive.current_series)

    @app.post('/api/camera/series/new')
    async def camera_new_series(settings:CameraSeriesName):
        camera_idle()
        from .image_archive import SeriesExistsError
        try:return await asyncio.to_thread(observatory.image_archive.new_series,settings.name)
        except SeriesExistsError as exc:raise HTTPException(409,str(exc)) from exc

    @app.get('/api/camera/images/{filename}')
    async def camera_image_metadata(filename:str):
        return await archived_image(filename)

    @app.get('/api/camera/latest-image')
    async def camera_latest_image():
        return {'image':await asyncio.to_thread(observatory.image_archive.latest)}

    @app.post('/api/camera/images/{filename}/stars')
    async def camera_image_stars(filename:str):
        record=await archived_image(filename)
        metadata=record['metadata']
        existing=metadata.get('solution') or metadata.get('preview_solution') or {}
        if existing.get('success') and metadata.get('matched_star_positions'):return record
        if existing.get('success') and existing.get('matched_star_positions'):
            values={key:existing[key] for key in ('star_positions','matched_star_positions','detected_stars','star_source') if key in existing}
        else:
            import subprocess,sys,json
            headers={c['key']:c['value'] for c in metadata.get('fits_header',[])}
            ra=existing.get('ra_hours',headers.get('SIMRA'))
            dec=existing.get('dec_deg',headers.get('SIMDEC'))
            if ra is None or dec is None:
                encoder=metadata.get('encoder_before') or {}
                if encoder.get('azimuth') is None or encoder.get('altitude') is None:raise HTTPException(409,'Image has no saved sky pointing for PWLib analysis')
                sky=await observatory.call(observatory.astro.from_encoder,encoder['azimuth'],encoder['altitude'],metadata.get('capture_utc'))
                ra,dec=sky['ra_hours'],sky['dec_deg']
            scale=existing.get('pixel_scale',headers.get('PIXSCALE'))
            if not scale:raise HTTPException(409,'Image has no saved plate scale for PWLib analysis')
            def inspect():
                payload={'fits':metadata['fits'],'ra_hours':ra,'dec_deg':dec,'pixel_scale':scale,'inspect_only':True}
                result=subprocess.run([sys.executable,'-m','mount_service.solver_worker'],input=json.dumps(payload),capture_output=True,text=True,timeout=55)
                if result.returncode:raise ValueError('PWLib star analysis failed: '+result.stderr[-300:])
                return json.loads(result.stdout.strip().splitlines()[-1])
            try:values=await asyncio.to_thread(inspect)
            except (ValueError,subprocess.TimeoutExpired) as exc:raise HTTPException(409,str(exc))
            if not values.get('success') or not values.get('matched_star_positions'):
                raise HTTPException(409,values.get('error') or 'Plate solve did not produce catalog-matched stars; no circles displayed')
            values={**values,'preview_solution':dict(values)}
        await asyncio.to_thread(observatory.image_archive.update_metadata,filename,values)
        return observatory.image_archive.get(filename)

    @app.post('/api/camera/images/{filename}/stretch')
    async def camera_image_stretch(filename:str,settings:ImageStretch):
        try:observatory.image_archive.validate(filename)
        except ValueError:raise HTTPException(404,'Image not found')
        if not observatory.image_archive.get(filename):raise HTTPException(404,'Image not found')
        return await asyncio.to_thread(observatory.image_archive.save_stretch,filename,settings.model_dump())

    @app.get('/api/pointing/images')
    async def calibration_images():
        def load():
            archive=observatory.image_archive
            changed=False
            for point in list(observatory.points):
                if not point.get('fits'):continue
                filename=Path(point['fits']).name
                stored=archive.get(filename)
                if stored and point.get('total_error_arcsec') is None and point.get('simulated'):
                    headers={c['key']:c['value'] for c in stored['metadata'].get('fits_header',[])}
                    if 'SIMRA' in headers and 'SIMDEC' in headers:
                        from .pointing import pointing_error
                        point.update(expected_ra_hours=headers['SIMRA'],expected_dec_deg=headers['SIMDEC'])
                        point.update(pointing_error(headers['SIMRA'],headers['SIMDEC'],point['solved_ra_hours'],point['solved_dec_deg']))
                        changed=True
                if archive.get(filename) or Path(point['fits']).is_file():
                    archive.ensure(filename,{'point':point,'solution':point.get('solution'),'calibration_status':'solved'},point['fits'])
            if changed:observatory._save_points()
            for p in list(pointing_workbench.plan):
                filename=p.get('capture_filename')
                if filename and archive.get(filename):
                    archive.update_metadata(filename,{'calibration_status':p['status'],'error':p.get('error')})
            return {'images':archive.calibration_images(pointing_workbench.image_session_started_utc)}
        return await asyncio.to_thread(load)

    @app.get('/api/camera/images/{filename}/display-levels')
    async def camera_display_levels(filename:str):
        await archived_image(filename)
        levels=await asyncio.to_thread(observatory.image_archive.display_levels,filename)
        return Response(orjson.dumps(levels),media_type='application/json',headers={'Cache-Control':'private, max-age=3600'})

    preview_slots=asyncio.Semaphore(1)

    @app.get('/api/camera/images/{filename}/display-data.png')
    async def camera_display_data(filename:str,maximum:int=1280):
        if maximum not in (1280,2560):raise HTTPException(422,'Invalid preview size')
        await archived_image(filename)
        async with preview_slots:
            content=await asyncio.to_thread(observatory.image_archive.display_data,filename,maximum)
        return Response(content,media_type='image/png',headers={'Cache-Control':'private, max-age=3600'})

    @app.get('/api/camera/images/{filename}/display.webp')
    async def camera_display_preview(filename:str,request:Request,maximum:int=640,black:float|None=None,white:float|None=None,tone:float|None=None):
        import math
        if maximum not in (640,1280,2560):raise HTTPException(422,'Invalid preview size')
        if any(v is not None and (not math.isfinite(v) or v<0 or v>65535) for v in (black,white)) or tone is not None and (not math.isfinite(tone) or not 0<tone<=30):
            raise HTTPException(422,'Invalid stretch settings')
        if black is not None and white is not None and white<=black:raise HTTPException(422,'White point must exceed black point')
        await archived_image(filename)
        async with preview_slots:
            if await request.is_disconnected():return Response(status_code=499)
            content,width,height=await asyncio.to_thread(observatory.image_archive.display_webp,filename,maximum,black,white,tone)
        return Response(content,media_type='image/webp',headers={'X-Preview-Width':str(width),'X-Preview-Height':str(height),'Cache-Control':'private, no-cache'})

    @app.get('/api/camera/stretch-source/{filename}')
    async def camera_stretch_source(filename:str):
        if Path(filename).name!=filename or not filename.endswith('.fits'):
            raise HTTPException(404,'Preview source not found')
        await archived_image(filename)
        content,width,height,settings=await asyncio.to_thread(observatory.image_archive.source,filename)
        return Response(content,media_type='application/octet-stream',headers={
            'X-Preview-Width':str(width),'X-Preview-Height':str(height),
            'X-Stretch-Black':str(settings['black']),'X-Stretch-White':str(settings['white']),
            'Cache-Control':'private, max-age=3600'})

    @app.get('/api/camera/images/{filename}/fits')
    async def image_fits_download(filename:str):
        await archived_image(filename)
        path=observatory.directory/'captures'/filename
        if not path.is_file():raise HTTPException(404,'FITS file unavailable')
        return FileResponse(path,filename=filename)

    @app.get("/api/camera/fits")
    async def camera_fits():
        if not observatory.camera.last:raise HTTPException(404,'No exposure')
        return FileResponse(observatory.camera.last['fits'],filename=observatory.camera.last['filename'])

    @app.post("/api/calibration/solve")
    async def plate_solve(target: Solve):
        pointing_workbench.idle_required()
        return observatory.new_job('plate solve',observatory.solve,target.ra_hours,target.dec_deg,target.pixel_scale)

    @app.get("/api/calibration/points")
    async def calibration_points():
        return {"points":observatory.points}

    @app.post("/api/calibration/points/{identifier}")
    async def select_point(identifier: str, selection: PointSelection):
        point=next((p for p in observatory.points if p['id']==identifier),None)
        if point is None:raise HTTPException(404,'Calibration point not found')
        pointing_workbench.idle_required()
        point['included']=selection.included
        observatory._save_points()
        return {"ok":True}

    @app.get("/api/calibration/points/download")
    async def calibration_download():
        return Response(orjson.dumps({"frame":"solved J2000","points":observatory.points}),media_type='application/json',
                        headers={'Content-Disposition':'attachment; filename="calibration-points.json"'})

    @app.websocket("/ws/telemetry")
    async def telemetry(websocket: WebSocket):
        origin = websocket.headers.get("origin")
        host = websocket.headers.get("host")
        if origin and origin not in (f"http://{host}", f"https://{host}"):
            await websocket.close(code=1008)
            return
        await websocket.accept()
        cursor = mount.sequence
        generation = mount.generation
        try:
            while True:
                if cursor == mount.sequence and generation == mount.generation:
                    await asyncio.sleep(max(1 / 60, getattr(mount.transport, "interval_ms", 50) / 1000))
                    continue
                snapshot = mount.snapshot()
                snapshot["observatory"] = observatory.status()
                snapshot["drive_watchdog"] = watchdog.states
                if generation != mount.generation:
                    cursor = -1
                    generation = mount.generation
                pending = []
                for sample in reversed(mount.history):
                    if sample["sequence"] <= cursor:
                        break
                    pending.append(sample)
                snapshot["samples"] = pending[::-1]
                cursor = snapshot["sequence"]
                await asyncio.wait_for(websocket.send_text(orjson.dumps(snapshot).decode("utf-8")), timeout=2)
                await asyncio.sleep(max(1 / 60, getattr(mount.transport, "interval_ms", 50) / 1000))
        except (WebSocketDisconnect, asyncio.TimeoutError, RuntimeError):
            pass

    app.mount("/assets", StaticFiles(directory=web), name="assets")
    return app


app = create_app()
