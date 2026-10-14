"""Read-only Bosch JPEG live view and bounded private-LAN discovery."""
import concurrent.futures
import ipaddress
import json
import urllib.request
import urllib.error
import threading

class CCTV:
    def __init__(self,directory):
        self.path=directory/'cctv.json';self.lock=threading.Lock();self.password=''
        self.config={'host':'','scheme':'http','channel':1,'username':''}
        if self.path.exists():self.config.update(json.loads(self.path.read_text()))

    def configure(self,values):
        host=values['host'].strip()
        ip=ipaddress.IPv4Address(host)
        if not ip.is_private or ip.is_loopback or ip.is_multicast or ip.is_unspecified:raise ValueError('Use the private IP address of the CCTV camera')
        with self.lock:
            self.config={k:values[k] for k in ('host','scheme','channel','username')}
            self.config['host']=host;self.password=values.get('password','')
            self.path.write_text(json.dumps(self.config))
        return self.config

    def snapshot(self):
        with self.lock:config=dict(self.config);password=self.password
        if not config['host']:raise ValueError('Select a CCTV camera first')
        url=f"{config['scheme']}://{config['host']}/snap.jpg?jpegCam={config['channel']}"
        manager=urllib.request.HTTPPasswordMgrWithDefaultRealm();manager.add_password(None,url,config['username'],password)
        opener=urllib.request.build_opener(urllib.request.ProxyHandler({}),urllib.request.HTTPDigestAuthHandler(manager),urllib.request.HTTPBasicAuthHandler(manager))
        try:
            with opener.open(url,timeout=4) as response:
                image=response.read(8*1024*1024+1)
                if len(image)>8*1024*1024 or not image.startswith(b'\xff\xd8'):raise ValueError('Camera did not return a JPEG live image')
                return image
        except urllib.error.HTTPError as exc:
            if exc.code in (401,403):raise ValueError('Camera requires a valid username and password') from None
            raise ValueError(f'Camera returned HTTP {exc.code}') from None
        except (urllib.error.URLError,TimeoutError):raise ValueError('Cannot reach the CCTV camera from the controller') from None

    @staticmethod
    def scan(subnet):
        network=ipaddress.ip_network(subnet,strict=False)
        if network.version!=4 or network.prefixlen!=24 or not network.is_private:raise ValueError('Enter a private /24 subnet, for example 192.168.70.0/24')
        def probe(ip):
            opener=urllib.request.build_opener(urllib.request.ProxyHandler({}))
            try:
                with opener.open('http://'+str(ip)+'/',timeout=.7) as r:
                    text=r.read(8192).decode(errors='replace');server=r.headers.get('Server','');status=r.status
            except urllib.error.HTTPError as e:text='';server=e.headers.get('Server','')+' '+e.headers.get('WWW-Authenticate','');status=e.code
            except (OSError,urllib.error.URLError):return None
            bosch='bosch' in (text+' '+server).lower()
            return {'host':str(ip),'status':status,'bosch':bosch,'label':('Bosch camera' if bosch else 'Web device (unverified)')+' · '+str(ip)}
        with concurrent.futures.ThreadPoolExecutor(max_workers=24) as pool:
            devices=[d for d in pool.map(probe,network.hosts()) if d]
        return {'subnet':str(network),'devices':devices}
