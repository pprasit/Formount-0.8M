"""Latch boot region once, then unwrap the absolute azimuth continuously."""
from .domain import DriveError

class AzimuthPosition:
    def __init__(self):
        self.previous = None
        self.position = None
        self.raw_to_logical_offset = 0.0
        self.boot_region = None

    def update(self, raw_degrees, inputs):
        absolute = raw_degrees % 360
        if self.previous is None:
            if inputs is None:
                raise DriveError("Azimuth position requires CW/CCW sensor inputs at startup")
            cw, ccw = bool(inputs & (1 << 16)), bool(inputs & (1 << 17))
            if cw and ccw:
                raise DriveError("Azimuth CW and CCW sensors are both active; check the sensors")
            self.boot_region = 'CCW' if ccw else 'CW' if cw else 'Neither'
            # Near encoder zero, negative jitter is reported as 359.99 degrees.
            # Select the zero boundary before unwrapping, rather than latching
            # an extra full turn when CW is active.
            near_zero = absolute < .05 or absolute > 359.95
            if near_zero:
                self.position = absolute - 360 if absolute > 180 else absolute
            else:
                self.position = absolute - 360 if ccw else absolute
        else:
            delta = (absolute - self.previous + 180) % 360 - 180
            self.position += delta
        self.previous = absolute
        self.raw_to_logical_offset = self.position - raw_degrees
        return self.position

    def status(self):
        return dict(initialized=self.previous is not None, boot_region=self.boot_region,
                    position_deg=self.position, absolute_deg=self.previous,
                    offset_deg=self.raw_to_logical_offset)
