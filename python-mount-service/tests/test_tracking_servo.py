import pytest
from mount_service.tracking import TrackingServo


def test_far_target_saturates_at_drive_velocity_limit():
    servo=TrackingServo(20)
    assert servo.velocity(100,0,.004,0,.1)==20
    assert servo.velocity(-100,0,.004,0,.1)==-20


def test_moving_target_is_acquired_and_velocity_converges():
    servo=TrackingServo(20)
    position,velocity,target,rate=0.,0.,45.,.004
    initial=servo.velocity(target,position,rate,velocity,.05)
    assert initial==20
    for _ in range(3000):
        command=servo.velocity(target,position,rate,velocity,.05)
        # Simple actuator with acceleration bounded to the configured drive value.
        velocity+=max(-1,min(1,command-velocity))
        position+=velocity*.05
        target+=rate*.05
    assert abs(target-position)<.001
    assert velocity==pytest.approx(rate,abs=.00001)


def test_nonfinite_feedback_is_rejected():
    with pytest.raises(ValueError):TrackingServo(20).velocity(10,float('nan'),0,0,.1)
