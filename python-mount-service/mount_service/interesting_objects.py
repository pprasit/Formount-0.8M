"""Persistent interesting targets; independent of mount communication."""
from contextlib import contextmanager
import json
import sqlite3

@contextmanager
def connect(path):
    db=sqlite3.connect(path,timeout=5)
    db.execute('CREATE TABLE IF NOT EXISTS interesting_objects (id TEXT PRIMARY KEY, object_json TEXT NOT NULL)')
    try:
        with db:yield db
    finally:db.close()

def list_objects(path):
    with connect(path) as db:
        return [json.loads(r[0]) for r in db.execute('SELECT object_json FROM interesting_objects ORDER BY rowid')]

def save_object(path,row):
    with connect(path) as db:
        db.execute('INSERT INTO interesting_objects VALUES(?,?) ON CONFLICT(id) DO UPDATE SET object_json=excluded.object_json',(row['interest_id'],json.dumps(row)))

def delete_object(path,identifier):
    with connect(path) as db:
        db.execute('DELETE FROM interesting_objects WHERE id=?',(identifier,))

def insert_objects(path,rows):
    added=0
    with connect(path) as db:
        for row in rows:
            cursor=db.execute('INSERT OR IGNORE INTO interesting_objects VALUES(?,?)',(row['interest_id'],json.dumps(row)))
            added+=cursor.rowcount
    return added
