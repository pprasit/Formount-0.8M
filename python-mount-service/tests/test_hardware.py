import asyncio
from types import SimpleNamespace
import pytest
import math
from mount_service.domain import DriveError
from mount_service.hardware import HardwareWorkbench, validate
from mount_service.transports import SerialElmoDrive

@pytest.mark.parametrize('command,requested,expected',[('VL[3]',-72817.77777777778,0.),('VH[3]',291271.1111111111,300000.)])
def test_fractional_m3_limit_is_encoded_at_encoder_resolution_without_flash(command,requested,expected):
    w,commands,values=workbench()
    values.update({'AX4.MO':'0','AX4.VL[3]':'0','AX4.VH[3]':'300000'})
    result=asyncio.run(w.write('m3',command,requested,expected))
    rounded=math.ceil(requested) if command=='VL[3]' else math.floor(requested)
    assert f'AX4.{command}={rounded}' in commands
    assert result['value']==rounded and not result['saved_to_flash']
    assert 'SV' not in commands


def workbench():
    drive=SerialElmoDrive('OFFLINE', 921600)
    drive.connected=True
    commands=[]
    values={'AX1.MO':'0','AX1.MP[1]':'0.5','AX1.CL[1]':'2','AX1.MC[4]':'5'}
    def query(command, **kwargs):
        commands.append(command)
        if '=' in command:
            key,value=command.split('=');values[key]=value;return '0'
        return values.get(command,'0')
    drive._query_sync=query
    async def job(fn, **kwargs):return fn()
    drive._job=job
    mount=SimpleNamespace(transport=drive,_lock=asyncio.Lock(),event=lambda *args:None)
    w=HardwareWorkbench(mount)
    w.audit=lambda record:None
    return w,commands,values


def test_write_checks_motor_off_and_stale_value_before_mutation():
    w,commands,values=workbench()
    values['AX1.MO']='1'
    with pytest.raises(DriveError,match='Disable'):
        asyncio.run(w.write('azimuth','MP[1]',.75,.5))
    assert not any('=' in c for c in commands)
    values['AX1.MO']='0';values['AX1.MP[1]']='.6'
    with pytest.raises(DriveError,match='changed'):
        asyncio.run(w.write('azimuth','MP[1]',.75,.5))
    assert not any('=' in c for c in commands)


def test_verified_edit_is_axis_scoped_and_not_saved_automatically():
    w,commands,_=workbench()
    result=asyncio.run(w.write('azimuth','MP[1]',.75,.5))
    assert commands==['AX1.MO','AX1.MP[1]','AX1.MP[1]=0.75','AX1.MP[1]']
    assert result['value']==.75 and not result['saved_to_flash']


def test_dynamic_hardware_current_ceiling_is_enforced():
    w,commands,_=workbench()
    with pytest.raises(DriveError,match='ceiling'):
        asyncio.run(w.write('azimuth','CL[1]',6.,2.))
    assert not any('=' in c for c in commands)


@pytest.mark.parametrize('command,value',[('MO',1.),('CA[18]',5.),('MP[1];MO',1.),('CA[17]',6.),('CA[7]',4096.),('MP[1]',float('nan')),('MP[4]',1.5)])
def test_catalogue_rejects_unsupported_commands_and_values(command,value):
    with pytest.raises(DriveError):validate(command,value)


def test_save_checks_every_axis_and_uses_global_sv():
    w,commands,values=workbench()
    values['AX4.MO']='1'
    with pytest.raises(DriveError,match='All four'):
        asyncio.run(w.save())
    assert 'SV' not in commands
    values['AX4.MO']='0';commands.clear()
    for n in range(1,5):
        values.update({f'AX{n}.FC[{i}]':'1' for i in range(1,13)})
        values.update({f'AX{n}.VH[2]':'1000000',f'AX{n}.UM':'5',f'AX{n}.VL[3]':'-1000',f'AX{n}.VH[3]':'1000'})
    assert asyncio.run(w.save())['acknowledged']
    assert commands[:5]==['AX1.MO','AX2.MO','AX3.MO','AX4.MO','SV']
    assert 'AX1.FC[1]' in commands


