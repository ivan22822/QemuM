import importlib.util
from pathlib import Path
import unittest
from unittest.mock import patch,Mock
import tempfile,json

ROOT=Path(__file__).resolve().parents[2]
spec=importlib.util.spec_from_file_location('independent_baseline',ROOT/'tools/run_idle_baseline_independent.py')
module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module)

class IndependentBaselineTests(unittest.TestCase):
 def test_complete_continuous_window(self):
  data=dict(elapsed_idle_seconds=1800,idle_samples=[dict(elapsed_seconds=i) for i in range(0,1801,5)])
  self.assertTrue(module.valid_window(data))
 def test_interrupted_window_is_not_accepted(self):
  self.assertFalse(module.valid_window(dict(elapsed_idle_seconds=235,idle_samples=[])))
 def test_sleep_gap_is_not_accepted(self):
  values=list(range(0,1801,5));values=[v for v in values if not 600<=v<=630]
  self.assertFalse(module.valid_window(dict(elapsed_idle_seconds=1800,idle_samples=[dict(elapsed_seconds=v) for v in values])))
 def test_sparse_samples_are_not_accepted(self):
  self.assertFalse(module.valid_window(dict(elapsed_idle_seconds=1800,idle_samples=[dict(elapsed_seconds=i) for i in range(0,1801,20)])))
 def test_telemetry_flags_are_process_local(self):
  env=module.environment()
  self.assertEqual(env['COR_ENABLE_PROFILING'],'0')
  self.assertEqual(env['OTEL_TRACES_EXPORTER'],'none')
  self.assertNotIn('APPINSIGHTS_INSTRUMENTATIONKEY',env)

 def test_desktop_job_membership_does_not_block_start(self):
  process=Mock(pid=12345);state=dict(status='running',started_utc='fixture',pid=12345)
  with tempfile.TemporaryDirectory() as folder, patch.object(module,'OUT',Path(folder)), patch.object(module,'in_job',return_value=True), patch.object(module.subprocess,'Popen',return_value=process) as popen, patch.object(module,'wait_for_worker',return_value=state):
   module.start()
   popen.assert_called_once()
   self.assertNotIn('CREATE_BREAKAWAY_FROM_JOB',str(popen.call_args))
   receipt=json.loads((Path(folder)/'independent-launch.json').read_text())
   self.assertTrue(receipt['startup_confirmed'])
 def test_duplicate_live_worker_is_not_started_again(self):
  with tempfile.TemporaryDirectory() as folder, patch.object(module,'OUT',Path(folder)), patch.object(module,'process_alive',return_value=True), patch.object(module.subprocess,'Popen') as popen:
   (Path(folder)/'independent-run.json').write_text(json.dumps(dict(status='running',pid=12345)))
   module.start();popen.assert_not_called()
 def test_launch_receipt_requires_worker_confirmation(self):
  with tempfile.TemporaryDirectory() as folder, patch.object(module,'OUT',Path(folder)), patch.object(module.subprocess,'Popen',return_value=Mock(pid=12345)), patch.object(module,'wait_for_worker',side_effect=RuntimeError('no acknowledgement')):
   with self.assertRaisesRegex(RuntimeError,'no acknowledgement'):module.start()
   self.assertFalse((Path(folder)/'independent-launch.json').exists())
