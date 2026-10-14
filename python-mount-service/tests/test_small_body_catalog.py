import pytest
from mount_service.small_body_catalog import parse_catalog, sky_frame
from mount_service.astronomy import Astronomy
from mount_service.api import OrbitalElements

RAW={'fields':['spkid','full_name','epoch','e','a','q','i','om','w','ma','tp','H','diameter'],'data':[
[20000001,'1 Ceres','2461200.5','.08','2.77','2.55','10.6','80.3','73.5','40','2461000.1','3.5','939'],
[20000001,'duplicate','2461200.5','.08','2.77','2.55','10.6','80.3','73.5','40','2461000.1',None,None],
[10000001,'invalid hyperbolic','2461200.5','1.2','-5','1','10','20','30','0','2461000.1',None,None]]}

@pytest.mark.parametrize('category',['asteroid','comet'])
def test_real_catalog_fields_map_to_valid_target(category):
    rows=parse_catalog(RAW,category)
    assert len(rows)==1
    validated=OrbitalElements.model_validate(rows[0]['orbit'])
    assert validated.name=='1 Ceres'
    if category=='asteroid':assert validated.semi_major_axis_au==2.77
    else:assert validated.perihelion_epoch_jd==2461000.1


def test_sphere_positions_use_same_model_as_selected_target(monkeypatch):
    from astropy.time import Time
    rows=parse_catalog(RAW,'asteroid')
    monkeypatch.setattr('mount_service.small_body_catalog.catalog',lambda category:{'objects':rows,'category':category})
    astro=Astronomy();now=Time('2026-10-10T02:00:00');monkeypatch.setattr(astro,'time',lambda utc=None:now)
    plotted=sky_frame(astro,'asteroid')['objects'][0]
    target=astro.target(orbit=rows[0]['orbit'])
    assert plotted['azimuth_deg']==pytest.approx(target['azimuth_deg'],abs=1e-8)
    assert plotted['altitude_deg']==pytest.approx(target['altitude_deg'],abs=1e-8)


def test_catalog_rejects_unknown_and_empty():
    with pytest.raises(ValueError):parse_catalog(RAW,'satellite')
    with pytest.raises(ValueError):parse_catalog({'fields':RAW['fields'],'data':[]},'comet')
