import asyncio
import pytest
from astropy.time import Time
from mount_service.setpoint_generator import SetPointGenerator


@pytest.mark.parametrize('spec,kind',[
    ({'ra_hours':3,'dec_deg':20},'celestial'),
    ({'body':'moon'},'solar_system'),
    ({'body':'mercury'},'solar_system'),
    ({'tle':{'line1':'test','line2':'test'}},'satellite'),
    ({'orbit':{'eccentricity':.2}},'small_body'),
])
def test_all_objects_use_server_clock_and_same_model_pipeline(spec,kind):
    now=Time('2026-10-13T00:00:00',scale='utc')
    samples=[]
    async def preview(payload):
        samples.append(payload)
        elapsed=(Time(payload['utc'])-now).sec
        return {'encoder_azimuth_deg':(359.99+2*elapsed)%360,
                'encoder_altitude_deg':40-.5*elapsed,'slew_allowed':True,
                'site':{'latitude':18,'longitude':99,'elevation':350},'model_applied':True}
    target,rates=asyncio.run(SetPointGenerator(lambda:now,preview).generate({**spec,'utc':'2000-01-01'}))
    assert samples[0]['utc']==now.utc.isot
    assert (Time(samples[1]['utc'])-now).sec==pytest.approx(.1)
    assert all({k:v for k,v in s.items() if k!='utc'}==spec for s in samples)
    assert rates==pytest.approx({'azimuth':2,'altitude':-.5})
    assert target['model_applied']
    assert target['set_point_generator']['object_type']==kind


@pytest.mark.parametrize('allowed,position',[(False,40),(True,float('nan'))])
def test_invalid_trajectory_is_rejected(allowed,position):
    async def preview(payload):
        return {'slew_allowed':allowed,'encoder_azimuth_deg':position,'encoder_altitude_deg':40}
    with pytest.raises(ValueError):
        asyncio.run(SetPointGenerator(lambda:Time('2026-10-13'),preview).generate({'body':'sun'}))
