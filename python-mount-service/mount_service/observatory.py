import asyncio
import json
import math
import time
import subprocess
import sys
import uuid
import astropy.units as u
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

from .astronomy import Astronomy, ROOT
from .camera import CameraService
from .pointing import PointingModel
from .domain import AXIS_MAP, DriveError
from .transports import SerialElmoDrive
from .tracking import TrackingServo, tracking_velocity_status
from .setpoint_generator import SetPointGenerator


class Observatory:
    def __init__(self, mount):
        self.audit=None
        self.mount=mount
        self.astro=Astronomy()
        self.set_point_generator=SetPointGenerator(self.astro.time,self.preview)
        self.pointing=PointingModel(self.astro)
        self.camera=CameraService()
        self.actor=ThreadPoolExecutor(max_workers=1,thread_name_prefix='astronomy-pointing')
        self.camera_actor=ThreadPoolExecutor(max_workers=1,thread_name_prefix='camera')
        self.jobs={}
        self.points=[]
        self.target=None
        self.tracking=False
        from .sky_demo import SkyDemo
        self.demo = SkyDemo(self)
        from .moving_demo import MovingDemo
        self.moving_demo = MovingDemo(self)
        from collections import deque
        self.position_samples = deque(maxlen=10)
        self.position_rms = {}
        self.track_task=None
        self.sky_task=None
        self.model_status=self.pointing.status()
        self.sky={"status":"initializing","site":self.astro.site}
        self.catalog_error=None
        self.directory=ROOT/'python-mount-service/runtime'
        self.directory.mkdir(parents=True,exist_ok=True)
        self.tracking_settings_path=self.directory/'tracking-display-settings.json'
        self.tracking_tolerance=1.0
        if self.tracking_settings_path.exists():
            try:
                value=float(json.loads(self.tracking_settings_path.read_text(encoding='utf-8'))['tolerance_arcsec_s'])
                if math.isfinite(value) and value>0:self.tracking_tolerance=value
            except (ValueError,KeyError,TypeError):pass
        data=self.directory/'calibration-points.json'
        if data.exists():self.points=json.loads(data.read_text())

    async def call(self, function, *args):
        return await asyncio.get_running_loop().run_in_executor(self.actor,function,*args)

    def set_site(self, location):
        if self.tracking:
            raise ValueError('Stop tracking before changing observer location')
        with self.pointing.lock:
            self.astro.set_site(location['latitude'],location['longitude'],location['elevation'])
            if self.pointing.model is not None:
                self.pointing.model.Latitude=location['latitude']
                self.pointing.model.Longitude=location['longitude']

    async def camera_call(self,function,*args):
        return await asyncio.get_running_loop().run_in_executor(self.camera_actor,function,*args)

    async def start(self):
        self.sky_task=asyncio.create_task(self._sky_loop())
        if not self.mount.allow_simulation:self.new_job('initialize',self._initialize)

    async def _initialize(self):
        try:
            await self.call(self.pointing.initialize)
            self.model_status=self.pointing.status()
            saved=self.directory/'active-model.json'
            if saved.exists():self.model_status=await self.call(self.pointing.load,json.loads(saved.read_text())['path'])
        except Exception as exc:
            self.model_status={"available":False,"active":False,"error":str(exc)}
        return await self.camera_call(self.camera.scan)

    async def close(self):
        await self.moving_demo.stop()
        await self.demo.stop()
        await self.stop_tracking()
        self.camera.abort()
        if self.sky_task:self.sky_task.cancel()
        for job in self.jobs.values():
            task=job.get('_task')
            if task and not task.done():task.cancel()
        await self.camera_call(self.camera.close)
        self.actor.shutdown(wait=False,cancel_futures=True)
        self.camera_actor.shutdown(wait=False,cancel_futures=True)

    def set_tracking_tolerance(self,value):
        if not math.isfinite(value) or value<=0:raise ValueError('Tracking tolerance must be positive and finite')
        temporary=self.tracking_settings_path.with_suffix('.tmp')
        temporary.write_text(json.dumps({'tolerance_arcsec_s':value}),encoding='utf-8')
        temporary.replace(self.tracking_settings_path)
        self.tracking_tolerance=value
        return {'tolerance_arcsec_s':value}

    def status(self):
        return {"moving_demo":self.moving_demo.snapshot(),"position_rms":self.position_rms if self.tracking else {},"tracking_quality":tracking_velocity_status(self.tracking,self.target,self.mount.snapshot()["axes"],self.tracking_tolerance),"sky":self.sky,"target":self.target,"tracking":self.tracking,"demo":self.demo.snapshot(),"pointing":self.model_status,
                "camera":self.camera.status(),"calibration_points":len(self.points),"pointing_workbench":{"state":self.pointing_workbench.state,"busy":self.pointing_workbench.busy} if hasattr(self,"pointing_workbench") else None,
                "jobs":[{k:v for k,v in j.items() if not k.startswith('_')} for j in list(self.jobs.values())[-10:]]}

    async def _sky_loop(self):
        while True:
            try:
                state=self.mount.snapshot()
                axes={a['id']:a for a in state['axes']}
                if axes['azimuth']['valid'] and axes['altitude']['valid']:
                    az,alt=await self.call(self.pointing.corrected,axes['azimuth']['position_deg'],axes['altitude']['position_deg'])
                    coordinate=await self.call(self.astro.from_encoder,az,alt)
                    self.sky={**coordinate,"site":self.astro.site,"azimuth_deg":az,"altitude_deg":alt,
                              "status":"model corrected" if self.model_status.get('active') else "encoder geometry; no pointing model"}
                else:self.sky={"status":"telemetry unavailable","site":self.astro.site}
            except Exception as exc:self.sky={"status":str(exc),"site":self.astro.site}
            await asyncio.sleep(1)

    def new_job(self,kind,function,*args):
        identifier=uuid.uuid4().hex
        job={"id":identifier,"kind":kind,"status":"running","result":None,"error":None}
        async def run():
            if self.audit:await asyncio.to_thread(self.audit.record,'job.started',{'job_id':identifier,'kind':kind})
            try:
                job['result']=await function(*args);job['status']='complete'
            except asyncio.CancelledError:
                job['status']='cancelled'
            except Exception as exc:
                job['error']=str(exc);job['status']='failed'
                self.mount.event('error',f'{kind} failed',str(exc))
            finally:
                if self.audit:await asyncio.to_thread(self.audit.record,'job.'+job['status'],{k:v for k,v in job.items() if not k.startswith('_')})
        self.jobs[identifier]=job
        job['_task']=asyncio.create_task(run())
        # Keep a bounded recent job list; never remove running jobs.
        for key in list(self.jobs)[:-30]:
            if self.jobs[key]['status']!='running':self.jobs.pop(key)
        return {k:v for k,v in job.items() if not k.startswith('_')}

    async def motion_limits(self, refresh=False):
        limits = {}
        for name in ('azimuth', 'altitude'):
            axis = AXIS_MAP[name]
            if isinstance(self.mount.transport, SerialElmoDrive):
                limits[name] = (await self.mount.transport.refresh_position_limits(axis) if refresh
                                else self.mount.transport.travel_limits(axis))
            else:
                limits[name] = (axis.minimum_deg, axis.maximum_deg)
        return limits

    async def preview(self,payload):
        target=await self.call(lambda:self.astro.target(**payload))
        az,alt=await self.call(self.pointing.encoder_target,target)
        state=self.mount.snapshot()
        current=state['axes'][0]['position_deg']
        limits=await self.motion_limits()
        lower,upper=limits['azimuth']
        candidates=[az+360*k for k in range(math.ceil((lower-az)/360),math.floor((upper-az)/360)+1)]
        if not candidates:raise ValueError(f'No equivalent azimuth fits drive limits: {lower:g} to {upper:g} degrees')
        az=min(candidates,key=lambda x:abs(x-current)) if current is not None else candidates[0]
        return {**target,'encoder_azimuth_deg':az,'encoder_altitude_deg':alt,
                'model_applied':self.model_status.get('active',False),
                'slew_allowed':bool(target.get('above_horizon', alt>0) and limits['altitude'][0]<=alt<=limits['altitude'][1]),
                'drive_limits':limits}

    async def goto(self,payload):
        await self.stop_tracking()
        await self.motion_limits(refresh=True)
        target=await self.preview(payload)
        if not target['slew_allowed']:raise ValueError('Target is below the horizon or outside drive altitude limits')
        state=self.mount.snapshot()
        for axis in state['axes'][:2]:
            if not axis['valid'] or not axis['enabled']:raise ValueError('Enable Azimuth and Altitude with fresh telemetry first')
        try:
            await self.mount.goto_pair(target['encoder_azimuth_deg'],target['encoder_altitude_deg'])
        except Exception:
            await self.mount.stop_all()
            raise
        self.target={**payload,'preview':target}
        return target

    async def tracking_rates(self, payload):
        return await self.set_point_generator.generate(payload)

    async def send_tracking_rates(self, rates):
        axes = {a['id']:a for a in self.mount.snapshot()['axes']}
        limits = await self.motion_limits()
        # Validate both axes before either velocity is issued.
        for name, rate in rates.items():
            axis = axes[name]
            if not axis['valid'] or not axis['enabled']:
                raise ValueError('Tracking requires Azimuth and Altitude enabled with fresh telemetry')
            lower, upper = limits[name]
            if (axis['position_deg']<=lower and rate<0) or (axis['position_deg']>=upper and rate>0):
                raise ValueError(f'{name.title()} tracking would exceed drive travel limits')
        try:
            for name, rate in rates.items():
                await self.mount.command(name, 'jog', rate, lease=.8)
        except Exception:
            await self.mount.stop_all()
            raise

    async def tracking_velocity_limits(self):
        limits={}
        for name in ('azimuth','altitude'):
            axis=AXIS_MAP[name]
            if isinstance(self.mount.transport,SerialElmoDrive):
                drive=self.mount.transport
                native=await drive._job(lambda axis=axis:drive._numeric(f'AX{axis.number}.VH[2]'))
                maximum=native/abs(drive.velocity_units(axis,1))
            else:maximum=axis.max_velocity
            if not math.isfinite(maximum) or maximum<=0:raise ValueError(f'{name}: configure a positive drive velocity limit')
            limits[name]=min(maximum,axis.max_velocity)
        return limits

    def tracking_commands(self,target,rates,dt):
        axes={a['id']:a for a in self.mount.snapshot()['axes']}
        commands={}
        errors=[]
        for name,key in [('azimuth','encoder_azimuth_deg'),('altitude','encoder_altitude_deg')]:
            axis=axes[name]
            if not axis['valid'] or not axis['enabled']:raise ValueError('Tracking requires both axes enabled with fresh telemetry')
            errors.append((target[key]-axis['position_deg'])*3600)
            servo=self.track_servos[name]
            if abs(rates[name])>servo.maximum_velocity:raise ValueError(f'{name}: object rate exceeds drive velocity limit')
            commands[name]=servo.velocity(target[key],axis['position_deg'],rates[name],axis['velocity_deg_s'],dt)
        self.position_samples.append(errors)
        self.position_rms = {'samples':len(self.position_samples), **{key:math.sqrt(sum(row[i]**2 for row in self.position_samples)/len(self.position_samples)) for i,key in enumerate(('azimuth_arcsec','altitude_arcsec'))}}
        return commands

    async def start_tracking(self,payload):
        if self.tracking:raise ValueError('Tracking already active')
        self.position_samples.clear()
        self.position_rms = {}
        await self.motion_limits(refresh=True)
        velocity_limits=await self.tracking_velocity_limits()
        self.track_servos={name:TrackingServo(limit) for name,limit in velocity_limits.items()}
        target, rates = await self.tracking_rates(payload)
        commands=self.tracking_commands(target,rates,0)
        await self.send_tracking_rates(commands)
        self.target={**payload,'preview':target,'rates_deg_s':rates,'command_rates_deg_s':commands}
        self.tracking=True
        self.track_task=asyncio.create_task(self._track_loop(payload))
        return self.status()

    async def _track_loop(self,payload):
        previous=time.monotonic()
        next_update=previous
        try:
            while self.tracking:
                target, rates = await self.tracking_rates(payload)
                now=time.monotonic()
                commands=self.tracking_commands(target,rates,now-previous)
                previous=now
                await self.send_tracking_rates(commands)
                self.target={**payload,'preview':target,'rates_deg_s':rates,'command_rates_deg_s':commands}
                next_update+=self.set_point_generator.interval_s
                remaining=next_update-time.monotonic()
                if remaining<0:next_update=time.monotonic()
                await asyncio.sleep(max(0,remaining))
        except asyncio.CancelledError:pass
        except Exception as exc:self.mount.event('error','Tracking stopped',str(exc))
        finally:
            self.tracking=False
            try:await self.mount.stop_all()
            except Exception as exc:self.mount.event('error','Tracking stop acknowledgement failed',str(exc))

    async def stop_tracking(self):
        if self.track_task and not self.track_task.done():
            self.tracking=False
            self.track_task.cancel()
            try:
                await self.track_task
            except asyncio.CancelledError:
                # A task cancelled before its first step never runs its finally block.
                await self.mount.stop_all()
        self.track_task=None

    async def capture(self,exposure,binning,gain,dark,epoch=None,telescope_id=None,roi=None,frame_type=None,readout=None,simulation_purpose='imaging'):
        if epoch is None:epoch=self.camera.prepare_capture()
        dark=dark or frame_type in ("Dark","Bias")
        before=self.mount.snapshot()
        metadata={}
        if all(a['valid'] for a in before['axes'][:2]):
            metadata={'AZENC':before['axes'][0]['position_deg'],'ALTENC':before['axes'][1]['position_deg']}
        device=self.camera.connected
        telescope=None
        profile=None
        if hasattr(self,'pointing_workbench'):
            profile=self.pointing_workbench.config.get('imaging')
            if not telescope_id and profile and device and profile['device_key']==device['key']:
                telescope_id=profile['telescope_id']
            if telescope_id:
                telescope=next((t for t in self.pointing_workbench.config.telescopes() if t['id']==telescope_id),None)
                if not telescope:raise ValueError('Selected telescope not found')
            if telescope:
                focal=telescope['focal_length_mm']*telescope.get('optical_multiplier',1)
                pixel=device['pixel_um'] if device and device.get('simulated') else profile['pixel_um'] if profile else device.get('pixel_um')
                if pixel:
                    metadata.update(TELESCOP=telescope['name'],FOCALLEN=focal,XPIXSZ=pixel,
                                    PIXSCALE=206.264806247*pixel*binning/focal)
        if device and device.get('simulated') and not dark:
            if not all(a['valid'] for a in before['axes'][:2]):
                raise ValueError('Catalog simulation needs fresh mount encoders')
            if not telescope:raise ValueError('Select a telescope before capturing a catalog simulation')
            started=time.time()
            from datetime import datetime,timezone
            midpoint=datetime.fromtimestamp(started+exposure/2,timezone.utc).isoformat().replace("+00:00","Z")
            center=self.astro.from_encoder(before['axes'][0]['position_deg'],before['axes'][1]['position_deg'],midpoint)
            direction=self.astro.target(center['ra_hours'],center['dec_deg'],utc=midpoint)
            metadata.update(SIMSTART=started,SIMRA=center['ra_hours'],SIMDEC=center['dec_deg'],
                            SIMSCALE=206.264806247*device['pixel_um']/focal,SIMROT=direction['parallactic_deg'],
                            SITELAT=self.astro.site['latitude'],SITELONG=self.astro.site['longitude'],
                            SITEALT=self.astro.site['elevation'])
            # Calibration retains its existing quick field; imaging includes fainter stars.
            metadata.update(SIMMAG=16 if simulation_purpose=='calibration' else 18,
                            SIMMAX=5000 if simulation_purpose=='calibration' else 15000)
        result=await self.camera_call(self.camera.capture,exposure,binning,gain,dark,metadata,epoch,roi,frame_type,readout)
        after=self.mount.snapshot()
        result['encoder_before']={a['id']:a['position_deg'] for a in before['axes'][:2]}
        result['encoder_after']={a['id']:a['position_deg'] for a in after['axes'][:2]}
        result['encoder_valid']=all(a['valid'] for a in before['axes'][:2]+after['axes'][:2])
        result['preview_url']=f"/api/camera/preview/{Path(result['preview']).name}"
        result['device']=dict(device) if device else None
        result['telescope']=dict(telescope) if telescope else None
        await asyncio.to_thread(self.image_archive.ensure,result['filename'],result,result['fits'])
        if simulation_purpose!='calibration':await asyncio.to_thread(self.image_archive.add_to_series,result['filename'])
        self.camera.last=result
        return result

    @property
    def image_archive(self):
        from .image_archive import ImageArchive
        if not hasattr(self,'_image_archive') or self._image_archive.directory!=self.directory:
            self._image_archive=ImageArchive(self.directory)
        return self._image_archive

    async def solve(self,ra_hours,dec_deg,pixel_scale,add_point=True):
        capture=self.camera.last
        if not capture:raise ValueError('Capture an image first')
        # Freeze the capture metadata before another exposure can replace it.
        capture=json.loads(json.dumps(capture))
        if add_point and not capture.get('encoder_valid'):raise ValueError('Calibration needs valid encoder readings during exposure')
        if add_point and any(p.get('fits')==capture['fits'] for p in self.points):
            raise ValueError('This exposure already has a calibration point; capture another position')
        payload={'fits':capture['fits'],'ra_hours':ra_hours,'dec_deg':dec_deg,'pixel_scale':pixel_scale}
        def worker():
            result=subprocess.run([sys.executable,'-m','mount_service.solver_worker'],input=json.dumps(payload),
                                  capture_output=True,text=True,timeout=55,cwd=ROOT/'python-mount-service')
            if result.returncode:raise ValueError(f'PWLib solver process failed: {result.stderr[-1000:]}')
            solution=json.loads(result.stdout.strip().splitlines()[-1])
            if not solution.get('success'):raise ValueError(solution.get('error','Plate solve failed'))
            return solution
        solution=await asyncio.to_thread(worker)
        if add_point:
            before,after=capture['encoder_before'],capture['encoder_after']
            if any(abs(after[a]-before[a])>.02 for a in ('azimuth','altitude')):
                raise ValueError('Encoder moved more than 0.02° during exposure; point not added')
            t=self.astro.time(capture['capture_utc'])+capture['exposure_s']/2*u.s
            point={'id':uuid.uuid4().hex,'name':f'Point {len(self.points)+1}','included':True,
                   'solved_ra_hours':solution['ra_hours'],'solved_dec_deg':solution['dec_deg'],
                   'encoder_azimuth_deg':(before['azimuth']+after['azimuth'])/2,
                   'encoder_altitude_deg':(before['altitude']+after['altitude'])/2,'capture_utc':t.utc.isot,
                   'fits':capture['fits'],'solution':solution,'simulated':bool(capture.get('simulated'))}
            from .pointing import pointing_error
            point.update(expected_ra_hours=ra_hours,expected_dec_deg=dec_deg)
            point.update(pointing_error(ra_hours,dec_deg,solution['ra_hours'],solution['dec_deg']))
            self.points.append(point)
            self._save_points()
            await asyncio.to_thread(self.image_archive.update_metadata,capture['filename'],{'point':point,'solution':solution,'calibration_status':'solved'})
        return solution

    def _save_points(self):
        path=self.directory/'calibration-points.json'
        temporary=path.with_suffix('.tmp');temporary.write_text(json.dumps(self.points,indent=2));temporary.replace(path)

    async def fit(self):
        await self.stop_tracking()
        self.model_status=await self.call(self.pointing.fit,json.loads(json.dumps(self.points)))
        self.mount.event('info','Pointing model fitted',f"RMS {self.model_status['rms_arcsec']:.2f} arcsec")
        return self.model_status

    async def load_model(self,path):
        await self.stop_tracking()
        self.model_status=await self.call(self.pointing.load,path)
        return self.model_status

