"""One transport owner for all four axes; no browser opens a serial port."""
import asyncio
import math
import time
import threading
from collections import deque
from typing import Protocol

import serial

from .domain import AXES, AxisConfig, DriveError, Reading
from .command_lane import CommandLane
from .azimuth_position import AzimuthPosition
from .domain import travel_limit_message


class DriveTransport(Protocol):
    mode: str
    connected: bool
    identity: str

    async def read(self, axis: AxisConfig, now: float) -> Reading: ...
    async def enable(self, axis: AxisConfig, value: bool) -> None: ...
    async def velocity(self, axis: AxisConfig, value: float) -> None: ...
    async def goto(self, axis: AxisConfig, target: float, speed: float) -> None: ...
    async def stop(self, axis: AxisConfig) -> None: ...
    async def close(self) -> None: ...


class SimulatedDrive:
    mode = "simulation"
    identity = "ELMO • SIM-0001"

    def __init__(self, started: float | None = None):
        self.connected = True
        self.started = started if started is not None else time.perf_counter()
        self.positions = {a.id: a.initial_deg for a in AXES}
        self.velocities = {a.id: 0.0 for a in AXES}
        self.commands = {a.id: 0.0 for a in AXES}
        self.targets: dict[str, tuple[float, float] | None] = {a.id: None for a in AXES}
        self.enabled = {a.id: True for a in AXES}
        self.demo = {a.id: True for a in AXES}
        self.last_read = {a.id: self.started for a in AXES}

    async def read(self, axis: AxisConfig, now: float) -> Reading:
        if not self.connected:
            raise DriveError("Drive is disconnected")
        key = axis.id
        dt = min(max(now - self.last_read[key], 0), 0.2)
        self.last_read[key] = now
        elapsed = now - self.started
        phase = axis.number * 0.8
        if self.demo[key]:
            self.commands[key] = (0.06 + axis.number * 0.015) * math.sin(elapsed * 0.32 + phase)
        target = self.targets[key]
        if target:
            error = target[0] - self.positions[key]
            self.commands[key] = max(-target[1], min(target[1], error * 2))
            if abs(error) < 0.0005 and abs(self.velocities[key]) < 0.001:
                self.targets[key] = None
                self.commands[key] = 0
        desired = self.commands[key] if self.enabled[key] else 0
        self.velocities[key] += (desired - self.velocities[key]) * min(1, dt * 12)
        self.positions[key] += self.velocities[key] * dt
        if self.positions[key] <= axis.minimum_deg or self.positions[key] >= axis.maximum_deg:
            self.positions[key] = max(axis.minimum_deg, min(axis.maximum_deg, self.positions[key]))
            self.velocities[key] = self.commands[key] = 0
            self.demo[key] = False
            self.targets[key] = None
        current = 0.0
        if self.enabled[key]:
            current = 0.25 + abs(self.velocities[key]) * 0.3 + 0.018 * math.sin(elapsed * 2 + phase)
        return Reading(self.positions[key], self.velocities[key], current,
                       self.enabled[key], now, axis.deg_to_counts(self.positions[key]))

    async def enable(self, axis: AxisConfig, value: bool) -> None:
        self.enabled[axis.id] = value
        if not value:
            await self.stop(axis)

    async def velocity(self, axis: AxisConfig, value: float) -> None:
        self.demo[axis.id] = False
        self.targets[axis.id] = None
        self.commands[axis.id] = value

    async def goto(self, axis: AxisConfig, target: float, speed: float) -> None:
        self.demo[axis.id] = False
        self.targets[axis.id] = (target, speed)

    async def stop(self, axis: AxisConfig) -> None:
        self.demo[axis.id] = False
        self.targets[axis.id] = None
        self.commands[axis.id] = self.velocities[axis.id] = 0

    async def close(self) -> None:
        for axis in AXES:
            await self.stop(axis)
        self.connected = False


