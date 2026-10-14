import asyncio
import threading
import unittest
from mount_service.interesting_ai_jobs import InterestingAIJobs

class JobTests(unittest.IsolatedAsyncioTestCase):
    async def test_reconnect_and_duplicate_attach(self):
        started=threading.Event();release=threading.Event();calls=[]
        def build(path,site,settings,progress):
            calls.append(settings);progress(80,'Selecting objects');started.set();release.wait(3)
            return {'added':1,'objects':[{'id':'test'}]}
        jobs=InterestingAIJobs();first=jobs.start(build,None,None,{'count':1})
        await asyncio.to_thread(started.wait,2);await asyncio.sleep(.01)
        second=jobs.start(build,None,None,{'count':99})
        self.assertEqual(first['id'],second['id']);self.assertTrue(second['attached'])
        self.assertEqual(jobs.snapshot()['percent'],80)
        release.set();await jobs.task
        self.assertEqual(len(calls),1);self.assertFalse(jobs.snapshot()['active'])
        self.assertEqual(jobs.snapshot()['result']['added'],1)
    async def test_error_releases_job(self):
        def fail(*args):raise ValueError('Invalid list')
        jobs=InterestingAIJobs();jobs.start(fail,None,None,{})
        await jobs.task
        self.assertEqual(jobs.snapshot()['state'],'error');self.assertFalse(jobs.snapshot()['active'])
if __name__=='__main__':unittest.main()
