"""Bounded SNTP measurements; never adjusts the operating system clock."""
import asyncio
import ipaddress
import math
import socket
import statistics
import struct
import time
from datetime import datetime, timezone
from urllib.parse import urlsplit

EPOCH = 2208988800
CANDIDATES = ('time.cloudflare.com', '0.th.pool.ntp.org', '1.th.pool.ntp.org', '0.asia.pool.ntp.org')

def host_from_input(value):
    value = value.strip()
    try:
        return str(ipaddress.ip_address(value))
    except ValueError:
        pass
    parsed = urlsplit(value if '://' in value else 'ntp://' + value)
    if parsed.scheme not in ('ntp', 'http', 'https') or parsed.username or parsed.password or parsed.query or parsed.fragment or parsed.path not in ('', '/'):
        raise ValueError('Enter an NTP IP, hostname or URL without a service path')
    if parsed.port not in (None, 123) or not parsed.hostname:
        raise ValueError('NTP uses UDP port 123; enter the server hostname')
    host = parsed.hostname.encode('idna').decode('ascii')
    if len(host) > 253 or any(not part or len(part)>63 or part.startswith('-') or part.endswith('-') or any(not(c.isalnum() or c=='-') for c in part) for part in host.rstrip('.').split('.')):
        raise ValueError('Invalid NTP hostname')
    return host.lower()

def timestamp(value):
    seconds = value + EPOCH
    return struct.pack('!II', int(seconds) & 0xffffffff, int((seconds % 1)*2**32))

def decode_timestamp(data, reference):
    seconds, fraction = struct.unpack('!II', data)
    value = seconds + fraction/2**32 - EPOCH
    return value + round((reference-value)/2**32)*2**32

def decode_reply(data, token, t1, t4, elapsed):
    if len(data)<48 or data[0]&7 != 4 or (data[0]>>3)&7 not in (3,4) or data[0]>>6 == 3:
        raise ValueError('Invalid or unsynchronized NTP reply')
    if data[1] == 0:
        raise ValueError('NTP server declined the request: '+data[12:16].decode('ascii',errors='replace'))
    if not 1 <= data[1] <= 15 or data[24:32] != token or not any(data[32:40]) or not any(data[40:48]):
        raise ValueError('NTP reply does not match a synchronized request')
    if abs((t4-t1)-elapsed)>.1:
        raise ValueError('Computer clock changed during measurement; retry')
    t2=decode_timestamp(data[32:40],t1); t3=decode_timestamp(data[40:48],t1)
    delay=elapsed-(t3-t2)
    if t3<t2 or delay<-.001:
        raise ValueError('Invalid NTP timestamp ordering')
    delay=max(0.,delay)
    root_delay=struct.unpack('!i',data[4:8])[0]/65536
    dispersion=struct.unpack('!I',data[8:12])[0]/65536
    return {'offset_ms':((t2-t1)+(t3-t4))*500, 'rtt_ms':delay*1000,
            'estimated_error_ms':(delay/2+max(0.,root_delay)/2+dispersion)*1000, 'stratum':data[1]}

def measure(host, address):
    samples=[]; error=None
    family, _, _, _, endpoint=address
    with socket.socket(family,socket.SOCK_DGRAM) as connection:
        connection.settimeout(2); connection.connect(endpoint)
        for attempt in range(3):
            if attempt: time.sleep(1)
            t1=time.time(); start=time.monotonic(); token=timestamp(t1)
            packet=bytearray(48);packet[0]=0x23;packet[40:48]=token
            try:
                connection.send(packet);data=connection.recv(512);elapsed=time.monotonic()-start;t4=time.time()
                samples.append(decode_reply(data,token,t1,t4,elapsed))
            except (OSError,ValueError) as exc:
                error=str(exc)
                break # includes Kiss-o'-Death; no immediate retries
    if not samples: return {'host':host,'address':endpoint[0],'ok':False,'error':error or 'No NTP reply'}
    best=min(samples,key=lambda s:s['estimated_error_ms'])
    jitter=math.sqrt(statistics.mean((s['offset_ms']-best['offset_ms'])**2 for s in samples))
    return {'host':host,'address':endpoint[0],'ok':True,**best,'jitter_ms':jitter,
            'estimated_error_ms':best['estimated_error_ms']+jitter,'samples':len(samples),
            'checked_utc':datetime.now(timezone.utc).isoformat()}

async def probe(host):
    try:
        addresses=await asyncio.wait_for(asyncio.get_running_loop().getaddrinfo(host,123,type=socket.SOCK_DGRAM),3)
        # Prefer IPv4 on the mount network; one endpoint per public service.
        addresses.sort(key=lambda a:a[0]!=socket.AF_INET)
        return await asyncio.to_thread(measure,host,addresses[0])
    except (OSError,TimeoutError,ValueError,IndexError) as exc:
        return {'host':host,'ok':False,'error':str(exc) or 'DNS lookup timed out'}

async def discover(host=None):
    results=await asyncio.gather(*(probe(h) for h in ((host,) if host else CANDIDATES)))
    results.sort(key=lambda r:(not r['ok'],r.get('estimated_error_ms',float('inf')),r.get('rtt_ms',float('inf'))))
    return {'results':results,'best':next((r for r in results if r['ok']),None)}
