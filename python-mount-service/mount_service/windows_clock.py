"""Persistent Windows Time configuration; no application-only UTC offset."""
import asyncio
import ctypes
import os
import re
import subprocess
from pathlib import Path
from .ntp import host_from_input


def run(*args):
    result=subprocess.run(args,capture_output=True,text=True,timeout=20,
                          creationflags=getattr(subprocess,'CREATE_NO_WINDOW',0))
    if result.returncode:
        raise ValueError('Windows Time: '+(result.stdout+result.stderr).strip())
    return result.stdout.strip()


def is_admin():
    return os.name=='nt' and bool(ctypes.windll.shell32.IsUserAnAdmin())


def inspect(host):
    if os.name!='nt':return {'state':'unavailable','message':'Windows Time is unavailable on this server'}
    import winreg
    with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE,r'SYSTEM\CurrentControlSet\Services\W32Time\Parameters') as key:
        peers=winreg.QueryValueEx(key,'NtpServer')[0]
        mode=winreg.QueryValueEx(key,'Type')[0]
    # /query /source requires elevation on some Windows builds; status is readable.
    native_status=run('w32tm','/query','/status')
    match=re.search(r'^Source:\s*(.+)$',native_status,re.MULTILINE)
    source=match.group(1).strip() if match else ''
    leap=re.search(r'^Leap Indicator:\s*(\d+)',native_status,re.MULTILINE)
    stratum=re.search(r'^Stratum:\s*(\d+)',native_status,re.MULTILINE)
    synchronized=bool(leap and leap[1]=='0' and stratum and 1<=int(stratum[1])<=15)
    configured=mode.upper()=='NTP' and host.lower() in [p.split(',')[0].lower() for p in peers.split()]
    return {'state':'checking' if configured else 'configuration_mismatch','configured':configured,
            'server':host,'source':source,'peers':peers,'mode':mode,
            'source_matches':source.lower().split(',')[0]==host.lower(),
            'os_synchronized':synchronized,'native_status':native_status}


def configure(host):
    host=host_from_input(host)
    if os.name!='nt':raise ValueError('Windows Time configuration requires a Windows server')
    try:existing=inspect(host)
    except (ValueError,OSError,subprocess.SubprocessError):existing={}
    if existing.get('configured'):return existing
    if not is_admin():
        launcher=Path(__file__).with_name('request-windows-time.ps1')
        result=subprocess.run(['powershell.exe','-NoProfile','-ExecutionPolicy','Bypass','-File',str(launcher),'-Server',host],capture_output=True,text=True,timeout=180,creationflags=getattr(subprocess,'CREATE_NO_WINDOW',0))
        if result.returncode:raise ValueError('Windows time setup was not approved or could not complete. Approve the Windows UAC prompt on the mount computer once, then retry Next.')
        configured=inspect(host)
        if not configured.get('configured'):raise ValueError('Windows did not retain the selected time server')
        return configured
    # Native argument arrays: the tested hostname never becomes shell code.
    run('sc.exe','config','w32time','start=','auto')
    try:run('sc.exe','start','w32time')
    except ValueError as exc:
        if '1056' not in str(exc):raise
    run('w32tm','/config',f'/manualpeerlist:{host},0x8','/syncfromflags:manual','/update')
    run('w32tm','/resync','/rediscover')
    result=inspect(host)
    if not result['configured']:raise ValueError('Windows did not retain the selected time server')
    return result


class ClockMonitor:
    def __init__(self, workbench, observatory):
        self.workbench=workbench
        self.observatory=observatory
        self.status={'state':'not_configured'}

    async def check(self):
        saved=self.workbench.ntp or {}
        host=saved.get('host')
        if not host:self.status={'state':'not_configured'};return
        try:
            result=await asyncio.to_thread(inspect,host)
            # Compare the OS source with the selected hostname or tested IP.
            result['source_matches']=result.get('source_matches',False) or result.get('source')==saved.get('address')
            if result.get('configured') and result['source_matches']:
                from .ntp import probe
                measurement=await probe(host)
                result['measurement']=measurement
                result['state']='synchronized' if result.get('os_synchronized') and measurement.get('ok') and abs(measurement['offset_ms'])<=max(10,measurement['estimated_error_ms']) else 'sync_unconfirmed'
            elif result.get('configured'):result['state']='source_mismatch'
            self.status=result
        except (ValueError,OSError,subprocess.SubprocessError) as exc:
            self.status={'state':'error','server':host,'message':str(exc)}

    async def run(self):
        previous=None
        while True:
            await self.check()
            if (self.workbench.ntp or {}).get('windows_time_configured') and self.status['state'] not in ('synchronized','not_configured'):
                if is_admin() and not self.observatory.tracking and not self.observatory.camera.busy:
                    try:
                        await asyncio.to_thread(configure,self.workbench.ntp['host'])
                        await self.check()
                    except (ValueError,OSError,subprocess.SubprocessError) as exc:
                        self.status={'state':'error','server':self.workbench.ntp['host'],'message':str(exc)}
            state=self.status['state']
            if state!=previous and state not in ('synchronized','not_configured'):
                self.observatory.mount.event('warning','Server clock synchronization',self.status.get('message',state))
            previous=state
            await asyncio.sleep(60)