class ElmoCommandRejected(DriveError):
    def __init__(self, command, code):
        self.command, self.code = command, code
        reason = {28:'Value outside configured limits',220:'Hardware position limit active',222:'Software position limit active',166:'Position outside configured modulo'}.get(code,'Drive rejected the command')
        self.diagnostic = f'Elmo rejected {command}: {reason} (EC {code})'
        movement = command.split('.')[-1].split('=')[0] in ('BG','JP','JV','PA','PR','SP','MO')
        super().__init__(f"TCS rejected {'this movement' if movement else 'this command'}: {reason}.")


def parse_reply(command: str, raw: str | bytes) -> str:
    """Decode a single terminated ASCII response; reject error/ambiguous replies."""
    if isinstance(raw, bytes):
        if not raw.endswith(b';'):raise DriveError('Incomplete Elmo reply')
        # Platinum returns a binary EC byte immediately before ?;.
        # Decode this before ASCII; EC 220/222 are not printable characters.
        if raw.endswith(b'?;') and len(raw)>=3:
            raise ElmoCommandRejected(command, raw[-3])
        try:raw=raw.decode('ascii')
        except UnicodeDecodeError as exc:
            raise DriveError('Drive communication error: unreadable reply (Non-ASCII). Command result is unconfirmed; check the drive connection.') from exc
    if not raw.endswith(";"):
        raise DriveError("Incomplete Elmo reply")
    if "?" in raw:
        raise DriveError('TCS rejected this command. The requested operation could not be completed.')
    parts = [p.strip() for p in raw[:-1].replace("\r", "\n").split("\n") if p.strip()]
    if parts and parts[0] == command:
        parts.pop(0)
    if not parts:
        return ""
    if len(parts) != 1:
        raise DriveError("Ambiguous Elmo reply; check firmware/echo protocol")
    return parts[0]


