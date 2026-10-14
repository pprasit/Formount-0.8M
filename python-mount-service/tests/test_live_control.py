import asyncio
import time
from pathlib import Path

import pytest

from mount_service.command_lane import CommandLane
from mount_service.domain import AXIS_MAP, DriveError, Reading
from mount_service.transports import SerialElmoDrive
from mount_service.astronomy import Astronomy
from mount_service.pointing import PointingModel


def test_stop_is_reserved_even_when_command_queue_is_full():
    lane=CommandLane();executed=[]
    old=[lane.submit(lambda:executed.append('move')) for _ in range(128)]
    stop=lane.submit(lambda:executed.append('stop'),priority=0,invalidate=True)
    lane.execute(200)
    assert executed==['stop'] and stop.result() is None
    assert all(isinstance(f.exception(),DriveError) for f in old)


def test_second_axis_stop_never_cancels_pending_stop_all():
    lane=CommandLane();executed=[]
    first=lane.submit(lambda:executed.append('all'),priority=0,invalidate=True)
    second=lane.submit(lambda:executed.append('axis'),priority=0,invalidate=True)
    lane.execute()
    assert executed==['all','axis'] and first.done() and second.done()


def fake_drive(axis):
    drive=SerialElmoDrive('NOT-A-REAL-PORT',921600)
    drive.unit_modes[axis.id]=5
    drive._motor_states[axis.id]=True
    drive.latest[axis.id]=Reading(axis.counts_to_deg(-axis.counts_per_rev*.1),0,0,True,time.perf_counter(),-axis.counts_per_rev*.1)
    commands=[]
    drive._query_sync=lambda command:commands.append(command) or (str(axis.counts_per_rev*100/360) if command.endswith('VH[2]') else '0')
    async def job(fn,**kw):return fn()
    drive._job=job
    return drive,commands


def test_goto_uses_signed_absolute_target_without_adding_encoder_turn():
    axis=AXIS_MAP['rotator'];drive,commands=fake_drive(axis)
    asyncio.run(drive.goto(axis,300,1))
    raw=int(next(c.split('=')[1] for c in commands if '.PA=' in c))
    assert raw==axis.deg_to_counts(300)
    assert axis.counts_to_deg(raw)==pytest.approx(300,abs=1e-6)
    assert commands[-1]=='AX3.BG'


def test_serial_deadman_retries_a_failed_stop_until_acknowledged():
    axis=AXIS_MAP['m3'];drive,commands=fake_drive(axis)
    asyncio.run(drive.velocity(axis,.5,.1))
    drive.leases[axis.id]=time.perf_counter()-1
    def failed(command):raise DriveError('missing acknowledgement')
    drive._query_sync=failed;drive._check_leases()
    assert axis.id in drive.leases and drive.commands[axis.id]==.5
    drive._query_sync=lambda command:commands.append(command) or (str(axis.counts_per_rev*100/360) if command.endswith('VH[2]') else '0')
    drive._check_leases()
    assert axis.id not in drive.leases and drive.commands[axis.id]==0


def test_stale_telemetry_blocks_actual_command_sequence():
    axis=AXIS_MAP['rotator'];drive,commands=fake_drive(axis)
    drive.latest[axis.id].monotonic_at-=1
    with pytest.raises(DriveError):asyncio.run(drive.goto(axis,100,1))
    assert commands==[]


def test_astronomy_coordinates_roundtrip_and_real_catalog():
    astro=Astronomy();utc='2026-10-08T12:00:00'
    sky=astro.from_encoder(60,20,utc)
    target=astro.target(sky['ra_hours'],sky['dec_deg'],utc=utc)
    assert target['azimuth_deg']==pytest.approx(60,abs=1e-6)
    assert target['altitude_deg']==pytest.approx(20,abs=1e-6)
    assert len(astro.catalog)>9000


def test_vendor_fit_export_reload_and_inverse_with_test_only_points():
    astro=Astronomy();model=PointingModel(astro);utc='2026-10-08T12:00:00'
    points=[];path=None
    # Explicit offline test fixtures; never written into operator calibration points.
    for i,(az,alt) in enumerate([(60,20),(100,35),(150,65),(210,45),(280,30),(320,70),
                                 (350,55),(80,75),(240,25),(300,50),(180,40),(120,50)]):
        sky=astro.from_encoder(az,alt,utc)
        points.append(dict(name=f'Offline test {i}',solved_ra_hours=sky['ra_hours'],solved_dec_deg=sky['dec_deg'],
                           encoder_azimuth_deg=az,encoder_altitude_deg=alt,capture_utc=utc))
    try:
        fitted=model.fit(points);path=Path(fitted['path'])
        assert fitted['used']==12 and fitted['rms_arcsec']<1
        assert 'Model Terms:' in path.read_text()
        corrected=model.corrected(60,20)
        model.load(str(path))
        assert model.corrected(60,20)==pytest.approx(corrected,abs=1e-7)
        target=astro.target(points[0]['solved_ra_hours'],points[0]['solved_dec_deg'],utc=utc)
        assert model.encoder_target(target)==pytest.approx((60,20),abs=.001)
    finally:
        if path:path.unlink(missing_ok=True)

