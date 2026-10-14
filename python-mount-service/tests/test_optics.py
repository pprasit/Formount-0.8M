import json
import asyncio
import pytest
from test_mapped_hardware import setup
from mount_service.optics import OpticsControl
from mount_service.domain import DriveError

def optics(tmp_path):
    topology,hardware=setup(tmp_path)
    for role in ('focuser1','focuser2','rotator2'):
        n=topology.active_mapping[role]['channel'];prefix=f'AX{n}.'
        topology.port.values.update({prefix+'PX':200,prefix+'MO':1,prefix+'UM':5,prefix+'VL[3]':0,prefix+'VH[3]':1000,prefix+'VH[2]':500})
        topology.port.values.update({prefix+f'FC[{i}]':1 for i in range(1,13)})
    return topology,OpticsControl(topology.mount,hardware,tmp_path)

def test_move_uses_mapped_drive_and_native_scaling(tmp_path):
    t,o=optics(tmp_path);t.port.values['AX1.FC[2]']=2
    result=asyncio.run(o.command('focuser1','goto',step=10,speed=50,target=o.hardware.binding('focuser1')))
    assert result['target']==210
    assert t.port.commands[-4:]==['AX1.ST','AX1.SP=100','AX1.PA=420','AX1.BG']
    assert not t.mount.transport._port.commands

@pytest.mark.parametrize('change,value,speed', [('AX1.MO',300,50),('AX1.UM',300,50),('AX1.VH[3]',300,50)])
def test_invalid_axis_configuration_never_starts(tmp_path,change,value,speed):
    t,o=optics(tmp_path);t.port.values[change]=0
    with pytest.raises(DriveError):asyncio.run(o.command('focuser1','goto',value=value,speed=speed,target=o.hardware.binding('focuser1')))
    assert not any('=' in c or c.endswith('.BG') for c in t.port.commands)

@pytest.mark.parametrize('value,speed',[(1001,50),(300,501)])
def test_targets_and_speeds_respect_drive_limits(tmp_path,value,speed):
    t,o=optics(tmp_path)
    with pytest.raises(DriveError):asyncio.run(o.command('focuser1','goto',value=value,speed=speed,target=o.hardware.binding('focuser1')))
    assert not any('=' in c or c.endswith('.BG') for c in t.port.commands)

def test_stale_mapping_rejected_before_motion(tmp_path):
    t,o=optics(tmp_path);target=o.hardware.binding('focuser1');t.active_mapping['focuser1']['channel']=4
    with pytest.raises(DriveError):asyncio.run(o.command('focuser1','goto',value=300,speed=50,target=target))
    assert t.port.commands==[]

def test_m3_presets_persist_without_hardware_motion(tmp_path):
    t,o=optics(tmp_path);o.configure_m3(300)
    assert OpticsControl(t.mount,o.hardware,tmp_path).settings['m3_port1']==300
    assert (o.settings['m3_port1']+180)%360==120
    assert not t.port.commands and not any('=' in c or c=='SV' for c in t.mount.transport._port.commands)

def test_stop_all_accessories_uses_each_mapped_channel(tmp_path):
    t,o=optics(tmp_path);asyncio.run(o.stop_accessories())
    assert t.port.commands==['AX1.ST','AX2.ST','AX3.ST']


def test_pending_mapping_does_not_block_stop_on_active_channel(tmp_path):
    t,o=optics(tmp_path)
    import copy
    t.axis_mapping=copy.deepcopy(t.active_mapping)
    t.axis_mapping['focuser1']['channel']=4
    asyncio.run(o.command('focuser1','stop',target=o.hardware.binding('focuser1')))
    assert t.port.commands==['AX1.ST']


def test_calibration_persists_and_checks_soft_travel(tmp_path):
    t,o=optics(tmp_path);t.port.values['AX1.MO']=0
    binding=o.hardware.binding('focuser1')
    asyncio.run(o.configure_axis('focuser1',0,.3,.001,binding))
    assert OpticsControl(t.mount,o.hardware,tmp_path).axis_settings('focuser1')['unit_per_count']==.001
    t.port.values['AX1.MO']=1
    asyncio.run(o.command('focuser1','goto',value=250,speed=50,target=binding))
    with pytest.raises(DriveError,match='optics travel'):asyncio.run(o.command('focuser1','goto',value=400,speed=50,target=binding))
    o.configure_m3(330)
    assert o.axis_settings('focuser1')['unit_per_count']==.001

