import asyncio
import csv
import io
import time

import pytest
from fastapi.testclient import TestClient

from mount_service.api import create_app
from mount_service.domain import AXES, DriveError
from mount_service.service import MountService
from mount_service.transports import SerialElmoDrive, parse_reply


@pytest.fixture
def client():
    with TestClient(create_app(MountService(simulation=True))) as client:
        yield client


def send(client, axis, action, **kwargs):
    return client.post(f"/api/axes/{axis}/command", json={"action": action, **kwargs})


def test_dashboard_and_live_stream(client):
    assert client.get("/").status_code == 200
    assert client.get("/assets/app.js").status_code == 200
    assert client.get("/api/health").json()["mode"] == "simulation"
    with client.websocket_connect("/ws/telemetry") as ws:
        state = ws.receive_json()
        assert [a["number"] for a in state["axes"]] == [1, 2, 3, 4]
        assert all(a["valid"] for a in state["axes"])


def test_limits_and_disabled_axis_block_motion(client):
    assert send(client, "azimuth", "goto", value=701).status_code == 409
    assert send(client, "altitude", "goto", value=-1).status_code == 409
    assert send(client, "rotator", "jog", value=6).status_code == 409
    assert send(client, "m3", "goto", value=100, speed=11).status_code == 409
    assert send(client, "azimuth", "disable").status_code == 200
    assert send(client, "azimuth", "jog", value=1).status_code == 409
    state = send(client, "azimuth", "enable").json()["state"]
    assert state["axes"][0]["mode"] == "idle"
    assert state["axes"][0]["enabled"]


def test_stop_all_clears_every_pending_motion(client):
    send(client, "azimuth", "goto", value=250)
    send(client, "m3", "jog", value=1)
    result = client.post("/api/stop").json()
    assert result["ok"]
    assert all(a["command_velocity"] == 0 and a["target_position"] is None
               for a in result["state"]["axes"])
    assert not client.app.state.mount.deadlines


def test_deadman_stops_without_browser_release(client):
    assert send(client, "rotator", "jog", value=.5, lease=.1).status_code == 200
    time.sleep(.25)
    axis = client.get("/api/state").json()["axes"][2]
    assert axis["mode"] == "idle"
    assert axis["command_velocity"] == 0
    assert axis["velocity_deg_s"] == 0


def test_disconnect_invalidates_immediately_and_reconnect_restores(client):
    state = client.post("/api/disconnect").json()
    assert not state["drive"]["connected"]
    assert all(not a["valid"] for a in state["axes"])
    assert send(client, "azimuth", "jog", value=1).status_code == 409
    state = client.post("/api/connect", json={"mode": "simulation"}).json()
    assert state["drive"]["connected"] and all(a["valid"] for a in state["axes"])


def test_history_and_csv_have_four_channels_and_units(client):
    history = client.get("/api/history?seconds=60").json()["samples"]
    assert len(history) > 500
    assert len(history[0]["axes"]) == 4
    rows = list(csv.DictReader(io.StringIO(client.get("/api/history.csv").text)))
    assert {r["axis"] for r in rows} == {"azimuth", "altitude", "rotator", "m3"}
    assert "position_deg" in rows[0] and "current_a" in rows[0]


def test_history_compression_preserves_samples(client):
    compressed = client.get("/api/history?seconds=120", headers={"Accept-Encoding": "gzip"})
    raw = client.get("/api/history?seconds=120", headers={"Accept-Encoding": "identity"})
    assert compressed.headers["content-encoding"] == "gzip"
    assert "content-encoding" not in raw.headers
    assert len(compressed.json()["samples"]) >= 601
    assert len(raw.json()["samples"]) >= 601
    assert int(compressed.headers["content-length"]) < int(raw.headers["content-length"])


def test_unknown_axis_extra_fields_and_cross_origin_writes_rejected(client):
    assert send(client, "unknown", "stop").status_code == 404
    assert send(client, "m3", "stop", arbitrary="PA=100").status_code == 422
    assert client.post("/api/stop", headers={"Origin": "https://other.example"}).status_code == 403
    assert client.post("/api/connect", json={"mode": "hardware"}).status_code == 409