def test_manual_go_uses_native_velocity_limit_and_preserves_acceleration():
    axis=AXIS_MAP['rotator'];drive,commands=fake_drive(axis)
    def query(command):
        commands.append(command)
        return '123456.75' if command.endswith('.VH[2]') else '0'
    drive._query_sync=query
    asyncio.run(drive.goto(axis,300,None))
    assert 'AX3.VH[2]' in commands
    assert 'AX3.SP=123456.75' in commands
    assert commands[-1]=='AX3.BG'
    assert not any('.AC' in c or '.DC' in c for c in commands)

def test_goto_speed_does_not_send_binary_float_tail():
    axis=AXIS_MAP["m3"];drive,commands=fake_drive(axis)
    asyncio.run(drive.goto(axis,150,10))
    assert "AX4.SP=7281.77778" in commands
    assert not any("779999" in c for c in commands)


@pytest.mark.parametrize('velocity',[10,-10,1,-1,.5,-.5])
def test_m3_position_jog_sends_integer_native_counts(velocity):
    axis=AXIS_MAP['m3'];drive,commands=fake_drive(axis)
    original=drive._query_sync
    def integer_only(command):
        if '.JP=' in command and not command.split('=')[1].lstrip('-').isdigit():
            raise DriveError('Elmo rejects fractional JP')
        return original(command)
    drive._query_sync=integer_only
    asyncio.run(drive.velocity(axis,velocity,.8))
    expected=int(drive.velocity_units(axis,velocity))
    assert commands==['AX4.VH[2]',f'AX4.JP={expected}','AX4.BG']
    assert drive.commands['m3']==velocity


def test_fractional_velocity_mode_jv_preserved():
    axis=AXIS_MAP['m3'];drive,commands=fake_drive(axis)
    drive.unit_modes['m3']=2
    asyncio.run(drive.velocity(axis,.5,.8))
    assert commands[1]==f'AX4.JV={drive.velocity_units(axis,.5):.17g}'


def test_jog_below_one_native_count_rejected_before_motion():
    axis=AXIS_MAP['m3'];drive,commands=fake_drive(axis)
    with pytest.raises(DriveError,match='below one encoder count'):
        asyncio.run(drive.velocity(axis,.000001,.8))
    assert commands==['AX4.VH[2]']
    assert 'm3' not in drive.leases


@pytest.mark.parametrize("role", ["rotator", "m3"])
@pytest.mark.parametrize("degrees", [-360.001, -0.001, 0, 0.001, 359.999, 360.001, 720.001])
def test_encoder_positions_preserve_sign_and_full_turns(role, degrees):
    axis = AXIS_MAP[role]
    drive = SerialElmoDrive("OFFLINE", 921600)
    counts = degrees * axis.counts_per_rev / 360
    assert axis.counts_to_deg(counts) == pytest.approx(degrees)
    assert drive.position_degrees(axis, counts) == pytest.approx(degrees)
    assert drive.position_units(axis, degrees) == round(counts)


@pytest.mark.parametrize("role", ["rotator", "m3"])
def test_negative_goto_is_not_shifted_to_previous_turn(role):
    axis = AXIS_MAP[role]
    drive, commands = fake_drive(axis)
    asyncio.run(drive.goto(axis, -.01, 1))
    raw = float(next(c.split("=")[1] for c in commands if ".PA=" in c))
    assert raw == axis.deg_to_counts(-.01)


@pytest.mark.parametrize("role",["azimuth","altitude","rotator","m3"])
def test_jog_uses_fresh_native_limit_instead_of_static_profile(role):
    axis=AXIS_MAP[role]
    drive,commands=fake_drive(axis)
    ceiling=50
    drive._query_sync=lambda c:commands.append(c) or str(drive.velocity_units(axis,ceiling))
    asyncio.run(drive.velocity(axis,50,.8))
    assert any(c.endswith(".BG") for c in commands)
    commands.clear()
    ceiling=8
    with pytest.raises(DriveError,match="exceeds drive limit"):
        asyncio.run(drive.velocity(axis,10,.8))
    assert not any("=" in c or c.endswith(".BG") for c in commands)


@pytest.mark.parametrize("role",["azimuth","altitude","rotator","m3"])
@pytest.mark.parametrize("side",["lower","upper"])
def test_limit_warning_identifies_axis_boundary_and_retreat(role,side):
    from mount_service.domain import travel_limit_message
    axis=AXIS_MAP[role]
    message=travel_limit_message(axis,side,-300 if side=="lower" else 300)
    assert axis.name in message
    assert side.title() in message
    assert "Motion stopped" in message and "opposite direction" in message
    assert "telemetry" not in message
