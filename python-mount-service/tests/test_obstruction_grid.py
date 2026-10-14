import unittest
from mount_service.pointing_workbench import PointingWorkbench
class ObstructionGridTests(unittest.TestCase):
 def test_blocked_points_remain_visible_and_cannot_be_enabled(self):
  w=PointingWorkbench.__new__(PointingWorkbench)
  w.idle_required=lambda:None;w.save=lambda:None;w.snapshot=lambda:{'plan':w.plan}
  w.config={'blind_spots':[{'name':'Tree','vertices':[[0,0],[45,0],[45,40],[0,40]]}]}
  w.settings={'azimuth_points':4,'altitude_points':2,'min_altitude':10,'max_altitude':60}
  w.plan=[]
  rows=w.create_grid()['plan'];self.assertEqual(len(rows),8)
  blocked=[p for p in rows if p.get('blind_spot')];self.assertEqual(len(blocked),1)
  self.assertEqual(blocked[0]['status'],'skipped');self.assertFalse(blocked[0]['included'])
  with self.assertRaises(ValueError):w.select(blocked[0]['id'],True)
if __name__=='__main__':unittest.main()