@pytest.mark.parametrize("axis,degrees", [(AXES[0], 700), (AXES[2], -120), (AXES[3], 90)])
def test_encoder_conversion_preserves_turns(axis, degrees):
    assert axis.counts_to_deg(axis.deg_to_counts(degrees)) == pytest.approx(degrees%360 if axis.wrap else degrees, abs=360 / axis.counts_per_rev)


def test_parser_rejects_partial_error_and_ambiguous_replies():
    assert parse_reply("AX1.PX", "AX1.PX\r\n123;") == "123"
    assert parse_reply("AX4.MO", "0;") == "0"
    for raw in ("123", "?;", "123\r456;"):
        with pytest.raises(DriveError):
            parse_reply("AX1.PX", raw)


def test_hardware_read_maps_axes_and_blocks_motion():
    async def run():
        drive = SerialElmoDrive("COM_TEST", 115200)
        commands = []
        async def query(command):
            commands.append(command)
            return {"PX": str(2**18 / 4), "VX": str(2**18 / 360), "IQ": ".42", "MO": "1"}[command.split(".")[1]]
        drive.query = query
        reading = await drive.read(AXES[3], time.perf_counter())
        assert reading.position_deg == 90
        assert reading.velocity_deg_s == 1
        assert reading.current_a == .42
        assert commands == ["AX4.PX", "AX4.VX", "AX4.IQ", "AX4.MO"]
        with pytest.raises(DriveError):
            await drive.goto(AXES[3], 100, 1)
    asyncio.run(run())


def test_failed_poll_retains_last_good_value_and_marks_invalid():
    async def run():
        service = MountService(simulation=True)
        await service._poll(time.perf_counter())
        old = service.readings["azimuth"].position_deg
        async def failed(*args):
            raise DriveError("Read timed out")
        service.transport.read = failed
        await service._poll(time.perf_counter())
        axis = service.snapshot()["axes"][0]
        assert axis["position_deg"] == old and not axis["valid"]
        assert axis["error"] == "Read timed out"
        with pytest.raises(DriveError):
            await service.command("azimuth", "goto", 200)
    asyncio.run(run())


def test_hardware_worker_owns_high_resolution_frames():
    drive = SerialElmoDrive("COM_TEST", 921600, 5)
    drive._motor_states = {a.id: False for a in AXES}
    drive.digital_inputs = {"1": 0}
    drive._numeric = lambda command: 1 if command.endswith(".PX") else 0
    import threading
    worker = threading.Thread(target=drive._poll_worker)
    worker.start()
    time.sleep(.08)
    drive._halt.set()
    worker.join()
    frames = drive.drain()
    assert len(frames) >= 8
    intervals = [b[0]-a[0] for a, b in zip(frames, frames[1:])]
    assert all(dt > 0 for dt in intervals)
    assert len(frames[0][1]) == 4
    assert all(not r.enabled for r in frames[0][1].values())


def test_production_starts_hardware_only_without_opening_a_port():
    mount = MountService()
    assert mount.transport.mode == "hardware"
    assert not mount.transport.connected
    assert not mount.allow_simulation
    async def check():
        with pytest.raises(DriveError, match="real hardware only"):
            await mount.connect("simulation")
    asyncio.run(check())


def test_chunked_serial_reply_handles_echo_and_partial_delivery():
    class Port:
        def __init__(self):
            self.buffer = b""
        def write(self, request):
            self.buffer = request + b"123;"
        def read(self, size):
            data, self.buffer = self.buffer[:size], self.buffer[size:]
            return data
        def read_until(self, expected, size):
            result = bytearray()
            while len(result) < size:
                result.extend(self.read(1))
                if result.endswith(expected): break
            return bytes(result)
        @property
        def in_waiting(self):
            return min(3, len(self.buffer))
        def reset_input_buffer(self):
            self.buffer = b""
    drive = SerialElmoDrive("COM_TEST", 921600)
    drive.connected = True
    drive._port = Port()
    assert drive._query_sync("AX1.PX") == "123"
    assert drive._query_sync("AX4.IQ") == "123"

def test_flash_save_serial_timeout_is_applied_and_restored():
    class Port:
        timeout=.35
        def write(self,request):pass
        def read_until(self,expected,size):
            assert self.timeout==6
            return b'SV\r;'
    drive=SerialElmoDrive('TEST',921600)
    drive.connected=True
    drive._port=Port()
    assert drive._query_sync('SV',timeout_seconds=6)==''
    assert drive._port.timeout==.35
