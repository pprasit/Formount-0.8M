"""Persistent display data, capture metadata and per-image stretch settings."""
import io
import json
import os
import sqlite3
import uuid
import unicodedata
from datetime import datetime
from pathlib import Path
from functools import lru_cache

import numpy as np
from astropy.io import fits
from PIL import Image
from .camera_preview import PROFILES, preview_sensor, stretched_preview


class SeriesExistsError(ValueError):
    pass


class ImageArchive:
    def __init__(self, directory):
        self.directory=Path(directory)
        self.path=Path(os.environ.get('NARIT_IMAGE_DB',str(self.directory/'camera-images.sqlite3')))
        self.path.parent.mkdir(parents=True,exist_ok=True)
        with self.connect() as db:
            db.execute('PRAGMA journal_mode=WAL')
            db.execute('''CREATE TABLE IF NOT EXISTS images (
                filename TEXT PRIMARY KEY, metadata TEXT NOT NULL, presets TEXT NOT NULL,
                view TEXT NOT NULL, width INTEGER NOT NULL, height INTEGER NOT NULL,
                sensor BLOB NOT NULL, png BLOB NOT NULL)''')

    def current_series(self):
        with self.connect() as db:
            db.execute('CREATE TABLE IF NOT EXISTS imaging_series (id TEXT PRIMARY KEY, created_utc TEXT NOT NULL)')
            db.execute('CREATE TABLE IF NOT EXISTS imaging_series_images (filename TEXT PRIMARY KEY, series_id TEXT NOT NULL)')
            if 'name' not in {r['name'] for r in db.execute('PRAGMA table_info(imaging_series)')}:db.execute("ALTER TABLE imaging_series ADD COLUMN name TEXT NOT NULL DEFAULT 'Previous series'")
            row=db.execute('SELECT id,created_utc,name FROM imaging_series ORDER BY rowid DESC LIMIT 1').fetchone()
            if row is None:
                identifier=uuid.uuid4().hex;created=datetime.now().isoformat()
                db.execute('INSERT INTO imaging_series(id,created_utc,name) VALUES(?,?,?)',(identifier,created,'Previous series'))
                name='Previous series'
                db.execute('INSERT OR IGNORE INTO imaging_series_images SELECT filename,? FROM images',(identifier,))
            else:identifier,created,name=row['id'],row['created_utc'],row['name']
            images=db.execute('SELECT i.filename,i.metadata FROM images i JOIN imaging_series_images s ON i.filename=s.filename WHERE s.series_id=? ORDER BY i.rowid DESC',(identifier,)).fetchall()
        return {'id':identifier,'name':name,'created_utc':created,'count':len(images),'images':[{'filename':r['filename'],'metadata':json.loads(r['metadata'])} for r in images]}

    def new_series(self,name):
        name=name.strip()
        if not name or len(name)>120:raise ValueError('Series name must contain 1 to 120 characters')
        self.current_series()
        identifier=uuid.uuid4().hex
        key=unicodedata.normalize('NFKC',name).casefold()
        with self.connect() as db:
            db.execute('BEGIN IMMEDIATE')
            if any(unicodedata.normalize('NFKC',row['name'].strip()).casefold()==key for row in db.execute('SELECT name FROM imaging_series')):
                raise SeriesExistsError('This series name already exists in the database. Enter a new series name.')
            db.execute('INSERT INTO imaging_series(id,created_utc,name) VALUES(?,?,?)',(identifier,datetime.now().isoformat(),name))
        return self.current_series()

    def add_to_series(self,filename):
        series=self.current_series()
        with self.connect() as db:db.execute('INSERT OR IGNORE INTO imaging_series_images VALUES(?,?)',(filename,series['id']))

    def connect(self):
        db=sqlite3.connect(self.path,timeout=10)
        db.row_factory=sqlite3.Row
        return db

    @staticmethod
    def validate(filename):
        if Path(filename).name!=filename or not filename.endswith('.fits') or '\\' in filename:
            raise ValueError('Invalid image filename')

    def get(self, filename):
        self.validate(filename)
        with self.connect() as db:
            row=db.execute('SELECT filename,metadata,presets,view,width,height FROM images WHERE filename=?',(filename,)).fetchone()
        if row is None:return None
        return dict(filename=row['filename'],metadata=json.loads(row['metadata']),presets=json.loads(row['presets']),
                    stretch=json.loads(row['view']),preview_width=row['width'],preview_height=row['height'])

    def ensure(self, filename, metadata=None, source=None):
        self.validate(filename)
        if self.get(filename):
            if metadata:self.update_metadata(filename,metadata)
            return self.get(filename)
        path=Path(source) if source else self.directory/'captures'/filename
        image,header=fits.getdata(path,header=True,memmap=False)
        small=preview_sensor(image).astype('<u2')
        presets={}
        png=None
        for name in PROFILES:
            preview,settings=stretched_preview(small,name)
            presets[name]=settings
            if name=='Auto':
                buffer=io.BytesIO();preview.save(buffer,format='PNG',optimize=True);png=buffer.getvalue()
        limits=np.percentile(small,[.05,99.99])
        values={'filename':filename,'fits':str(path),'preview_url':f'/api/camera/preview/{Path(filename).with_suffix(".png").name}',
                'width':image.shape[1],'height':image.shape[0],
                'capture_utc':header.get('DATE-OBS'),'exposure_s':header.get('EXPTIME'),
                'binning':header.get('XBINNING',1),'gain':header.get('GAIN'),
                'simulated':bool(header.get('SIMULATE',False)),
                'encoder_before':{'azimuth':header.get('AZENC'),'altitude':header.get('ALTENC')},
                'fits_header':[{'key':c.keyword,'value':c.value if isinstance(c.value,(str,int,float,bool)) else str(c.value),'comment':c.comment} for c in header.cards],
                'slider_limits':{'minimum':float(limits[0]),'maximum':float(min(65535,max(limits[1]*2,limits[0]+64)))}}
        values.update(metadata or {})
        with self.connect() as db:
            db.execute('INSERT OR IGNORE INTO images VALUES(?,?,?,?,?,?,?,?)',
                       (filename,json.dumps(values),json.dumps(presets),json.dumps(presets['Auto']),small.shape[1],small.shape[0],small.tobytes(),png))
        return self.get(filename)

    def update_metadata(self,filename,values):
        self.validate(filename)
        with self.connect() as db:
            db.execute('BEGIN IMMEDIATE')
            row=db.execute('SELECT metadata FROM images WHERE filename=?',(filename,)).fetchone()
            if row is None:raise FileNotFoundError(filename)
            metadata=json.loads(row['metadata']);metadata.update(values)
            db.execute('UPDATE images SET metadata=? WHERE filename=?',(json.dumps(metadata),filename))

    def save_stretch(self,filename,values):
        self.validate(filename)
        with self.connect() as db:
            db.execute('BEGIN IMMEDIATE')
            row=db.execute('SELECT view,sensor,width,height FROM images WHERE filename=?',(filename,)).fetchone()
            if row is None:raise FileNotFoundError(filename)
            previous=json.loads(row['view'])
            if any(previous.get(key)!=values.get(key) for key in ('black','white','tone')):
                sensor=np.frombuffer(row['sensor'],dtype='<u2').reshape(row['height'],row['width']).astype(np.float32)
                low,high,tone=values['black'],values['white'],values['tone']
                display=np.arcsinh(np.clip((sensor-low)/max(1,high-low),0,1)*tone)/np.arcsinh(tone)
                buffer=io.BytesIO();Image.fromarray(np.rint(display*255).astype(np.uint8)).save(buffer,format='PNG')
                db.execute('UPDATE images SET png=? WHERE filename=?',(buffer.getvalue(),filename))
            db.execute('UPDATE images SET view=? WHERE filename=?',(json.dumps(values),filename))
        return self.get(filename)

    def source(self,filename,png=False):
        self.validate(filename)
        with self.connect() as db:
            row=db.execute('SELECT sensor,png,width,height,presets FROM images WHERE filename=?',(filename,)).fetchone()
        if row is None:raise FileNotFoundError(filename)
        return row['png' if png else 'sensor'],row['width'],row['height'],json.loads(row['presets'])['Auto']

    @lru_cache(maxsize=4)
    def display_levels(self,filename):
        content,width,height,_=self.source(filename)
        raw=np.frombuffer(content,dtype='<u2')
        cumulative=np.cumsum(np.bincount(raw,minlength=65536))
        ranks=np.maximum(1,np.ceil(raw.size*np.linspace(0,1,1001)))
        return {'percentiles':np.searchsorted(cumulative,ranks).tolist()}

    @lru_cache(maxsize=2)
    def display_sensor(self,filename,maximum):
        self.validate(filename)
        if maximum<=1280:
            content,width,height,_=self.source(filename)
            raw=np.frombuffer(content,dtype='<u2').reshape(height,width)
        else:
            record=self.get(filename)
            if not record:raise FileNotFoundError(filename)
            raw=fits.getdata(record['metadata']['fits'],memmap=False)
        return preview_sensor(raw,maximum).astype(np.float32)

    @lru_cache(maxsize=2)
    def display_data(self,filename,maximum):
        raw=self.display_sensor(filename,maximum).astype(np.uint16)
        packed=np.zeros((*raw.shape,3),dtype=np.uint8)
        packed[:,:,0]=raw>>8
        packed[:,:,1]=raw&255
        buffer=io.BytesIO()
        Image.fromarray(packed).save(buffer,format='PNG',compress_level=3)
        return buffer.getvalue()

    def display_webp(self,filename,maximum,black=None,white=None,tone=None):
        record=self.get(filename)
        if not record:raise FileNotFoundError(filename)
        view=record['stretch'];low=view['black'] if black is None else black
        high=view['white'] if white is None else white;strength=view['tone'] if tone is None else tone
        raw=self.display_sensor(filename,maximum)
        values=np.arcsinh(np.clip((raw-low)/max(1,high-low),0,1)*strength)/np.arcsinh(strength)
        buffer=io.BytesIO();Image.fromarray(np.rint(values*255).astype(np.uint8)).save(buffer,format='WEBP',quality=85,method=2)
        return buffer.getvalue(),raw.shape[1],raw.shape[0]

    def calibration_images(self,session_started_utc=None):
        with self.connect() as db:
            rows=db.execute('SELECT filename,metadata FROM images ORDER BY rowid DESC').fetchall()
        result=[]
        for row in rows:
            metadata=json.loads(row['metadata'])
            if session_started_utc:
                try:
                    if datetime.fromisoformat(metadata['capture_utc'].replace('Z','+00:00'))<datetime.fromisoformat(session_started_utc.replace('Z','+00:00')):continue
                except (KeyError,TypeError,ValueError):continue
                result.append({'filename':row['filename'],'metadata':metadata})
                continue
            if metadata.get('calibration_plan_id') or metadata.get('point'):
                result.append({'filename':row['filename'],'metadata':metadata})
        return result

    def latest(self):
        with self.connect() as db:
            row=db.execute('SELECT filename FROM images ORDER BY rowid DESC LIMIT 1').fetchone()
        return self.get(row['filename']) if row else None