def test_scaling_ratio_matches_manual_and_raw_feedback_is_independent():
    from mount_service.domain import AXIS_MAP
    drive=SerialElmoDrive('OFFLINE',921600)
    axis=AXIS_MAP['azimuth']
    drive.resolutions[axis.id]=36000
    drive.scaling[axis.id]=[100,1,1,1,2,1,360,1,10,1,1,1]
    # 100 counts/motor rev, 2 motor rev/axis rev, 360 user units/axis rev.
    assert drive.unit_factors(axis)==(1.8,18.)
    drive.azimuth_position.update(0,0)
    assert drive.position_units(axis,90)==16200
    assert drive.velocity_units(axis,2)==3600
    assert drive.position_degrees(axis,9000)==90


def test_mode_write_updates_motion_mode_cache():
    w,commands,values=workbench();values['AX1.UM']='5'
    asyncio.run(w.write('azimuth','UM',2.,5.))
    assert w.mount.transport.unit_modes['azimuth']==2
    assert not w.mount.transport.configuration_errors


def test_uncertain_scaling_write_blocks_future_motion():
    w,commands,values=workbench();values['AX1.FC[1]']='1'
    original=w.mount.transport._query_sync
    def query(command,**kwargs):
        if '=' in command:raise DriveError('Lost acknowledgement')
        return original(command,**kwargs)
    w.mount.transport._query_sync=query
    with pytest.raises(DriveError):asyncio.run(w.write('azimuth','FC[1]',2.,1.))
    assert w.mount.transport.configuration_errors['azimuth']


def test_sensor_global_write_preserves_unrelated_flags():
    w,commands,values=workbench();values.update({'S1[1]':'5','S1[2]':'4096','S1[7]':'32'})
    result=asyncio.run(w.sensor_write('azimuth',1,'S1_REVERSE',1.,0.))
    assert result['value']==1
    assert values['S1[2]']=='4097'
    assert not any(c.startswith('AX1.S1') for c in commands)


def test_shared_sensor_edit_refuses_if_another_motor_is_enabled():
    w,commands,values=workbench();values['AX4.MO']='1'
    with pytest.raises(DriveError,match='all four'):
        asyncio.run(w.sensor_write('azimuth',1,'S1[7]',31.,32.))
    assert not any('=' in c for c in commands)


def test_commutation_writes_are_paused_but_encoder_resolution_is_editable():
    assert validate('CA[18]',2.**32)['writable']
    for command in ['CA[7]','CA[16]','CA[17]','CA[20]','CA[25]']:
        with pytest.raises(DriveError):validate(command,0.)


def test_mount_resolution_persists_separately_without_sending_fake_elmo_command(tmp_path,monkeypatch):
    import json
    import mount_service.hardware as hardware
    from mount_service.domain import AXIS_MAP
    w,commands,_=workbench();w.mount.config={'port':'OFFLINE'}
    monkeypatch.setattr(hardware,'__file__',str(tmp_path/'mount_service'/'hardware.py'))
    axis=AXIS_MAP['azimuth']
    result=asyncio.run(w.write('azimuth','MOUNT_CPR',65536.,float(axis.counts_per_rev)))
    assert result['value']==65536
    assert commands==['AX1.MO']
    assert w.mount.transport.resolutions['azimuth']==65536
    assert json.loads((tmp_path/'config.json').read_text())['axis_resolutions']=={'azimuth':65536}

@pytest.mark.parametrize('command,value,expected',[('VL[3]',20.,-10.),('VH[3]',-20.,10.)])
def test_position_limit_order_blocks_write(command,value,expected):
    w,commands,values=workbench()
    values.update({'AX1.VL[3]':'-10','AX1.VH[3]':'10'})
    with pytest.raises(DriveError,match='CCW limit'):
        asyncio.run(w.write('azimuth',command,value,expected))
    assert not any('=' in c for c in commands)

@pytest.mark.parametrize('command,value,expected',[('VL[3]',-5.,-10.),('VH[3]',5.,10.),('VH[2]',2.,0.),('AC[1]',3.,0.),('DC',4.,0.)])
def test_limit_edit_readback_is_verified(command,value,expected):
    w,commands,values=workbench()
    values.update({'AX1.VL[3]':'-10','AX1.VH[3]':'10'})
    result=asyncio.run(w.write('azimuth',command,value,expected))
    assert result['value']==value
    assert not result['saved_to_flash']
