import json
import sys
import tempfile
from pathlib import Path
import unittest

sys.path.insert(0,str(Path(__file__).resolve().parents[2]/'src/backend'))
import vm_profile_store as store

class ProfileDescriptionTests(unittest.TestCase):
    def test_all_builtins_explain_their_purpose(self):
        self.assertTrue(all(len(row['description'])>80 for row in store.defaults()))

    def test_custom_description_survives_reload_and_edit(self):
        with tempfile.TemporaryDirectory() as folder:
            root=Path(folder)
            row=dict(id=None,label='Profile',description='Назначение\nОграничения',settings=dict(accelerator='tcg',cpu='max',options=''))
            result=store.change(root,store.read(root)['hash'],row)
            saved=next(item for item in result['profiles'] if item['id']==result['selected_id'])
            self.assertEqual(saved['description'],row['description'])
            saved['description']='Изменено'
            updated=store.change(root,result['hash'],saved)
            self.assertEqual(next(item for item in updated['profiles'] if item['id']==saved['id'])['description'],'Изменено')

    def test_old_catalog_and_invalid_description(self):
        with tempfile.TemporaryDirectory() as folder:
            root=Path(folder)
            row=store.defaults()[0];row.pop('description')
            (root/'launch-profiles.json').write_text(json.dumps([row]),encoding='utf-8')
            self.assertTrue(store.read(root)['profiles'][0]['description'])
            for value in (None,42,'x'*2001,'bad\x00text'):
                with self.assertRaises(ValueError):store.validate_row(dict(row,description=value))
