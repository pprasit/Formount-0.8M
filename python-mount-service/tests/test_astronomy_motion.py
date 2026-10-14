import asyncio
from types import SimpleNamespace
from unittest.mock import AsyncMock
from astropy.time import Time
import pytest
from mount_service.observatory import Observatory
from mount_service.transports import SerialElmoDrive


def prepared(az=316, alt=64, position=-35):
    o=object.__new__(Observatory)
    drive=SerialElmoDrive('MOCK',921600)
    drive.travel_limits=lambda axis: (-290,290) if axis.id=='azimuth' else (10,80)
    drive.refresh_position_limits=AsyncMock(side_effect=lambda axis:drive.travel_limits(axis))
    state={'axes':[{'id':'azimuth','position_deg':position,'enabled':True,'valid':True,'velocity_deg_s':0},{'id':'altitude','position_deg':alt,'enabled':True,'valid':True,'velocity_deg_s':0}]}
    o.mount=SimpleNamespace(transport=drive,snapshot=lambda:state,goto_pair=AsyncMock(),stop_all=AsyncMock(),command=AsyncMock())
    o.astro=SimpleNamespace(time=lambda:Time('2026-10-09T06:00:00'),target=lambda **payload:dict(azimuth_deg=az,altitude_deg=alt,above_horizon=alt>0))
    o.pointing=SimpleNamespace(encoder_target=lambda t:(t['azimuth_deg'],t['altitude_deg']))
    async def call(fn,*args):return fn(*args)
    o.call=call;o.model_status={};o.tracking=False;o.track_task=None;o.target=None
    o.tracking_velocity_limits=AsyncMock(return_value={'azimuth':20,'altitude':10})
    o.status=lambda:{'tracking':o.tracking}
    return o


def test_signed_equivalent_and_drive_velocity_defaults():
    async def run():
        o=prepared()
        result=await o.goto({})
        assert result['encoder_azimuth_deg']==-44
        o.mount.goto_pair.assert_awaited_once_with(-44,64)
        assert o.mount.transport.refresh_position_limits.await_count==2
    asyncio.run(run())


def test_preview_checks_drive_altitude_limits():
    async def run():
        o=prepared(alt=85)
        assert not (await o.preview({}))['slew_allowed']
        with pytest.raises(ValueError,match='altitude limits'):await o.goto({})
        o.mount.goto_pair.assert_not_awaited()
    asyncio.run(run())


def test_tracking_accepts_signed_position_and_stops():
    async def run():
        o=prepared(position=-44)
        async def loop(payload):await asyncio.Event().wait()
        o._track_loop=loop
        assert (await o.start_tracking({}))['tracking']
        await o.stop_tracking()
        assert not o.tracking
    asyncio.run(run())


def test_tracking_starts_rates_without_slew_or_position_correction():
    async def run():
        o=prepared(position=-35)
        async def loop(payload):await asyncio.Event().wait()
        o._track_loop=loop
        o.tracking_rates=AsyncMock(return_value=({'slew_allowed':True,'encoder_azimuth_deg':-44,'encoder_altitude_deg':64},{'azimuth':.004,'altitude':-.002}))
        await o.start_tracking({})
        assert o.tracking
        assert o.mount.command.await_args_list[0].args[0:2] == ('azimuth','jog')
        assert o.mount.command.await_args_list[0].args[2] < -10
        assert o.mount.command.await_args_list[1].args[2] == pytest.approx(-.0026)
        o.mount.goto_pair.assert_not_awaited()
        await o.stop_tracking()
    asyncio.run(run())


def test_tracking_derivative_uses_same_timestamp_and_short_wrap():
    async def run():
        o=prepared()
        o.preview=AsyncMock(side_effect=[{'encoder_azimuth_deg':359.999,'encoder_altitude_deg':40,'slew_allowed':True},{'encoder_azimuth_deg':0.003,'encoder_altitude_deg':40.002,'slew_allowed':True}])
        _,rates=await o.tracking_rates({})
        assert rates['azimuth']==pytest.approx(.004)
        assert rates['altitude']==pytest.approx(.002)
        calls=o.preview.await_args_list
        assert (Time(calls[1].args[0]['utc'])-Time(calls[0].args[0]['utc'])).sec==pytest.approx(1)
    asyncio.run(run())


def test_tracking_rejects_disabled_axis_before_either_rate():
    async def run():
        o=prepared()
        o.mount.snapshot()['axes'][1]['enabled']=False
        with pytest.raises(ValueError,match='enabled'):await o.send_tracking_rates({'azimuth':.004,'altitude':.002})
        o.mount.command.assert_not_awaited()
    asyncio.run(run())


def test_tracking_partial_command_failure_stops_both():
    async def run():
        o=prepared()
        o.mount.command=AsyncMock(side_effect=[None,RuntimeError('rejected')])
        with pytest.raises(RuntimeError):await o.send_tracking_rates({'azimuth':.004,'altitude':.002})
        o.mount.stop_all.assert_awaited_once()
    asyncio.run(run())