def test_calibration_rejects_enabled_axis_and_invalid_range(tmp_path):
    t,o=optics(tmp_path);binding=o.hardware.binding('focuser1')
    with pytest.raises(DriveError,match='Disable'):asyncio.run(o.configure_axis('focuser1',0,1,.001,binding))
    with pytest.raises(DriveError):asyncio.run(o.configure_axis('focuser1',1,0,.001,binding))
    assert not o.settings.get('axes')

def test_calibration_does_not_follow_new_axis_mapping(tmp_path):
    t,o=optics(tmp_path);t.port.values['AX1.MO']=0
    asyncio.run(o.configure_axis('focuser1',0,1,.001,o.hardware.binding('focuser1')))
    t.active_mapping['focuser1']['channel']=4
    assert o.axis_settings('focuser1') is None


def test_legacy_zero_offset_removed_for_absolute_encoders(tmp_path):
    import json
    t,o=optics(tmp_path)
    config=dict(minimum=0,maximum=1,unit_per_count=.001,zero_count=100,binding=o.hardware.binding('focuser1'))
    o.path.write_text(json.dumps({'m3_port1':330,'axes':{'focuser1':config}}))
    loaded=OpticsControl(t.mount,o.hardware,tmp_path)
    assert 'zero_count' not in loaded.axis_settings('focuser1')
    loaded.check_calibrated_target('focuser1',0)
    with pytest.raises(DriveError):loaded.check_calibrated_target('focuser1',1050)


def test_m3_settings_saved_to_drive_and_flash(tmp_path):
    t,o=optics(tmp_path);port=t.mount.transport._port
    port.values.update({f'AX4.FC[{i}]':1 for i in (1,2,5,6,7,8)})
    port.values.update({'AX4.VL[3]':0,'AX4.VH[3]':0,'AX4.TR[1]':7})
    o.hardware.audit=lambda record:None
    async def run():
        read=await o.m3_settings()
        result=await o.save_m3(330,0,359,.01,read['target'],read['native'],2.5)
        assert OpticsControl(t.mount,o.hardware,tmp_path).settings['m3_jog_speed']==2.5
        assert result['saved_to_flash']
        assert port.values['AX4.VH[3]']==261415
        assert port.values['AX4.TR[1]']==pytest.approx(.01*262144/360)
        assert 'SV' in port.commands
        assert not any(c.endswith('.BG') or c.endswith('MO=1') for c in port.commands)
        assert 'm3_minimum' not in o.settings
        port.values['AX4.MO']=1
        before=len(port.commands)
        with pytest.raises(DriveError,match='Disable'):await o.save_m3(330,0,359,.01,read['target'],read['native'])
        assert not any('=' in c or c=='SV' for c in port.commands[before:])
    asyncio.run(run())

def test_m3_slew_obeys_configured_limits_before_transport_motion():
    from mount_service.service import MountService
    async def run():
        mount=MountService(simulation=True)
        await mount.start()
        try:
            mount.config['m3_position_limits']=[120,340]
            await mount.command('m3','enable')
            with pytest.raises(DriveError,match='limits'):await mount.command('m3','goto',110)
            await mount.command('m3','goto',150)
        finally:await mount.close()
    asyncio.run(run())


def test_m3_removes_legacy_program_limits(tmp_path):
    t,o=optics(tmp_path)
    o.settings.update(m3_minimum=0,m3_maximum=359,m3_in_position_window=.01)
    t.mount.config['m3_position_limits']=[0,359]
    o.path.write_text(json.dumps(o.settings))
    loaded=OpticsControl(t.mount,o.hardware,tmp_path)
    assert 'm3_position_limits' not in t.mount.config
    loaded.configure_m3(330)
    assert 'm3_maximum' not in json.loads(o.path.read_text())


