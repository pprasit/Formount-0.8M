import asyncio

import math

import time

import json

from pathlib import Path

from collections import deque

from datetime import datetime, timezone



from .domain import travel_limit_message, AXES, AXIS_MAP, DriveError

from .transports import SerialElmoDrive, SimulatedDrive

from .regions import travel_region, input_snapshot





def utc_iso() -> str:

    return datetime.now(timezone.utc).isoformat()





class MountService:

    """Owns one drive, four logical axes, telemetry history, and all motion commands."""

    def __init__(self, simulation=False):

        self.audit = None

        self.started = time.perf_counter()

        self.config = json.loads((Path(__file__).parent.parent / "config.json").read_text())

        self.allow_simulation = simulation

        self.transport = SimulatedDrive(self.started - 60) if simulation else SerialElmoDrive(

            self.config["port"], self.config["baud"], self.config["poll_interval_ms"])

        if isinstance(self.transport, SerialElmoDrive):

            self.transport.resolutions = dict(self.config.get('axis_resolutions', {}))

        self.readings = {}

        self.errors = {a.id: None for a in AXES}

        self.modes = {a.id: "demo" if simulation else "monitoring" for a in AXES}

        self.deadlines = {}

        self.history = deque(maxlen=24000)

        self.events = deque(maxlen=100)

        self.sequence = 0

        self.generation = 0

        self.poll_hz = 20.0

        self.last_cycle_ms = 0.0

        self._cycles = deque(maxlen=60)

        self._lock = asyncio.Lock()

        self._task = None

        self._running = False

        self.event("info", "Mount service ready", "Simulation test" if simulation else "Real Elmo telemetry · target 5 ms")



    def event(self, level: str, title: str, detail: str = ""):

        self.events.appendleft({"time": utc_iso(), "level": level, "title": title, "detail": detail})

        if self.audit:self.audit.enqueue("mount."+level,{"title":title,"detail":detail})



    async def start(self):

        # Seed real simulator samples so charts are useful on the first visit.

        if isinstance(self.transport, SimulatedDrive):

            for index in range(601):

                await self._poll(self.started - 60 + index / 10)

        else:

            try:

                await self.transport.connect(self.config["serial_number"])

                self.event("info", "Elmo connected", f"{self.config['port']} · {self.config['baud']} baud · SN {self.transport.identity}")

            except Exception as exc:

                self.event("error", "Hardware connection failed", str(exc))

                self.errors = {a.id: str(exc) for a in AXES}

        self._running = True

        self._task = asyncio.create_task(self._loop(), name="mount-polling")



    async def close(self):

        self._running = False

        if self._task:

            self._task.cancel()

            try:

                await self._task

            except asyncio.CancelledError:

                pass

        async with self._lock:

            await self.transport.close()



    async def _loop(self):

        while self._running:

            started = time.perf_counter()

            async with self._lock:

                if self.transport.connected:

                    await self._poll(started)

            if isinstance(self.transport, SerialElmoDrive):

                self.poll_hz = getattr(self.transport, "measured_hz", 0)

                self.last_cycle_ms = getattr(self.transport, "cycle_ms", 0)

                await asyncio.sleep(max(.001, self.transport.interval_ms / 1000))

                continue

            self.last_cycle_ms = (time.perf_counter() - started) * 1000

            self._cycles.append(started)

            if len(self._cycles) > 1:

                span = self._cycles[-1] - self._cycles[0]

                self.poll_hz = (len(self._cycles) - 1) / span if span else 0

            await asyncio.sleep(max(0.001, 0.05 - (time.perf_counter() - started)))



    async def _poll(self, now: float):

        if isinstance(self.transport, SerialElmoDrive):

            for stamp, readings, errors in self.transport.drain():

                self.readings.update(readings)

                for axis in AXES:

                    error = errors.get(axis.id)

                    if error and error != self.errors[axis.id]:

                        self.event("error", f"{axis.name} telemetry unavailable", error)

                    if not error and self.errors[axis.id]:

                        self.event("info", f"{axis.name} telemetry recovered")

                    notice = getattr(self.transport,'motion_notices',{}).get(axis.id)

                    if not hasattr(self,'_last_motion_notices'):self._last_motion_notices={}

                    if notice and self._last_motion_notices.get(axis.id)!=notice:self.event('warning',f'{axis.name} motion stopped',notice)

                    self._last_motion_notices[axis.id]=notice

                    self.errors[axis.id] = error

                for axis in AXES:

                    if self.modes[axis.id] == 'jogging' and not self.transport.commands[axis.id]:

                        self.modes[axis.id] = 'idle'

                    if self.modes[axis.id] == 'slewing' and not self.transport.targets[axis.id]:

                        self.modes[axis.id] = 'idle'

                self._record(stamp)

            return

        for axis in AXES:

            try:

                deadline = self.deadlines.get(axis.id)

                if deadline and now >= deadline:

                    await self.transport.stop(axis)

                    self.deadlines.pop(axis.id, None)

                    self.modes[axis.id] = "idle"

                    self.event("info", f"{axis.name} jog stopped", "Deadman lease expired")

                self.readings[axis.id] = await self.transport.read(axis, now)

                previous_error = self.errors[axis.id]

                self.errors[axis.id] = None

                if previous_error:

                    self.event("info", f"{axis.name} telemetry recovered")

                if isinstance(self.transport, SimulatedDrive):

                    if self.modes[axis.id] == "slewing" and not self.transport.targets[axis.id]:

                        self.modes[axis.id] = "idle"

            except Exception as exc:

                # Keep the last good reading; never turn missing hardware data into zero.

                if self.errors[axis.id] != str(exc):

                    self.event("error", f"{axis.name} telemetry unavailable", str(exc))

                self.errors[axis.id] = str(exc)

        self._record(now)



    def _record(self, now):

        self.sequence += 1

        self.history.append({"sequence": self.sequence, "t": now - self.started,

                             "axes": {a.id: {key: value for key, value in self._axis_snapshot(a, now).items()

                                             if key in ("position_deg", "velocity_deg_s", "current_a", "command_velocity", "valid", "position_counts")}

                                      for a in AXES}})



    def _axis_snapshot(self, axis, now: float) -> dict:

        reading = self.readings.get(axis.id)

        age = max(0, now - reading.monotonic_at) if reading else None

        valid = bool(self.transport.connected and reading and not self.errors[axis.id] and age < 1)

        command = self.transport.commands[axis.id]

        target = self.transport.targets[axis.id]

        return {**axis.export(),

                "max_velocity": getattr(self.transport, "velocity_limits", {}).get(axis.id, axis.max_velocity) if isinstance(self.transport, SerialElmoDrive) else axis.max_velocity,

                "counts_per_rev": getattr(self.transport, "resolutions", {}).get(axis.id, axis.counts_per_rev),

                "motion_notice": getattr(self.transport, "motion_notices", {}).get(axis.id),

                "configuration_error": getattr(self.transport, "configuration_errors", {}).get(axis.id),

                "position_deg": reading.position_deg if reading else None,

                "position_counts": reading.position_counts if reading else None,

                "velocity_deg_s": reading.velocity_deg_s if reading else None,

                "current_a": reading.current_a if reading else None,

                "enabled": reading.enabled if reading else None,

                "command_velocity": command,

                "target_position": target[0] if target else None,

                "mode": self.modes[axis.id], "valid": valid,

                "age_ms": round(age * 1000, 1) if age is not None else None,

                "error": self.errors[axis.id]}



    def snapshot(self) -> dict:

        now = time.perf_counter()

        az=self._axis_snapshot(AXES[0],now)

        alt=self._axis_snapshot(AXES[1],now)

        return {"sequence": self.sequence, "utc": utc_iso(), "elapsed_s": now - self.started,

                "inputs":input_snapshot(getattr(self.transport,'digital_inputs',{}),getattr(self.transport,'input_stamp',None) if self.transport.connected else None,getattr(self.transport,'input_error',None) if self.transport.connected else 'Drive disconnected'),

                "region":travel_region(az['position_deg'] if az['valid'] else None,alt['position_deg'] if alt['valid'] else None),

                "drive": {"mode": self.transport.mode, "connected": self.transport.connected,

                          "generation": self.generation,

                          "port": getattr(self.transport, "port_name", None),

                          "baud": getattr(self.transport, "baud", None),

                          "target_interval_ms": getattr(self.transport, "interval_ms", 50),

                          "p95_cycle_ms": round(getattr(self.transport, "p95_ms", 0), 3),

                          "overruns": getattr(self.transport, "overruns", 0),

                          "query_ms": dict(getattr(self.transport, "query_timings", {})),

                          "scaling": getattr(self.transport, "scaling", {}),

                          "unit_modes": getattr(self.transport, "unit_modes", {}),

                          "azimuth_position": self.transport.azimuth_position.status() if isinstance(self.transport, SerialElmoDrive) else None,

                          "identity": self.transport.identity, "axis_count": 4,

                          "motion_available": self.transport.connected},

                "poll_hz": round(self.poll_hz, 1), "cycle_ms": round(self.last_cycle_ms, 2),

                "axes": [self._axis_snapshot(a, now) for a in AXES],

                "events": list(self.events)[:30]}



    async def command(self, axis_id: str, action: str, value=None, speed=None, lease=0.8):

        if axis_id not in AXIS_MAP:

            raise DriveError("Unknown axis")

        axis = AXIS_MAP[axis_id]

        stopped = False

        if action == 'stop' and isinstance(self.transport, SerialElmoDrive):

            await self.transport.stop(axis)

            stopped = True

        async with self._lock:

            if not self.transport.connected:

                raise DriveError("Connect the drive first")

            state = self._axis_snapshot(axis, time.perf_counter())

            lower, upper = axis.minimum_deg, axis.maximum_deg

            if action in ("jog", "goto"):

                if isinstance(self.transport, SerialElmoDrive):

                    lower, upper = await self.transport.refresh_position_limits(axis)

                if axis_id=='m3' and self.config.get('m3_position_limits'):

                    minimum,maximum=self.config['m3_position_limits']

                    lower,upper=max(lower,minimum),min(upper,maximum)

                if not state["valid"]:

                    raise DriveError("Motion requires fresh, valid telemetry")

                if not state["enabled"]:

                    raise DriveError("Enable this axis before moving")

                if value is None or not math.isfinite(value):

                    raise DriveError("A finite target or velocity is required")

            if action == "enable":

                if not state["valid"]:

                    raise DriveError("Enable requires fresh, valid telemetry")

                await self.transport.enable(axis, True)

                self.modes[axis_id] = "idle"

                if axis_id in self.readings: self.readings[axis_id].enabled = True

            elif action == "disable":

                await self.transport.enable(axis, False)

                self.modes[axis_id] = "disabled"

                if axis_id in self.readings: self.readings[axis_id].enabled = False

                self.deadlines.pop(axis_id, None)

            elif action == "stop":

                if not stopped: await self.transport.stop(axis)

                self.modes[axis_id] = "idle"

                self.deadlines.pop(axis_id, None)

            elif action == "jog":

                if not isinstance(self.transport, SerialElmoDrive) and abs(value) > axis.max_velocity:

                    raise DriveError(f"Velocity must be within ±{axis.max_velocity} deg/s")

                if (state["position_deg"] <= lower + .1 and value < 0) or (

                    state["position_deg"] >= upper - .1 and value > 0):

                    raise DriveError(travel_limit_message(axis, 'lower' if value<0 else 'upper', lower if value<0 else upper, stopped=False))

                if isinstance(self.transport, SerialElmoDrive):

                    await self.transport.velocity(axis, value, lease)

                else:

                    await self.transport.velocity(axis, value)

                self.deadlines[axis_id] = time.perf_counter() + lease

                self.modes[axis_id] = "jogging"

            elif action == "goto":

                if not lower <= value <= upper:

                    raise DriveError(f"{axis.name}: target {value:g}° is outside the {'Lower' if value<lower else 'Upper'} limit ({lower if value<lower else upper:g}°). Enter a target within the drive limits: {lower:g}° to {upper:g}°.")

                if speed is None and not isinstance(self.transport, SerialElmoDrive):speed=1.0

                if speed is not None and (speed <= 0 or (not isinstance(self.transport, SerialElmoDrive) and speed > axis.max_velocity)):

                    raise DriveError("Goto speed is outside this axis velocity range")

                await self.transport.goto(axis, value, speed)

                self.deadlines.pop(axis_id, None)

                self.modes[axis_id] = "slewing"

            else:

                raise DriveError("Unknown axis command")

            await self._poll(time.perf_counter())

            if action != "jog":

                self.event("info", f"{axis.name} · {action}", f"Target {value}°" if action == "goto" else "Command acknowledged")

            return {"ok": True, "action": action, "axis": axis_id, "state": self.snapshot()}



    async def goto_pair(self, azimuth, altitude, azimuth_speed=None, altitude_speed=None):

        async with self._lock:

            if not isinstance(self.transport,SerialElmoDrive):

                azimuth_speed=azimuth_speed or 1.0

                altitude_speed=altitude_speed or 1.0

            for axis,target,speed in zip(AXES[:2],(azimuth,altitude),(azimuth_speed,altitude_speed)):

                state=self._axis_snapshot(axis,time.perf_counter())

                if not state['valid'] or not state['enabled']:raise DriveError('Enable Azimuth and Altitude with fresh telemetry first')

                lower,upper=await self.transport.refresh_position_limits(axis) if isinstance(self.transport,SerialElmoDrive) else (axis.minimum_deg,axis.maximum_deg)

                if not math.isfinite(target) or not lower<=target<=upper:raise DriveError(f'{axis.name} target outside drive limits: {lower:g} to {upper:g} degrees')

                if speed is not None and (not math.isfinite(speed) or speed<=0 or (not isinstance(self.transport,SerialElmoDrive) and speed>axis.max_velocity)):raise DriveError(f'{axis.name} speed outside allowed range')

            if isinstance(self.transport,SerialElmoDrive):

                await self.transport.goto_pair(azimuth,altitude,azimuth_speed,altitude_speed)

            else:

                for axis,target,speed in zip(AXES[:2],(azimuth,altitude),(azimuth_speed,altitude_speed)):

                    await self.transport.goto(axis,target,speed)

            for axis in AXES[:2]:

                self.modes[axis.id]='slewing'

                self.deadlines.pop(axis.id,None)

            await self._poll(time.perf_counter())

            self.event('info','Combined Az/Alt slew',f'Az {azimuth}° at {azimuth_speed}°/s · Alt {altitude}° at {altitude_speed}°/s')

            return {'ok':True,'state':self.snapshot()}



    async def stop_all(self):

        if isinstance(self.transport, SerialElmoDrive) and self.transport.connected:

            await self.transport.stop_all()

        async with self._lock:

            if not self.transport.connected:

                raise DriveError("Drive is disconnected")

            self.deadlines.clear()

            for axis in AXES:

                if isinstance(self.transport, SimulatedDrive): await self.transport.stop(axis)

                self.modes[axis.id] = "idle"

            await self._poll(time.perf_counter())

            self.event("warning", "STOP ALL", "All four axes stopped; pending motion cleared")

            return {"ok": True, "state": self.snapshot()}



    async def connect(self, mode: str, port: str = "", baud: int = 921600, serial_number: str = "", poll_interval_ms=5):

        async with self._lock:

            if mode == "simulation" and not self.allow_simulation:

                raise DriveError("This service uses real hardware only")

            if mode == "hardware":

                if not port or not serial_number:

                    raise DriveError("COM port and expected serial number are required")

                new_transport = SerialElmoDrive(port, baud, poll_interval_ms)

                new_transport.axis_channels = dict(self.config.get('axis_channels', {}))

                new_transport.axis_external_queries = getattr(self,'axis_external_queries',{})

                new_transport.resolutions = dict(self.config.get('axis_resolutions', {}))

                # Release the old owner first; never open two owners of the same drive.

                await self.transport.close()

                try:

                    await new_transport.connect(serial_number)

                except Exception as exc:

                    self.event("error", "Connection failed", str(exc))

                    raise DriveError(str(exc)) from exc

                self.transport = new_transport

            elif mode == "simulation":

                await self.transport.close()

                self.transport = SimulatedDrive()

            else:

                raise DriveError("Unknown transport mode")

            self.readings.clear()

            self.generation += 1

            self.history.clear()

            self.deadlines.clear()

            self.errors = {a.id: None for a in AXES}

            self.modes = {a.id: "demo" if mode == "simulation" else "monitoring" for a in AXES}

            await self._poll(time.perf_counter())

            self.event("info", "Drive connected", "Simulation" if mode == "simulation" else f"{port} · live control")

            return self.snapshot()



    async def disconnect(self):

        async with self._lock:

            await self.transport.close()

            self.deadlines.clear()

            self.event("warning", "Drive disconnected")

            return self.snapshot()

