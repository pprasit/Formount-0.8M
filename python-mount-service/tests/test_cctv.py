from mount_service.cctv import CCTV
import pytest
import json
from unittest.mock import patch

def test_camera_mapping_does_not_persist_password(tmp_path):
    c=CCTV(tmp_path)
    result=c.configure({'host':'192.168.70.20','scheme':'http','channel':1,'username':'viewer','password':'test-secret'})
    assert 'password' not in result
    assert 'test-secret' not in c.path.read_text()
    assert c.password=='test-secret'
    with pytest.raises(ValueError):c.configure({'host':'8.8.8.8','scheme':'http','channel':1,'username':'','password':''})

def test_scan_rejects_unbounded_or_public_subnets():
    for subnet in ['192.168.0.0/16','8.8.8.0/24']:
        with pytest.raises(ValueError):CCTV.scan(subnet)

def test_snapshot_does_not_accept_a_login_page_as_video(tmp_path):
    c=CCTV(tmp_path);c.configure({'host':'192.168.70.20','scheme':'http','channel':1,'username':'','password':''})
    from unittest.mock import MagicMock
    response=MagicMock();response.__enter__.return_value.read.return_value=b'<html>login</html>'
    with patch('urllib.request.OpenerDirector.open',return_value=response):
        with pytest.raises(ValueError,match='JPEG'):c.snapshot()
