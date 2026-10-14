"""A build job outlives its browser request; reconnecting reads the same status."""
import asyncio
import uuid

class InterestingAIJobs:
    def __init__(self):
        self.state={'id':None,'active':False,'state':'idle','percent':0,'message':'Ready to build a list.'}
        self.task=None
    def snapshot(self):return dict(self.state)
    def start(self,build,path,site,settings):
        if self.state['active']:return dict(self.state,attached=True)
        self.state={'id':uuid.uuid4().hex,'active':True,'state':'running','percent':0,'message':'Preparing the observing list…'}
        loop=asyncio.get_running_loop()
        def progress(percent,message):loop.call_soon_threadsafe(self.state.update,{'percent':percent,'message':message})
        async def work():
            try:
                result=await asyncio.to_thread(build,path,site,settings,progress)
                self.state.update(active=False,state='completed',percent=100,message=f"Completed — added {result['added']} objects.",result=result)
            except ValueError as exc:self.state.update(active=False,state='error',message=str(exc))
            except Exception:self.state.update(active=False,state='error',message='Unable to prepare the observing catalog. Please try again.')
        self.task=asyncio.create_task(work())
        return self.snapshot()
