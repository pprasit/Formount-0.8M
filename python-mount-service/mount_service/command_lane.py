import itertools
import queue
import threading
from concurrent.futures import Future

from .domain import DriveError


class CommandLane:
    """Priority command queue consumed only by the serial acquisition owner."""
    def __init__(self):
        self.pending = queue.PriorityQueue()
        self.counter = itertools.count()
        self.epoch = 0
        self.guard = threading.Lock()

    def submit(self, function, priority=10, invalidate=False):
        future = Future()
        with self.guard:
            if invalidate:
                self.epoch += 1
                # Reserve room for Stop by cancelling every older pending command.
                stops = []
                while True:
                    try:
                        item = self.pending.get_nowait()
                        if item[0] == 0:
                            stops.append(item)
                        elif not item[-1].done():
                            item[-1].set_exception(DriveError("Command cancelled by Stop"))
                    except queue.Empty:
                        break
                for item in stops:
                    self.pending.put_nowait(item)
            epoch = self.epoch
            if priority > 0 and self.pending.qsize() >= 128:
                future.set_exception(DriveError("Command queue is full"))
                return future
            try:
                self.pending.put_nowait((priority, next(self.counter), epoch, function, future))
            except queue.Full:
                future.set_exception(DriveError("Command queue is full"))
        return future

    def execute(self, maximum=4):
        for _ in range(maximum):
            try:
                priority, _, epoch, function, future = self.pending.get_nowait()
            except queue.Empty:
                return
            if future.cancelled():
                continue
            if priority > 0 and epoch != self.epoch:
                future.set_exception(DriveError("Command cancelled by Stop"))
                continue
            if not future.set_running_or_notify_cancel():
                continue
            try:
                future.set_result(function())
            except Exception as exc:
                future.set_exception(exc)

    def cancel(self):
        self.epoch += 1
        while True:
            try:
                *_, future = self.pending.get_nowait()
                future.cancel()
            except queue.Empty:
                return
