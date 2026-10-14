"""Persistent observatory configuration, separate from engineering event history."""
import json
import os
import sqlite3
import uuid
from datetime import datetime, timezone
from pathlib import Path

class ConfigurationStore:
    def __init__(self, directory):
        self.path=Path(os.environ.get('NARIT_CONFIG_DB',str(directory/'observatory-config.sqlite3')))
        self.path.parent.mkdir(parents=True,exist_ok=True)
        with self.connect() as db:
            db.execute('PRAGMA journal_mode=WAL')
            db.execute('CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, value TEXT NOT NULL, updated_utc TEXT NOT NULL)')
            db.execute('CREATE TABLE IF NOT EXISTS telescopes (id TEXT PRIMARY KEY, specifications TEXT NOT NULL, created_utc TEXT NOT NULL, updated_utc TEXT NOT NULL)')
    def connect(self):return sqlite3.connect(self.path,timeout=5)
    def get(self,key,default=None):
        with self.connect() as db:row=db.execute('SELECT value FROM settings WHERE key=?',(key,)).fetchone()
        return json.loads(row[0]) if row else default
    def set(self,key,value):
        with self.connect() as db:db.execute('INSERT INTO settings VALUES(?,?,?) ON CONFLICT(key) DO UPDATE SET value=excluded.value,updated_utc=excluded.updated_utc',(key,json.dumps(value),datetime.now(timezone.utc).isoformat()))
    def migrate(self,values):
        with self.connect() as db:
            for key,value in values.items():
                if value is not None:db.execute('INSERT OR IGNORE INTO settings VALUES(?,?,?)',(key,json.dumps(value),datetime.now(timezone.utc).isoformat()))
    def telescopes(self):
        with self.connect() as db:rows=db.execute('SELECT id,specifications,created_utc,updated_utc FROM telescopes ORDER BY created_utc,id').fetchall()
        return [dict(id=r[0],**json.loads(r[1]),created_utc=r[2],updated_utc=r[3]) for r in rows]
    def save_telescope(self,specifications,identifier=None):
        identifier=identifier or uuid.uuid4().hex;now=datetime.now(timezone.utc).isoformat()
        with self.connect() as db:
            if db.execute('SELECT 1 FROM telescopes WHERE id=?',(identifier,)).fetchone():
                db.execute('UPDATE telescopes SET specifications=?,updated_utc=? WHERE id=?',(json.dumps(specifications),now,identifier))
            else:db.execute('INSERT INTO telescopes VALUES(?,?,?,?)',(identifier,json.dumps(specifications),now,now))
        return next(t for t in self.telescopes() if t['id']==identifier)