class SerialElmoDrive:
    """Platinum live transport: one worker owns telemetry and prioritized commands."""
    mode = "hardware"

    def __init__(self, port: str, baud: int, interval_ms: float = 5):
        self.port_name = port
        self.identity = "Unverified Elmo"
        self.connected = False
        self._port = None
        self._lock = asyncio.Lock()
        self.baud = baud
        self.interval_ms = interval_ms
        self._halt = threading.Event()
        self._frame_lock = threading.Lock()
        self._frames = deque(maxlen=4000)
        self._worker = None
        self._motor_states = {}
        self._timings = deque(maxlen=200)
        self._starts = deque(maxlen=200)
        self.overruns = 0
        self.scaling = {}
        self.resolutions = {}
        self.configuration_errors = {}
        self.lane = CommandLane()
        self.commands = {a.id: 0.0 for a in AXES}
        self.targets = {a.id: None for a in AXES}
        self.unit_modes = {}
        self.velocity_limits = {}
        self.leases = {}
        self.latest = {}
        self.safety_errors = {}
        self.motion_notices = {}
        self.digital_inputs = {}
        self.input_stamp = None
        self.input_error = None
        self.query_timings = {}
        self.azimuth_position = AzimuthPosition()
        self.position_limits = {}


    async def connect(self, expected_serial: str) -> None:
        self._port = await asyncio.to_thread(serial.Serial, self.port_name, self.baud,
                                             timeout=0.35, write_timeout=0.35)
        self.connected = True
        try:
            identity = await self.query("SN[4]")
            if identity != expected_serial:
                raise DriveError(f"Serial mismatch: expected {expected_serial}, received {identity}")
            self.identity = identity
            for axis in AXES:
                factors = [1. if i in (3,4) else float(await self.query(f"AX{axis.number}.FC[{i}]")) for i in range(1, 13)]
                if any(not math.isfinite(v) or v < 1 for v in factors):
                    raise DriveError(f"AX{axis.number} has invalid scaling factors")
                self.scaling[axis.id] = factors
                mo = float(await self.query(f"AX{axis.number}.MO"))
                if mo not in (0, 1):
                    raise DriveError("Invalid motor enable state")
                self._motor_states[axis.id] = bool(mo)
                self.unit_modes[axis.id] = int(float(await self.query(f"AX{axis.number}.UM")))
            self.digital_inputs = {'1': int(float(await self.query('IP[1]')))}
            self.input_stamp = time.perf_counter()
            self.azimuth_position = AzimuthPosition()
            for axis in AXES:
                self.position_limits[axis.id] = tuple([float(await self.query(f'AX{axis.number}.{c}')) for c in ('VL[3]', 'VH[3]')])
                self.velocity_limits[axis.id] = float(await self.query(f'AX{axis.number}.VH[2]')) / self.velocity_units(axis, 1)
            self._halt.clear()
            self._worker = threading.Thread(target=self._poll_worker, name="elmo-5ms-poll", daemon=True)
            self._worker.start()
        except Exception:
            await self.close()
            raise

    def position_degrees(self, axis, counts):
        result = counts * 360 / self.resolutions.get(axis.id, axis.counts_per_rev) + axis.offset_deg
        if axis.id == 'azimuth':
            return self.azimuth_position.update(result, self.digital_inputs.get('1'))
        return result

    def unit_factors(self, axis):
        f = self.scaling.get(axis.id, [1.] * 12)
        if len(f) < 12: f = f + [1.] * (12 - len(f))
        position = (f[6]/f[7]) * (f[5]/f[4]) * (f[1]/f[0])
        return position, position * f[8]/f[9]

    def position_units(self, axis, degrees):
        offset = axis.offset_deg
        if axis.id == 'azimuth':
            if self.azimuth_position.previous is None:
                raise DriveError('Azimuth startup sensor position has not been initialized')
            offset += self.azimuth_position.raw_to_logical_offset
        return round((degrees-offset) * self.resolutions.get(axis.id, axis.counts_per_rev) / 360) * self.unit_factors(axis)[0]

    def travel_limits(self, axis):
        limits = self.position_limits.get(axis.id)
        if limits is None or not all(math.isfinite(v) for v in limits) or limits[0] >= limits[1]:
            raise DriveError(f'{axis.name}: Read and configure valid drive position limits before motion')
        scale = self.resolutions.get(axis.id, axis.counts_per_rev) / 360 * self.unit_factors(axis)[0]
        offset = axis.offset_deg + (self.azimuth_position.raw_to_logical_offset if axis.id == 'azimuth' else 0)
        return tuple(v / scale + offset for v in limits)

    async def refresh_position_limits(self, axis):
        def read():
            self.position_limits[axis.id] = tuple(self._numeric(f'AX{axis.number}.{c}') for c in ('VL[3]', 'VH[3]'))
            return self.travel_limits(axis)
        return await self._job(read)

    def velocity_units(self, axis, degrees_s):
        return degrees_s * self.resolutions.get(axis.id, axis.counts_per_rev) / 360 * self.unit_factors(axis)[1]

    async def query(self, command: str) -> str:
        if self._worker and self._worker.is_alive():
            return await self._job(lambda: self._query_sync(command))
        async with self._lock:
            return await asyncio.to_thread(self._query_sync, command)

    async def query_physical(self, command):
        if self._worker and self._worker.is_alive():
            return await self._job(lambda:self._query_sync(command,physical=True))
        async with self._lock:return await asyncio.to_thread(self._query_sync,command,physical=True)

    def _query_sync(self, command: str, timeout_seconds=0.35, physical=False) -> str:
        if not self.connected or not self._port:raise DriveError('Serial transport is disconnected')
        if not physical and command.startswith('IP['):
            external=getattr(self,'axis_external_queries',{}).get('azimuth')
            if external:return external(command)
        for axis in ([] if physical else AXES):
            if command.startswith(f"AX{axis.number}."):
                command=f"AX{getattr(self,'axis_channels',{}).get(axis.id,axis.number)}."+command.split(".",1)[1]
                external=getattr(self,'axis_external_queries',{}).get(axis.id)
                if external:return external(command)
                break
        if not self.connected or not self._port:
            raise DriveError("Serial transport is disconnected")
        if "\r" in command or "\n" in command or ";" in command:
            raise DriveError("Invalid command framing")
        query_started = time.perf_counter()
        self._port.write((command + "\r").encode("ascii"))
        # One request is outstanding; stop at its terminator without consuming
        # bytes from a following reply.
        previous_timeout = getattr(self._port, 'timeout', 0.35)
        try:
            self._port.timeout = timeout_seconds
            raw_reply = self._port.read_until(b";", size=1024)
        finally:
            self._port.timeout = previous_timeout
        buffer = bytearray(raw_reply)
        self.query_timings[command] = round((time.perf_counter()-query_started)*1000, 3)
        raw = bytes(buffer)
        if not raw.endswith(b";"):
            self._port.reset_input_buffer()
        return parse_reply(command, raw)

    def _numeric(self, command):
        value = float(self._query_sync(command))
        if not math.isfinite(value):
            raise DriveError(f"Non-finite {command} response")
        return value

    def _poll_worker(self):
        # The serial worker owns the port; UI scheduling never sets acquisition timing.
        period = self.interval_ms / 1000
        deadline = time.perf_counter()
        motor_due = deadline + .2
        while not self._halt.is_set():
            began = time.perf_counter()
            self.lane.execute()
            self._check_leases()
            readings, errors = {}, {}
            for axis in AXES:
                try:
                    values = [self._numeric(f"AX{axis.number}.{field}") for field in ("PX", "VX", "IQ")]
                    readings[axis.id] = Reading(self.position_degrees(axis, values[0]), values[1] * 360 / self.resolutions.get(axis.id, axis.counts_per_rev),
                        values[2], self._motor_states[axis.id], time.perf_counter(), values[0])
                except Exception as exc:
                    errors[axis.id] = str(exc)
            if began >= motor_due:
                for axis in AXES:
                    try:
                        mo = self._numeric(f"AX{axis.number}.MO")
                        if mo not in (0, 1):
                            raise DriveError("Invalid motor enable state")
                        self._motor_states[axis.id] = bool(mo)
                        if axis.id in readings:
                            readings[axis.id].enabled = bool(mo)
                    except Exception as exc:
                        errors[axis.id] = str(exc)
                try:
                    # IP[1] is a bitfield of digital channels, not four axis indexes.
                    # This firmware rejects IP[2]; keep the successful legacy read.
                    inputs={'1':int(self._numeric('IP[1]'))}
                    self.digital_inputs=inputs
                    self.input_stamp=time.perf_counter()
                    self.input_error=None
                except Exception as exc:
                    self.input_error=str(exc)
                motor_due = began + .2
            self.latest.update(readings)
            for axis in AXES:
                if self.commands[axis.id] or self.targets[axis.id]:
                    reading = readings.get(axis.id)
                    invalid = axis.id in errors or reading is None or not reading.enabled
                    limit_notice = None
                    try:
                        lower, upper = self.travel_limits(axis)
                        outward = reading and ((reading.position_deg <= lower + .1 and self.commands[axis.id] < 0) or (reading.position_deg >= upper - .1 and self.commands[axis.id] > 0))
                        if outward and not invalid:
                            side = 'lower' if self.commands[axis.id] < 0 else 'upper'
                            limit_notice = travel_limit_message(axis, side, lower if side=='lower' else upper)
                    except DriveError:
                        outward = True
                    if invalid or outward:
                        try:
                            self._stop_sync(axis)
                            if limit_notice:
                                self.motion_notices[axis.id] = limit_notice
                            else:
                                self.safety_errors[axis.id] = f'{axis.name}: motion stopped because '+('fresh position / enabled telemetry is unavailable.' if invalid else 'drive travel limits could not be verified.')
                        except Exception as exc:
                            errors[axis.id] = str(exc)
                            self.safety_errors[axis.id] = f"Stop not acknowledged; retrying: {exc}"
                    target = self.targets[axis.id]
                    if reading and target and abs(reading.position_deg-target[0])<.01 and abs(reading.velocity_deg_s)<.01:
                        self.targets[axis.id] = None
            errors.update(self.safety_errors)
            ended = time.perf_counter()
            with self._frame_lock:
                self._frames.append((time.perf_counter(), readings, errors))
                self._timings.append((ended - began) * 1000)
                self._starts.append(began)
                if ended - began > period:
                    self.overruns += 1
            deadline += period
            if deadline < ended:
                deadline = ended
            if deadline > ended:
                time.sleep(deadline - ended)

    def drain(self):
        with self._frame_lock:
            frames = list(self._frames)
            self._frames.clear()
            starts, timings = list(self._starts), list(self._timings)
            self.measured_hz = (len(starts)-1)/(starts[-1]-starts[0]) if len(starts)>1 else 0
            self.cycle_ms = sum(timings)/len(timings) if timings else 0
            self.p95_ms = sorted(timings)[int((len(timings)-1)*.95)] if timings else 0
        return frames

    async def read(self, axis: AxisConfig, now: float) -> Reading:
        values = []
        for field in ("PX", "VX", "IQ", "MO"):
            text = await self.query(f"AX{axis.number}.{field}")
            try:
                value = float(text)
            except ValueError as exc:
                raise DriveError(f"Invalid AX{axis.number}.{field} response") from exc
            if not math.isfinite(value):
                raise DriveError(f"Non-finite AX{axis.number}.{field} response")
            values.append(value)
        if values[3] not in (0, 1):
            raise DriveError("Invalid motor enable state")
        return Reading(self.position_degrees(axis, values[0]), values[1] * 360 / self.resolutions.get(axis.id, axis.counts_per_rev),
                       values[2], bool(values[3]), time.perf_counter(), values[0])

    async def _job(self, function, priority=10, invalidate=False, timeout_seconds=2):
        if not self.connected or not self._worker or not self._worker.is_alive():
            raise DriveError("Serial acquisition worker is unavailable")
        future = self.lane.submit(function, priority, invalidate)
        try:
            return await asyncio.wait_for(asyncio.wrap_future(future), timeout_seconds)
        except asyncio.TimeoutError as exc:
            future.cancel()
            raise DriveError("Command acknowledgement timed out") from exc

    def _sequence(self, axis, commands):
        epoch = self.lane.epoch
        try:
            for command in commands:
                if epoch != self.lane.epoch:
                    raise DriveError("Motion cancelled by Stop")
                self._query_sync(f"AX{axis.number}.{command}")
        except Exception:
            try:
                self._stop_sync(axis)
            except Exception:
                pass
            raise

    def _stop_sync(self, axis):
        self._query_sync(f"AX{axis.number}.ST")
        self.commands[axis.id] = 0
        self.targets[axis.id] = None
        self.leases.pop(axis.id, None)

    def _check_leases(self):
        for axis in AXES:
            if time.perf_counter() >= self.leases.get(axis.id, float('inf')):
                try:
                    self._stop_sync(axis)
                except Exception as exc:
                    self.safety_errors[axis.id] = str(exc)

    async def enable(self, axis: AxisConfig, value: bool) -> None:
        def apply():
            self._stop_sync(axis)
            if value:
                if self.configuration_errors.get(axis.id):
                    raise DriveError(self.configuration_errors[axis.id])
                mode = self.unit_modes.get(axis.id)
                if mode not in (2, 5):
                    raise DriveError(f"AX{axis.number} UM={mode}: enable requires velocity/position mode")
            self._query_sync(f"AX{axis.number}.MO={int(value)}")
            actual = self._numeric(f"AX{axis.number}.MO")
            # MO changes asynchronously, including brake/disable sequencing.
            state_deadline = time.perf_counter() + .1
            while actual != int(value) and time.perf_counter() < state_deadline:
                time.sleep(.003)
                actual = self._numeric(f"AX{axis.number}.MO")
            if actual != int(value):
                details = []
                for field in ("MF", "EE[5]", "EE[2]", "EC", "SR"):
                    try: details.append(f"{field}={self._query_sync(f'AX{axis.number}.{field}')}")
                    except DriveError: pass
                if "EE[5]=229" in details:
                    raise DriveError(f"{axis.name}: Motor enable is blocked by the safety circuit. Check whether the Emergency Stop button is pressed.")
                raise DriveError(f"{axis.name}: Unable to enable the motor. Check the drive status in Hardware settings.")
            self._motor_states[axis.id] = bool(actual)
            if value:
                try:
                    # Platinum rejects JP while MO=0. MO acknowledges the request;
                    # SO confirms brake release/commutation has actually completed.
                    deadline = time.perf_counter() + 1.2
                    while self._numeric(f"AX{axis.number}.SO") != 1:
                        if time.perf_counter() >= deadline:
                            raise DriveError(f"AX{axis.number} servo not ready after Enable")
                        time.sleep(.01)
                    self._query_sync(f"AX{axis.number}.{'JP' if mode == 5 else 'JV'}=0")
                except Exception:
                    self._query_sync(f"AX{axis.number}.MO=0")
                    self._motor_states[axis.id] = False
                    raise
            self.safety_errors.pop(axis.id, None)
            self.motion_notices.pop(axis.id, None)
            return actual
        await self._job(apply, priority=5 if value else 0, invalidate=not value)

    async def velocity(self, axis: AxisConfig, value: float, lease=.8) -> None:
        def apply():
            reading = self.latest.get(axis.id)
            if not reading or time.perf_counter()-reading.monotonic_at>.2 or not self._motor_states[axis.id]:
                raise DriveError("Jog requires fresh enabled telemetry")
            if self.configuration_errors.get(axis.id):
                raise DriveError(self.configuration_errors[axis.id])
            mode = self.unit_modes.get(axis.id)
            if mode not in (2, 5):
                raise DriveError("Jog requires UM=2 or UM=5")
            native_velocity=self.velocity_units(axis,value)
            ceiling = self._numeric(f'AX{axis.number}.VH[2]')
            self.velocity_limits[axis.id] = ceiling / self.velocity_units(axis, 1)
            if ceiling <= 0 or abs(native_velocity) > ceiling:
                raise DriveError(f'{axis.name}: velocity exceeds drive limit {self.velocity_limits[axis.id]:g} deg/s')
            # Match the C# long cast: position-mode JP uses integer counts/s.
            if mode==5:
                native_velocity=int(native_velocity)
                if value and native_velocity==0:
                    raise DriveError("Jog speed is below one encoder count per second; increase the speed")
            self.commands[axis.id] = value
            self.targets[axis.id] = None
            self.leases[axis.id] = time.perf_counter()+lease
            jog=f"JP={native_velocity}" if mode==5 else f"JV={native_velocity:.17g}"
            try:
                self._sequence(axis, [jog, "BG"])
            except ElmoCommandRejected as exc:
                if exc.code in (220,222):
                    # Read again after the drive's rejection. Never infer which
                    # limit was reached merely from the requested direction.
                    position=self.position_degrees(axis,self._numeric(f'AX{axis.number}.PX'))
                    lower,upper=self.travel_limits(axis)
                    side='lower' if value<0 else 'upper'
                    at_limit=position<=lower+.1 if value<0 else position>=upper-.1
                    if at_limit:
                        message=travel_limit_message(axis,side,lower if value<0 else upper)
                        self.motion_notices[axis.id]=message
                        raise DriveError(message) from exc
                raise
            self.safety_errors.pop(axis.id, None)
            self.motion_notices.pop(axis.id, None)
        await self._job(apply)

    async def goto(self, axis: AxisConfig, target: float, speed: float) -> None:
        def apply():
            if self.configuration_errors.get(axis.id):
                raise DriveError(self.configuration_errors[axis.id])
            if self.unit_modes.get(axis.id) != 5:
                raise DriveError("Goto requires drive position mode UM=5; mode is not changed automatically")
            reading = self.latest.get(axis.id)
            if not reading or time.perf_counter()-reading.monotonic_at>.2 or not self._motor_states[axis.id]:
                raise DriveError("Goto requires fresh enabled telemetry")
            counts = self.position_units(axis, target)
            ceiling = self._numeric(f'AX{axis.number}.VH[2]')
            native_speed = ceiling if speed is None else abs(self.velocity_units(axis, speed))
            if native_speed > ceiling:raise DriveError('Goto speed exceeds the drive velocity limit')
            if native_speed <= 0:
                raise DriveError(f'{axis.name}: Velocity limit must be greater than zero before Go')
            actual_speed = native_speed / self.velocity_units(axis, 1)
            self.commands[axis.id] = 0
            self.targets[axis.id] = (target, actual_speed)
            self.leases.pop(axis.id, None)
            self._sequence(axis, ["ST", f"SP={native_speed:.9g}", f"PA={counts:.17g}", "BG"])
            self.safety_errors.pop(axis.id, None)
            self.motion_notices.pop(axis.id, None)
        await self._job(apply)

    async def stop(self, axis: AxisConfig) -> None:
        await self._job(lambda: self._stop_sync(axis), priority=0, invalidate=True)
        self.safety_errors.pop(axis.id, None)

    async def goto_pair(self, azimuth, altitude, azimuth_speed, altitude_speed):
        axes=AXES[:2]
        def apply():
            epoch=self.lane.epoch
            for axis in axes:
                reading=self.latest.get(axis.id)
                if self.configuration_errors.get(axis.id): raise DriveError(self.configuration_errors[axis.id])
                if self.unit_modes.get(axis.id)!=5 or not reading or time.perf_counter()-reading.monotonic_at>.2 or not self._motor_states[axis.id]:
                    raise DriveError('Both axes require UM=5 and fresh enabled telemetry')
            try:
                # Stage both axes first; issue both BG commands in the same worker job.
                for axis,target,speed in zip(axes,(azimuth,altitude),(azimuth_speed,altitude_speed)):
                    ceiling=self._numeric(f'AX{axis.number}.VH[2]')
                    native_speed=ceiling if speed is None else abs(self.velocity_units(axis,speed))
                    if native_speed>ceiling:raise DriveError('Goto speed exceeds the drive velocity limit')
                    if native_speed<=0:raise DriveError(f'{axis.name}: Velocity limit must be greater than zero before Go')
                    self.targets[axis.id]=(target,native_speed/self.velocity_units(axis,1))
                    self.commands[axis.id]=0
                    self.leases.pop(axis.id,None)
                    for command in ['ST',f'SP={native_speed:.9g}',f'PA={self.position_units(axis, target):.17g}']:
                        if epoch!=self.lane.epoch:raise DriveError('Combined slew cancelled by Stop')
                        self._query_sync(f'AX{axis.number}.{command}')
                for axis in axes:
                    if epoch!=self.lane.epoch:raise DriveError('Combined slew cancelled by Stop')
                    self._query_sync(f'AX{axis.number}.BG')
                    self.safety_errors.pop(axis.id,None)
            except Exception:
                for axis in axes:
                    try:self._stop_sync(axis)
                    except Exception as exc:self.safety_errors[axis.id]=f'Stop not acknowledged: {exc}'
                raise
        await self._job(apply)

    async def stop_all(self):
        def apply():
            failures=[]
            for axis in AXES:
                try: self._stop_sync(axis)
                except Exception as exc: failures.append(f"AX{axis.number}: {exc}")
            if failures: raise DriveError("; ".join(failures))
            self.safety_errors.clear()
        await self._job(apply, priority=0, invalidate=True)

    async def close(self) -> None:
        if self._worker and self._worker.is_alive() and (any(self.commands.values()) or any(self.targets.values())):
            await self.stop_all()
        self.lane.cancel()
        self._halt.set()
        if self._worker:
            await asyncio.to_thread(self._worker.join)
            self._worker = None
        self.connected = False
        if self._port:
            await asyncio.to_thread(self._port.close)
            self._port = None