def test_m3_jog_uses_saved_speed_direction_and_deadman_lease(tmp_path):
    t,o=optics(tmp_path);calls=[]
    async def command(*args):calls.append(args);return {'ok':True}
    t.mount.command=command
    o.settings['m3_jog_speed']=2.5
    target=o.hardware.binding('m3')
    asyncio.run(o.command('m3','jog',value=1,target=target,lease=.8))
    asyncio.run(o.command('m3','jog',value=-1,target=target,lease=.8))
    asyncio.run(o.command('m3','goto',value=150,target=target))
    assert calls[0]==('m3','jog',2.5,None,.8)
    assert calls[1]==('m3','jog',-2.5,None,.8)
    assert calls[2]==('m3','goto',150,2.5,.8)
    with pytest.raises(DriveError,match='direction'):
        asyncio.run(o.command('m3','jog',value=2,target=target))
    assert len(calls)==3


def test_m3_speed_rejected_before_hardware_write(tmp_path):
    t,o=optics(tmp_path)
    t.mount.transport._port.values['AX4.VH[2]']=10*262144/360
    with pytest.raises(DriveError,match='velocity limit'):
        asyncio.run(o.save_m3(330,0,359,.01,o.hardware.binding('m3'),{},11))
    assert not t.port.commands and not any('=' in c or c=='SV' for c in t.mount.transport._port.commands)


def test_fast_status_reads_positions_without_reloading_configuration(tmp_path):
    from mount_service.domain import AXES
    t,o=optics(tmp_path)
    t.mount.snapshot=lambda:{'axes':[{**a.export(),'position_deg':0,'valid':True,'enabled':False} for a in AXES],'drive':{'connected':True,'motion_available':True}}
    port=t.mount.transport._port
    port.values.update({f'AX4.FC[{i}]':1 for i in (1,2,5,6,7,8)})
    port.values.update({'AX4.VL[3]':0,'AX4.VH[3]':261415,'AX4.TR[1]':7})
    async def run():
        await o.status(fast=True)
        t.port.commands.clear();port.commands.clear()
        t.port.values['AX1.PX']=321
        result=await o.status(fast=True)
        assert result['axes']['focuser1']['position']==321
        assert t.port.commands==['AX1.PX','AX1.MO','AX2.PX','AX2.MO','AX3.PX','AX3.MO']
        assert port.commands==[]
    asyncio.run(run())


@pytest.mark.parametrize('angle,minimum,maximum,opposite', [(392,100,500,212),(-200,-500,0,-20),(330,0,359,150)])
def test_m3_signed_multiturn_presets(tmp_path,angle,minimum,maximum,opposite):
    t,o=optics(tmp_path)
    port=t.mount.transport._port
    port.values.update({f'AX4.FC[{i}]':1 for i in (1,2,5,6,7,8)})
    port.values.update({'AX4.VL[3]':0,'AX4.VH[3]':0,'AX4.TR[1]':7})
    o.hardware.audit=lambda record:None
    async def run():
        read=await o.m3_settings()
        await o.save_m3(angle,minimum,maximum,.01,read['target'],read['native'],2.5)
        saved=await o.m3_settings()
        assert saved['port1']==angle
        assert saved['port2']==opposite
        assert not any(c.endswith('.BG') or c.endswith('MO=1') for c in port.commands)
    asyncio.run(run())


def test_m3_outside_configured_range_is_rejected_before_write(tmp_path):
    t,o=optics(tmp_path)
    before=list(t.mount.transport._port.commands)
    with pytest.raises(DriveError,match='Minimum and Maximum'):
        asyncio.run(o.save_m3(501,100,500,.01,{},{}))
    assert t.mount.transport._port.commands==before


def test_m3_speed_uses_native_velocity_limit(tmp_path):
    t,o=optics(tmp_path)
    port=t.mount.transport._port
    port.values['AX4.VH[2]']=50*262144/360
    o.hardware.audit=lambda record:None
    async def run():
        read=await o.m3_settings()
        assert read['max_jog_speed']==pytest.approx(50)
        await o.save_m3(392,100,500,.01,read['target'],read['native'],20)
        assert o.settings['m3_jog_speed']==20
        read=await o.m3_settings()
        port.values['AX4.VH[2]']=15*262144/360
        before=len(port.commands)
        with pytest.raises(DriveError,match='velocity limit'):
            await o.save_m3(392,100,500,.01,read['target'],read['native'],20)
        assert not any('=' in c or c=='SV' for c in port.commands[before:])
    asyncio.run(run())
