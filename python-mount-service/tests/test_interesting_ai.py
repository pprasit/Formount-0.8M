import json
import os
import unittest
from pathlib import Path
from tempfile import TemporaryDirectory
from unittest.mock import patch
from mount_service.interesting_ai import shortlist,build,choose
from mount_service.interesting_objects import list_objects,save_object

SITE={'latitude':18.853823,'longitude':98.958353,'elevation':350}
class AIListTests(unittest.TestCase):
    def test_night_visibility(self):
        with TemporaryDirectory() as d:
            rows,start,end=shortlist(SITE,{'planet','galaxy'},[],5,Path(d)/'test.db')
            self.assertTrue(rows)
            self.assertTrue(all(r['night_max_altitude']>10 and r.get('body')!='sun' for r in rows))
            self.assertLess(start,end)
    def test_evening_score_declines_after_23_ict(self):
        from astropy.time import Time
        from mount_service.interesting_ai import observing_score
        times=Time(['2026-10-13T13:00:00','2026-10-13T16:00:00','2026-10-13T18:00:00','2026-10-13T21:00:00'])
        evening=observing_score(times,[50,50,-10,-10])
        late=observing_score(times,[-10,-10,50,-10])
        dawn=observing_score(times,[-10,-10,-10,50])
        self.assertTrue(evening['evening_visible'])
        self.assertFalse(late['evening_visible'])
        self.assertGreater(evening['observing_score'],late['observing_score'])
        self.assertGreater(late['observing_score'],dawn['observing_score'])
        self.assertIsNone(observing_score(times,[50,50,50,50],[False]*4))
    def test_night_samples_exclude_daylight(self):
        from datetime import datetime,timezone
        from astropy.coordinates import get_sun,AltAz
        import astropy.units as u
        from mount_service.interesting_ai import night_times
        times,location=night_times(SITE,datetime(2026,10,13,5,tzinfo=timezone.utc))
        sun_alt=get_sun(times).transform_to(AltAz(obstime=times,location=location,pressure=0*u.hPa)).alt.deg
        self.assertTrue((sun_alt<-6).all())
    def test_merge_deduplicate_and_ai_flag(self):
        with TemporaryDirectory() as d,patch.dict(os.environ,{'NARIT_LLLM_BASE_URL':'https://example.invalid/v1','NARIT_LLLM_MODEL':'test','NARIT_LLLM_API_KEY':'test-only-credential'}):
            path=Path(d)/'test.db';save_object(path,{'interest_id':'sky:existing','id':'existing','name':'Existing'})
            row={'id':'new','name':'New','category':'galaxy','ra_hours':1,'dec_deg':2}
            with patch('mount_service.interesting_ai.shortlist',return_value=([row],'start','end')),patch('mount_service.interesting_ai.choose',return_value=[row]):
                result=build(path,SITE,{'count':1,'categories':['galaxy']})
                self.assertEqual(result['added'],1)
                self.assertEqual(len(result['objects']),2)
                self.assertTrue(result['objects'][1]['ai_selected'])
                self.assertEqual(build(path,SITE,{'count':1,'categories':['galaxy']})['added'],0)
    def test_invalid_model_ids_do_not_write(self):
        class Response:
            def __enter__(self):return self
            def __exit__(self,*args):pass
            def read(self,*args):return json.dumps({'choices':[{'message':{'content':'{"ids":["999"]}'}}]}).encode()
        with patch.dict(os.environ,{'NARIT_LLLM_BASE_URL':'https://example.invalid/v1','NARIT_LLLM_MODEL':'test','NARIT_LLLM_API_KEY':'test-only-credential'}),patch('mount_service.interesting_ai.urlopen',return_value=Response()),patch('mount_service.interesting_ai.available_models',return_value=['test']):
            with self.assertRaises(ValueError):choose([{'name':'Test','category':'galaxy','night_max_altitude':50,'best_utc':'now'}],1,SITE,'start','end')
    def test_alternative_model_has_output_budget(self):
        responses=[{'choices':[{'finish_reason':'length','message':{'content':''}}]}, {'choices':[{'finish_reason':'stop','message':{'content':'{"ids":["0"]}'}}]}]
        calls=[]
        def request(endpoint,token,body):
            calls.append((body['model'],body['max_tokens']))
            return responses.pop(0)
        with patch('mount_service.interesting_ai.connection',return_value=('https://example.invalid/v1','Anthropic-NARIT-Local-AI-01','test-only')),patch('mount_service.interesting_ai.available_models',return_value=['Anthropic-NARIT-Local-AI-01','OpenAI-NARIT-Local-AI-01']),patch('mount_service.interesting_ai.model_request',side_effect=request):
            selected=choose([{'name':'Test','category':'galaxy','night_max_altitude':50,'best_utc':'now'}],1,SITE,'start','end')
            self.assertEqual(len(selected),1)
            self.assertEqual(calls[1],('OpenAI-NARIT-Local-AI-01',16384))
    def test_invalid_count_is_corrected_with_bounded_retries(self):
        responses=[{'choices':[{'finish_reason':'stop','message':{'content':'{"ids":[]}'}}]}, {'choices':[{'finish_reason':'length','message':{'content':''}}]}, {'choices':[{'finish_reason':'stop','message':{'content':'{"ids":["0"]}'}}]}]
        with patch('mount_service.interesting_ai.connection',return_value=('https://example.invalid/v1','test','test-only')),patch('mount_service.interesting_ai.available_models',return_value=['test']),patch('mount_service.interesting_ai.model_request',side_effect=responses) as call:
            selected=choose([{'name':'Test','category':'galaxy','night_max_altitude':50,'best_utc':'now'}],1,SITE,'start','end')
            self.assertEqual(len(selected),1)
            self.assertEqual(call.call_count,3)
    def test_connection_defaults_and_missing_credential(self):
        from mount_service.interesting_ai import connection
        with patch.dict(os.environ,{},clear=True):
            with self.assertRaisesRegex(ValueError,'NARIT_LLLM_API_KEY'):connection()
        with patch.dict(os.environ,{'NARIT_LLLM_API_KEY':'test-only-credential'},clear=True):
            base,model,_=connection()
            self.assertEqual(base,'https://lllm.narit.or.th/v1')
            self.assertEqual(model,'Anthropic-NARIT-Local-AI-01')
    def test_limited_retry_and_auth_failure(self):
        from mount_service.interesting_ai import model_request
        from urllib.error import HTTPError
        with patch('mount_service.interesting_ai.urlopen',side_effect=HTTPError('https://example.invalid',429,'busy',{},None)) as call,patch('mount_service.interesting_ai.time.sleep'):
            with self.assertRaisesRegex(ValueError,'busy'):model_request('https://example.invalid','test-only-credential',{})
            self.assertEqual(call.call_count,3)
        with patch('mount_service.interesting_ai.urlopen',side_effect=HTTPError('https://example.invalid',401,'unauthorized',{},None)) as call:
            with self.assertRaisesRegex(ValueError,'authentication'):model_request('https://example.invalid','test-only-credential',{})
            self.assertEqual(call.call_count,1)
if __name__=='__main__':unittest.main()
