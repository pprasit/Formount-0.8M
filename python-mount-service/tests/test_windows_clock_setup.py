import unittest
from unittest.mock import patch,Mock
from mount_service.windows_clock import configure
class ClockSetupTests(unittest.TestCase):
 def test_saved_server_needs_no_elevation(self):
  with patch('mount_service.windows_clock.inspect',return_value={'configured':True}),patch('mount_service.windows_clock.subprocess.run') as run:
   self.assertTrue(configure('1.th.pool.ntp.org')['configured']);run.assert_not_called()
 def test_once_setup_elevates_only_helper(self):
  with patch('mount_service.windows_clock.is_admin',return_value=False),patch('mount_service.windows_clock.inspect',side_effect=[{'configured':False},{'configured':True}]),patch('mount_service.windows_clock.subprocess.run',return_value=Mock(returncode=0)) as run:
   self.assertTrue(configure('1.th.pool.ntp.org')['configured']);self.assertIn('request-windows-time.ps1',run.call_args.args[0][5]);self.assertEqual(run.call_count,1)
 def test_rejected_uac_is_not_reported_as_success(self):
  with patch('mount_service.windows_clock.is_admin',return_value=False),patch('mount_service.windows_clock.inspect',return_value={'configured':False}),patch('mount_service.windows_clock.subprocess.run',return_value=Mock(returncode=4)):
   with self.assertRaisesRegex(ValueError,'not approved'):configure('1.th.pool.ntp.org')
if __name__=='__main__':unittest.main()
