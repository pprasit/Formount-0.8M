import unittest
from mount_service.blind_spots import validate,contains
class BlindSpotsTests(unittest.TestCase):
 def test_seam_and_boundary(self):
  rows=validate([{'name':'Trees','vertices':[[350,5],[10,5],[10,30],[350,30]]}])
  self.assertTrue(contains(rows,0,20));self.assertTrue(contains(rows,350,20));self.assertFalse(contains(rows,180,20));self.assertFalse(contains(rows,0,40))
 def test_invalid_coordinates(self):
  for v in [[[0,-1],[20,5],[30,20]],[[0,0],[10,0],[20,0]],[[0,0],[float('nan'),10],[20,30]],[[0,5],[40,35],[0,35],[35,5]]]:
   with self.assertRaises(ValueError):validate([{'vertices':v}])
if __name__=='__main__':unittest.main()
