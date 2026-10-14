import numpy as np
from PIL import Image
from mount_service.camera_preview import stretched_preview,save_preview,PROFILES


def test_preview_is_bounded_preserves_stars_and_raw_pixels(tmp_path):
    raw=np.random.default_rng(42).normal(1000,8,(1601,2603)).clip(0,65535).astype(np.uint16)
    raw[100,200]=16000;raw[-1,-1]=50000
    original=raw.copy()
    preview,settings=stretched_preview(raw)
    values=np.asarray(preview)
    assert preview.width<=1280 and preview.height<=1280
    assert values[100//3,200//3]>240 and values[-1,-1]>240
    assert np.median(values)==0
    np.testing.assert_array_equal(raw,original)
    path=tmp_path/'preview.png';save_preview(raw,path)
    assert path.stat().st_size<raw.nbytes/10
    assert Image.open(path).mode=='L' and settings['profile']=='Auto'


def test_flat_frame_and_profile_presets():
    for profile in PROFILES:
        preview,settings=stretched_preview(np.full((10,10),65535,dtype=np.uint16),profile)
        assert np.asarray(preview).max()==0
        assert settings['white']>=settings['black']
    assert PROFILES['High'][2]>PROFILES['Low'][2]
