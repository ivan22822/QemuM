from copy import deepcopy
from pathlib import Path
import tempfile
import unittest
from vm_snapshot_parameters import capture,restore,differences


class ParameterSnapshotTests(unittest.TestCase):
    def test_allowlist_preserves_argument_boundaries_and_excludes_runtime(self):
        data=dict(ram_mb=4096,cores=2,extra_args=['-device','some device,setting="a b"'],network_adapters=[dict(id='net0',type='user')],
                  qmp_port=12345,pid=23456,_runtime_accelerator='tcg',ui_color='#FFFFFF',label='Visual')
        saved=capture(data)
        self.assertEqual(saved['launch']['extra_args'],data['extra_args'])
        self.assertEqual(set(saved['launch']),{'ram_mb','cores','extra_args','network_adapters'})
        saved['launch']['extra_args'].append('changed');self.assertEqual(len(data['extra_args']),2)

    def test_secrets_are_refused_instead_of_saved_or_silently_redacted(self):
        for args in (['-object','secret,id=test,data=example'],['-chardev','socket,password=example']):
            with self.assertRaises(ValueError):capture(dict(extra_args=args))

    def test_restore_retains_local_identity_and_reports_diff(self):
        current=dict(ram_mb=1024,cores=1,ui_color='#FFFFFF',qmp_port=12345)
        saved=capture(dict(ram_mb=4096,cores=2,extra_args=['one','two words']))
        result=restore(saved,current)
        self.assertEqual(result['qmp_port'],12345);self.assertEqual(result['ui_color'],'#FFFFFF')
        self.assertEqual(result['extra_args'],['one','two words'])
        self.assertIn('ram_mb',{row['field'] for row in differences(saved,current)})

    def test_missing_resources_unknown_schema_and_runtime_injection_refused(self):
        with tempfile.TemporaryDirectory(dir=Path(__file__).resolve().parents[2]/'build') as folder:
            saved=capture(dict(ram_mb=128,cores=1,iso_path=str(Path(folder)/'missing.iso')))
            with self.assertRaises(ValueError):restore(saved,{})
        with self.assertRaises(ValueError):restore(dict(schema_version=2,launch={}),{})
        with self.assertRaises(ValueError):restore(dict(schema_version=1,launch=dict(qmp_port=12345)),{})
