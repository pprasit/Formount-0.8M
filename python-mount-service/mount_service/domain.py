from dataclasses import asdict, dataclass
from typing import Literal

AxisId = Literal["azimuth", "altitude", "rotator", "m3"]


@dataclass(frozen=True)
class AxisConfig:
    id: AxisId
    name: str
    number: int
    counts_per_rev: int
    minimum_deg: float
    maximum_deg: float
    max_velocity: float
    initial_deg: float
    color: str
    offset_deg: float = 0
    wrap: bool = False

    def counts_to_deg(self, value: float) -> float:
        # Preserve the mechanical turn, including negative and >360 degree values.
        result = value * 360 / self.counts_per_rev + self.offset_deg
        return result

    def deg_to_counts(self, value: float) -> int:
        return round((value - self.offset_deg) * self.counts_per_rev / 360)

    def velocity_to_counts(self, value: float) -> int:
        return round(value * self.counts_per_rev / 360)

    def export(self) -> dict:
        return asdict(self)


# Az/Alt resolutions and limits reflect the saved Fork-Mount profile.
# Rotator/M3 range 0–359 degrees confirmed by the operator.
AXES = (
    AxisConfig("azimuth", "Azimuth", 1, 2**32, 45, 700, 30, 178.42, "#65e5c4"),
    AxisConfig("altitude", "Altitude", 2, 2**32, 0, 90, 30, 54.18, "#87adff"),
    AxisConfig("rotator", "Rotator", 3, 2**32, 0, 359, 5, 12.64, "#bc9bff"),
    AxisConfig("m3", "M3", 4, 2**18, 0, 359, 10, 86.32, "#f3bc78"),
)
AXIS_MAP = {axis.id: axis for axis in AXES}

def travel_limit_message(axis, side, limit, stopped=True):
    label = ('CCW / Lower' if side=='lower' else 'CW / Upper') if axis.id=='azimuth' else ('Lower' if side=='lower' else 'Upper')
    return f'TCS rejected this movement · {axis.name}: {label} limit reached ({limit:g}°). '+('Motion stopped; move in the opposite direction to leave the limit.' if stopped else 'Movement toward this limit is blocked; move in the opposite direction.')


@dataclass
class Reading:
    position_deg: float
    velocity_deg_s: float
    current_a: float
    enabled: bool
    monotonic_at: float
    position_counts: float


class DriveError(RuntimeError):
    pass
