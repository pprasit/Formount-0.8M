import asyncio
import contextvars
from concurrent.futures import ThreadPoolExecutor
import json
import logging
import os
import sqlite3
import threading
import time
import uuid
from datetime import datetime, timezone
from pathlib import Path

correlation = contextvars.ContextVar('audit_correlation', default={})


def clean(value):
    if isinstance(value, dict):
        return {str(k): '[redacted]' if any(word in str(k).lower() for word in ('password', 'token', 'secret', 'authorization', 'cookie')) else clean(v) for k, v in value.items()}
    if isinstance(value, list):
        return [clean(v) for v in value[:100]]
    if isinstance(value, str):
        return value[:12000]
    return value


class AuditStore:
    def __init__(self, path=None):
        self.path = Path(path or os.environ.get('NARIT_AUDIT_DB', Path(__file__).parent.parent/'runtime'/'engineering-audit.sqlite3'))
        self.path.parent.mkdir(parents=True, exist_ok=True)
        self.lock = threading.Lock()
        self.writer = ThreadPoolExecutor(max_workers=1, thread_name_prefix='engineering-audit')
        with sqlite3.connect(self.path) as db:
            db.execute('PRAGMA journal_mode=WAL')
            db.execute('CREATE TABLE IF NOT EXISTS events (id INTEGER PRIMARY KEY, event_id TEXT UNIQUE, utc TEXT NOT NULL, client_utc TEXT, session_id TEXT, action_id TEXT, request_id TEXT, source TEXT, kind TEXT, page TEXT, details TEXT NOT NULL)')
            db.execute('CREATE INDEX IF NOT EXISTS events_action ON events(action_id)')
            db.execute('CREATE INDEX IF NOT EXISTS events_session ON events(session_id)')

    def enqueue(self, kind, details=None, **fields):
        context={**correlation.get(), **fields, 'record_utc':datetime.now(timezone.utc).isoformat()}
        return self.writer.submit(self.record,kind,details,**context)

    def close(self):
        self.writer.shutdown(wait=True)

    def record(self, kind, details=None, **fields):
        context = {**correlation.get(), **fields}
        try:
            with self.lock, sqlite3.connect(self.path, timeout=5) as db:
                db.execute('INSERT OR IGNORE INTO events(event_id,utc,client_utc,session_id,action_id,request_id,source,kind,page,details) VALUES(?,?,?,?,?,?,?,?,?,?)', (
                    context.get('event_id', uuid.uuid4().hex), context.get('record_utc',datetime.now(timezone.utc).isoformat()), context.get('client_utc'),
                    context.get('session_id'), context.get('action_id'), context.get('request_id'), context.get('source', 'server'), kind,
                    context.get('page'), json.dumps(clean(details or {}), ensure_ascii=False, default=str)))
            return True
        except Exception:
            logging.exception('Engineering audit write failed')
            return False

    def query(self, limit=200, before=None, session_id=None, action_id=None):
        clauses=[]; args=[]
        for column,value in [('id',before),('session_id',session_id),('action_id',action_id)]:
            if value is not None:
                clauses.append(f'{column} '+('< ?' if column=='id' else '= ?'));args.append(value)
        sql='SELECT * FROM events'+(' WHERE '+' AND '.join(clauses) if clauses else '')+' ORDER BY id DESC LIMIT ?'
        with sqlite3.connect(self.path) as db:
            db.row_factory=sqlite3.Row
            rows=[dict(r) for r in db.execute(sql, [*args,limit])]
        for row in rows: row['details']=json.loads(row['details'])
        return rows


class AuditMiddleware:
    def __init__(self, app, store): self.app,self.store=app,store

    async def __call__(self, scope, receive, send):
        if scope['type']!='http' or not scope['path'].startswith('/api/') or scope['path'].startswith('/api/audit'):
            return await self.app(scope,receive,send)
        headers=dict(scope.get('headers', []))
        def header(name): return headers.get(name.encode(),b'').decode(errors='replace')[:128] or None
        context={'request_id':uuid.uuid4().hex,'session_id':header('x-audit-session'),'action_id':header('x-audit-action'),'page':header('x-audit-page')}
        token=correlation.set(context);started=time.perf_counter();body=bytearray();response=bytearray();status=500
        mutation=scope['method'] not in ('GET','HEAD','OPTIONS')
        details={'method':scope['method'],'path':scope['path'],'peer':scope.get('client',[None])[0]}
        if mutation:self.store.enqueue('request.started',dict(details),**context)
        async def read():
            message=await receive()
            if len(body)<16384:body.extend(message.get('body',b'')[:16384-len(body)])
            return message
        async def write(message):
            nonlocal status
            if message['type']=='http.response.start':
                status=message['status'];message['headers']=list(message.get('headers',[]))+[(b'x-request-id',context['request_id'].encode())]
            if message['type']=='http.response.body' and len(response)<16384: response.extend(message.get('body',b'')[:16384-len(response)])
            await send(message)
        # Capture rejected requests too: a 404/validation handler may never read its body.
        buffered=[]
        if mutation:
            while len(body)<16384:
                message=await read()
                buffered.append(message)
                if message['type']=='http.disconnect' or not message.get('more_body',False):break
        async def replay():
            if buffered:return buffered.pop(0)
            return await read()
        try:
            await self.app(scope,replay,write)
        except Exception as exc:
            details['exception']={'type':type(exc).__name__,'message':str(exc)}
            raise
        finally:
            if mutation or status>=400:
                for key,raw in [('request',body),('response',response)]:
                    if raw:
                        try:details[key]=json.loads(raw)
                        except (ValueError,UnicodeError):details[key]={'bytes_captured':len(raw)}
                details.update(status=status,duration_ms=round((time.perf_counter()-started)*1000,2))
                self.store.enqueue('request.failed' if status>=400 else 'request.completed',details,**context)
            correlation.reset(token)
