"""Web calibration workflows ported from MountCalibrationManager's controls."""
import asyncio
import json
import math
import os
import time
import uuid
from datetime import datetime, timezone
from collections import deque
from pathlib import Path
from .pointing import parse_pxp_points
from .configuration import ConfigurationStore

DEFAULTS={'backend':'legacy-pxp','azimuth_points':8,'altitude_points':5,'min_altitude':30.,'max_altitude':85.,'min_azimuth':0.,'max_azimuth':360.,'point_count':None,
          'exposure_s':3.,'binning':2,'gain':0,'pixel_scale':.6,'tracking_seconds':8.,'arrival_deg':.5,
          'rms_threshold_arcsec':3.,'stable_seconds':1.,'minimum_samples':6,'extra_settle_s':.75,
          'post_exposure_s':.5,'rms_timeout_s':20.}


class PointingWorkbench:
    def __init__(self, observatory):
        self.o=observatory;self.path=Path(os.environ.get('NARIT_POINTING_WORKBENCH',str(observatory.directory/'pointing-workbench.json')))
        self.ntp=None;self.location=None;self.settings=dict(DEFAULTS);self.plan=[];self.state='idle';self.message='Create points or load a model to begin.'
        self.task=None;self.operation=None;self.current=None;self.started=None;self.candidate=None;self.analysis={};self.live_rms=None
        self.image_session_started_utc=None
        try:
            saved=json.loads(self.path.read_text());self.settings.update(saved.get('settings',{}));self.plan=saved.get('plan',[]);self.location=saved.get('location');self.ntp=saved.get('ntp')
            self.image_session_started_utc=saved.get('image_session_started_utc')
            for p in self.plan:
                if p.get('status') in ('slewing','tracking','capturing','solving'):p['status']='pending'
        except (OSError,ValueError):pass
        self.config=ConfigurationStore(observatory.directory)
        self.config.migrate({'location':self.location,'ntp':self.ntp})
        self.location=self.config.get('location');self.ntp=self.config.get('ntp')
        if self.location:self.o.set_site(self.location)

    @property
    def busy(self):return self.task is not None and not self.task.done()

    def idle_required(self):
        if getattr(self.o, 'demo', None) and self.o.demo.active:raise ValueError('Stop Demo before starting another operation')
        if self.busy:raise ValueError('Stop calibration/analysis before editing this operation')

    def save(self):
        self.config.set('location',self.location);self.config.set('ntp',self.ntp)
        temp=self.path.with_suffix('.tmp');temp.write_text(json.dumps({'settings':self.settings,'plan':self.plan,'location':self.location,'ntp':self.ntp,'image_session_started_utc':self.image_session_started_utc},indent=2));temp.replace(self.path)

    def new_image_session(self):
        self.idle_required()
        self.archive_points()
        self.o.points=[];self.o._save_points();self.plan=[];self.analysis={}
        self.current=None;self.started=None;self.state='idle'
        self.image_session_started_utc=datetime.now(timezone.utc).isoformat()
        self.candidate=None
        self.save()
        return self.snapshot()

    def sync_points(self):
        existing={}
        for row in self.plan:
            identifier=row.get('point_id')
            if identifier and (identifier not in existing or row.get('capture_filename')):existing[identifier]=row
        self.plan=[row for row in self.plan if not row.get('point_id') or existing[row['point_id']] is row]
        for point in self.o.points:
            row=existing.get(point['id'])
            if row:
                row['included']=point.get('included',True)
                if row.get('automatic_series') is False:point['automatic_series']=False
                elif point.get('automatic_series') is False:row['automatic_series']=False
            elif not any(p.get('status')=='solving' for p in self.plan):
                self.plan.append({'id':uuid.uuid4().hex,'point_id':point['id'],'azimuth_deg':point['encoder_azimuth_deg'],'altitude_deg':point['encoder_altitude_deg'],'status':'rejected' if point.get('auto_rejected') and not point.get('included',True) else 'solved','included':point.get('included',True),'automatic_series':point.get('automatic_series',True),'auto_rejected':point.get('auto_rejected',False),'rejection_reason':point.get('rejection_reason')})

    def snapshot(self):
        self.sync_points()
        series=[p for p in self.plan if p.get('automatic_series',True)]
        processed=sum(p.get('status') in ('solved','failed','rejected','skipped') for p in series)
        elapsed=time.monotonic()-self.started if self.started else 0
        errors=[p['total_error_arcsec'] for p in self.fit_points() if p.get('included',True)
                and isinstance(p.get('total_error_arcsec'),(int,float)) and math.isfinite(p['total_error_arcsec'])]
        return {'settings':self.settings,'plan':self.plan,'state':self.state,'message':self.message,'busy':self.busy,
                'image_session_started_utc':self.image_session_started_utc,'operation':self.operation,
                'calibration_rms_arcsec':math.sqrt(sum(e*e for e in errors)/len(errors)) if errors else None,
                'model_rms_arcsec':self.candidate.get('rms_arcsec') if self.candidate else None,
                'current':self.current,'processed':processed,'solved':sum(p.get('status')=='solved' for p in series),
                'total':len(series),'eta_s':elapsed/processed*(len(series)-processed) if processed else None,
                'save_ready':self.save_ready(),'tracking_rms_arcsec':self.live_rms,'candidate':self.candidate,'analysis':self.analysis}

    def fit_points(self):
        if not self.image_session_started_utc:return self.o.points
        boundary=datetime.fromisoformat(self.image_session_started_utc.replace('Z','+00:00'))
        points=[]
        for point in self.o.points:
            try:
                captured=datetime.fromisoformat(point['capture_utc'].replace('Z','+00:00'))
                if captured.tzinfo is None:captured=captured.replace(tzinfo=timezone.utc)
                if captured>=boundary:points.append(point)
            except (KeyError,TypeError,ValueError):continue
        return points

    def save_ready(self):
        points=[p for p in self.fit_points() if p.get('included',True) and all(isinstance(p.get(k),(int,float)) and math.isfinite(p[k]) for k in ('encoder_azimuth_deg','encoder_altitude_deg','solved_ra_hours','solved_dec_deg'))]
        return len({(round(p['encoder_azimuth_deg'],2),round(p['encoder_altitude_deg'],2)) for p in points})>=9

    async def save_model(self,directory,name):
        directory=Path(directory).expanduser().resolve()
        if not directory.is_dir():raise ValueError('Select an existing folder on the controller computer')
        name=name.strip()
        if not name.lower().endswith('.pxp'):name+='.pxp'
        if not name[:-4].strip() or any(c in name for c in '<>:"/\\|?*') or name.endswith((' ','.')) or any(ord(c)<32 for c in name):raise ValueError('Enter a valid .pxp filename')
        if name.split('.')[0].upper() in {'CON','PRN','AUX','NUL',*[f'COM{i}' for i in range(1,10)],*[f'LPT{i}' for i in range(1,10)]}:raise ValueError('This filename is reserved by Windows')
        destination=directory/name
        if destination.exists():raise ValueError('A file with this name already exists; choose a different name')
        if not self.save_ready():raise ValueError('Need at least 9 distinct included solved positions before saving a model')
        if not self.candidate:await self.build(False)
        content=await asyncio.to_thread(Path(self.candidate['path']).read_bytes)
        def write():
            with destination.open('xb') as stream:
                stream.write(content);stream.flush();os.fsync(stream.fileno())
        await asyncio.to_thread(write)
        await self.o.load_model(str(destination))
        self.candidate={**self.candidate,'path':str(destination),'saved':True}
        (self.o.directory/'active-model.json').write_text(json.dumps({'path':str(destination)}))
        self.state='complete';self.message='Model saved and compensation activated: '+str(destination)
        self.o.mount.event('info','Pointing model saved',str(destination))
        return {'path':str(destination),'model':self.o.model_status}

    def configure(self, values):
        self.idle_required();self.settings.update(values);self.save();return self.snapshot()

    def create_grid(self):
        self.idle_required();s=self.settings
        from .blind_spots import contains
        blocked=self.config.get('blind_spots',[]);blocked_count=0
        solved=[p for p in self.plan if p.get('status') in ('solved','rejected') and p.get('automatic_series',True)]
        plan=[p for p in self.plan if not p.get('automatic_series',True)]+list(solved)
        count=s.get('point_count')
        rows=s['altitude_points'] if not count else min(12,max(1,math.ceil(math.sqrt(count))))
        columns=s['azimuth_points'] if not count else math.ceil(count/rows)
        az_min,az_max=s.get('min_azimuth',0),s.get('max_azimuth',360)
        for az_index in range(columns):
            az=az_min+az_index*(az_max-az_min)/max(1,columns if az_max-az_min==360 else columns-1)
            for alt_index in range(rows):
                if count and az_index*rows+alt_index>=count:continue
                alt=s['min_altitude']+(s['max_altitude']-s['min_altitude'])*alt_index/max(1,rows-1)
                if contains(blocked,az,alt):
                    blocked_count+=1
                    plan.append({'id':uuid.uuid4().hex,'azimuth_deg':az,'altitude_deg':alt,'status':'skipped','included':False,'blind_spot':True,'error':'Obstructed by a saved blind spot'})
                    continue
                if any(abs((p['azimuth_deg']-az+180)%360-180)<5 and abs(p['altitude_deg']-alt)<5 for p in solved):continue
                plan.append({'id':uuid.uuid4().hex,'azimuth_deg':az,'altitude_deg':alt,'status':'pending','included':True})
        self.plan=plan;self.state='ready';self.message=f'{sum(p.get("automatic_series",True) and p.get("status")=="pending" for p in plan)} grid positions ready.'+(f' {blocked_count} positions marked red and skipped by blind spots.' if blocked_count else '');self.save();return self.snapshot()

    async def new_session(self):
        self.idle_required()
        if self.o.tracking:raise ValueError('Stop tracking before starting a new model session')
        # Archive real observations; New Model does not erase previous captures/models.
        if self.o.points:
            archive=self.o.directory/f'calibration-points-{uuid.uuid4().hex}.json';archive.write_text(json.dumps(self.o.points,indent=2))
        self.o.points=[];self.o._save_points();self.plan=[];self.candidate=None;self.analysis={}
        self.image_session_started_utc=datetime.now(timezone.utc).isoformat()
        self.o.model_status=await self.o.call(self.o.pointing.unload)
        self.state='idle';self.message='New session created; previous observations archived.';self.save();return self.snapshot()

    async def load(self,path):
        points=await asyncio.to_thread(parse_pxp_points,path)
        await self.o.load_model(path)
        self.archive_points()
        self.candidate=None
        self.o.points=points;self.o._save_points()
        self.plan=[{'id':uuid.uuid4().hex,'azimuth_deg':p['encoder_azimuth_deg'],'altitude_deg':p['encoder_altitude_deg'],
                    'point_id':p['id'],'status':'solved','included':p['included']} for p in points]
        self.state='ready';self.message='Model loaded; calibration points restored.'
        self.save();return self.snapshot()

    async def fit(self,activate=False):
        self.idle_required()
        return await self.build(activate)

    async def build(self,activate=False):
        await self.o.stop_tracking()
        self.candidate=await self.o.call(self.o.pointing.fit,json.loads(json.dumps(self.fit_points())),self.settings['backend'],activate)
        self.o.model_status=await self.o.call(self.o.pointing.status)
        self.state='complete';self.message='Model built. Choose Use This Model or Keep Current Model.'
        return self.candidate

    async def use_candidate(self):
        self.idle_required()
        if not self.candidate:raise ValueError('Build a model first')
        await self.o.load_model(self.candidate['path']);self.message='Latest model activated.';return self.o.model_status

    def select(self,identifier,included):
        self.idle_required();p=next((p for p in self.plan if p['id']==identifier),None)
        if p is None:raise ValueError('Point not found')
        if p.get('blind_spot') and included:raise ValueError('This position is obstructed and cannot be used for calibration')
        p['included']=included
        if p.get('auto_rejected'):
            p['status']='solved' if included else 'rejected'
            p['quality_override']=included
        for point in self.o.points:
            if point['id']==p.get('point_id'):
                point['included']=included
                filename=p.get('capture_filename') or (Path(point['fits']).name if point.get('fits') else None)
                if filename and self.o.image_archive.get(filename):self.o.image_archive.update_metadata(filename,{'point':point,'included':included,'calibration_status':p['status'],'quality_override':p.get('quality_override',False)})
        self.candidate=None
        self.o._save_points();self.save();return self.snapshot()

    def delete(self, identifiers):
        self.idle_required();self.archive_points();ids=set(identifiers);point_ids={p.get('point_id') for p in self.plan if p['id'] in ids}
        self.plan=[p for p in self.plan if p['id'] not in ids];self.o.points=[p for p in self.o.points if p['id'] not in point_ids]
        self.o._save_points();self.save();return self.snapshot()

    def archive_points(self):
        if self.o.points:
            (self.o.directory/f'calibration-points-{uuid.uuid4().hex}.json').write_text(json.dumps(self.o.points,indent=2),encoding='utf-8')

    def rerun(self):
        self.idle_required();self.archive_points()
        for p in self.plan:
            if p.get('blind_spot'):continue
            p['status']='pending';p.pop('error',None)
            old=p.pop('point_id',None)
            self.o.points=[point for point in self.o.points if point['id']!=old]
        self.o._save_points();self.save();self.state='ready';return self.snapshot()

    def reset(self):
        self.idle_required()
        self.archive_points()
        self.o.points=[];self.o._save_points();self.plan=[];self.candidate=None
        self.current=None;self.live_rms=None;self.started=None
        return self.create_grid()

    def camera_ready(self,motion=False):
        if not self.o.camera.connected:raise ValueError('Connect a camera before calibration/analysis')
        if self.o.camera.busy:raise ValueError('Camera is busy')
        if any(j['status']=='running' and j['kind'] in ('camera scan','camera connect','camera disconnect','capture')
               for j in getattr(self.o,'jobs',{}).values()):raise ValueError('Wait for the current camera operation before calibration/analysis')
        if motion:
            axes=self.o.mount.snapshot()['axes'][:2]
            if not all(a['enabled'] and a['valid'] for a in axes):raise ValueError('Enable both mount axes with fresh telemetry before calibration')
            if self.o.tracking:raise ValueError('Stop external tracking before starting calibration')

    def launch(self,kind, function,*args):
        self.idle_required();self.operation=kind;self.started=time.monotonic();self.state='running';self.message=kind
        async def operation():
            try:return await function(*args)
            except asyncio.CancelledError:raise
            except Exception as exc:
                self.state='failed';self.message=str(exc);raise
            finally:self.save()
        job=self.o.new_job(kind,operation);self.task=self.o.jobs[job['id']]['_task'];return job

    def start(self):
        self.camera_ready(True)
        if not any(p.get('status')=='pending' and p.get('included',True) and p.get('automatic_series',True) for p in self.plan):raise ValueError('Create or rerun pending points first')
        return self.launch('pointing calibration',self.run)

    async def stop(self,pause=True):
        if self.busy:
            self.state='paused' if pause else 'stopped';self.o.camera.abort()
            if self.operation not in ('fit model','build pointing model','load model'):self.task.cancel()
            try:await self.task
            except asyncio.CancelledError:pass
            await self.o.stop_tracking()
            await self.o.mount.stop_all()
            self.state='paused' if pause else 'stopped'
        self.current=None;self.message='Paused; completed points retained.' if pause else 'Stopped.';self.save();return self.snapshot()

    async def position(self,p):
        from .blind_spots import contains
        if 'azimuth_deg' in p and 'altitude_deg' in p and contains(self.config.get('blind_spots',[]),p['azimuth_deg'],p['altitude_deg']):raise ValueError('This calibration position is inside a saved blind spot. Rebuild the calibration grid.')
        s=self.settings
        coordinate=({'ra_hours':p['ra_hours'],'dec_deg':p['dec_deg']} if 'ra_hours' in p else await self.o.call(self.o.astro.from_encoder,p['azimuth_deg'],p['altitude_deg']))
        self.current={**p,**coordinate};p['status']='slewing';self.message='Slewing to next position'
        target=await self.o.goto(coordinate)
        deadline=time.monotonic()+90
        while True:
            axes=self.o.mount.snapshot()['axes'][:2]
            if not all(a['valid'] and a['enabled'] for a in axes):raise ValueError('Lost valid/enabled mount axes during slew')
            error=max(abs(axes[0]['position_deg']-target['encoder_azimuth_deg']),abs(axes[1]['position_deg']-target['encoder_altitude_deg']))
            if error<=s['arrival_deg']:break
            if time.monotonic()>deadline:raise ValueError('Calibration slew timed out')
            await asyncio.sleep(.15)
        p['status']='tracking';self.message='Tracking and checking RMS stability'
        await self.o.start_tracking(coordinate);began=time.monotonic();stable=None;samples=deque(maxlen=max(6,s['minimum_samples']))
        while True:
            if not self.o.tracking:raise ValueError('Tracking stopped during calibration')
            predicted=await self.o.preview(coordinate);axes=self.o.mount.snapshot()['axes'][:2]
            if not all(a['valid'] and a['enabled'] for a in axes):raise ValueError('Lost fresh encoders while settling')
            az_error=(axes[0]['position_deg']-predicted['encoder_azimuth_deg'])*math.cos(math.radians(predicted['encoder_altitude_deg']))
            alt_error=axes[1]['position_deg']-predicted['encoder_altitude_deg'];samples.append(math.hypot(az_error,alt_error)*3600)
            self.live_rms=math.sqrt(sum(v*v for v in samples)/len(samples))
            now=time.monotonic()
            if len(samples)>=s['minimum_samples'] and self.live_rms<=s['rms_threshold_arcsec']:stable=stable or now
            else:stable=None
            if now-began>=s['tracking_seconds'] and stable and now-stable>=s['stable_seconds']+s['extra_settle_s']:break
            if now-began>=s['rms_timeout_s']:raise ValueError('Tracking did not meet RMS stability threshold; exposure skipped')
            await asyncio.sleep(.15)
        return coordinate

    async def capture_point(self,p,coordinate):
        s=self.settings;p['status']='capturing';self.message='Capturing calibration image'
        capture=await self.o.capture(s['exposure_s'],s['binning'],s['gain'],False,simulation_purpose='calibration')
        p['capture_filename']=capture['filename'];self.save()
        await asyncio.to_thread(self.o.image_archive.update_metadata,capture['filename'],
            {'calibration_plan_id':p['id'],'automatic_series':p.get('automatic_series',True),'target_azimuth_deg':p['azimuth_deg'],'target_altitude_deg':p['altitude_deg'],'calibration_status':'captured'})
        p['status']='solving';self.message='Plate solving J2000 coordinates'
        try:
            solution=await self.o.solve(coordinate['ra_hours'],coordinate['dec_deg'],s['pixel_scale']*s['binning'])
        except Exception as exc:
            await asyncio.to_thread(self.o.image_archive.update_metadata,capture['filename'],{'calibration_status':'failed','error':str(exc)})
            raise
        point=self.o.points[-1];p['point_id']=point['id'];p['status']='solved';p['stars']=solution.get('matched_stars');p['solve_rms_arcsec']=solution.get('rms_arcsec');p.pop('error',None)
        self.candidate=None
        point['automatic_series']=p.get('automatic_series',True)
        error=point.get('total_error_arcsec')
        if point['automatic_series'] and isinstance(error,(int,float)) and math.isfinite(error) and error>100:
            reason=f'Pointing error {error:.2f} arcsec exceeds the 100 arcsec limit'
            p.update(status='rejected',included=False,auto_rejected=True,rejection_reason=reason)
            point.update(included=False,auto_rejected=True,rejection_reason=reason)
            self.message=reason+'; excluded from model'
        self.o._save_points()
        await asyncio.to_thread(self.o.image_archive.update_metadata,capture['filename'],
            {'point':point,'included':point.get('included',True),'calibration_status':p['status'],
             'auto_rejected':p.get('auto_rejected',False),'rejection_reason':p.get('rejection_reason')})
        if self.save_ready() and point.get('included',True):
            self.message='Updating working pointing model'
            try:
                self.candidate=await self.o.call(self.o.pointing.fit,json.loads(json.dumps(self.fit_points())),s['backend'],True)
                self.o.model_status=await self.o.call(self.o.pointing.status)
            except Exception as exc:
                self.message='Point saved; working model fit failed: '+str(exc)
                self.o.mount.event('warning','Working model fit failed',str(exc))
        await asyncio.sleep(s['post_exposure_s']);self.save()

    async def run(self):
        try:
            for p in self.plan:
                if p.get('status')!='pending' or not p.get('included',True) or not p.get('automatic_series',True):continue
                try:
                    coordinate=await self.position(p)
                    await self.capture_point(p,coordinate)
                except asyncio.CancelledError:
                    if p.get('status') not in ('solved','rejected'):p['status']='pending'
                    raise
                except Exception as exc:
                    p['status']='failed';p['error']=str(exc);self.save();raise
                finally:
                    await self.o.stop_tracking()
                    await self.o.mount.stop_all()
            if sum(p.get('included',True) for p in self.fit_points())>=9:
                self.message='Fitting completion model'
                self.candidate=await self.o.call(self.o.pointing.fit,json.loads(json.dumps(self.fit_points())),self.settings['backend'],False)
                self.o.model_status=await self.o.call(self.o.pointing.status)
            self.state='complete';self.message='Calibration complete. Review results before using the model.'
        except asyncio.CancelledError:raise
        except Exception as exc:self.state='failed';self.message=str(exc);raise
        finally:self.current=None;self.save()

    def add_current(self):
        self.camera_ready();axes=self.o.mount.snapshot()['axes'][:2]
        if not all(a['valid'] for a in axes):raise ValueError('Fresh encoders are required')
        p={'id':uuid.uuid4().hex,'azimuth_deg':axes[0]['position_deg'],'altitude_deg':axes[1]['position_deg'],'status':'pending','included':True,'automatic_series':False}
        self.idle_required();self.plan.append(p);self.save()
        async def capture():
            try:
                coordinate=await self.o.call(self.o.astro.from_encoder,p['azimuth_deg'],p['altitude_deg'])
                await self.capture_point(p,coordinate);self.state='complete';self.message='Current position captured and solved.'
            except asyncio.CancelledError:
                if p.get('status')!='solved':p['status']='pending'
                raise
            except Exception as exc:p['status']='failed';p['error']=str(exc);self.state='failed';raise
            finally:self.save()
        return self.launch('add calibration point',capture)
