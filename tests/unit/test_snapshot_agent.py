"""Independent guardian state machine, no real guest or guest installation."""
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import uuid
import vm_snapshot_agent as agent
from vm_snapshot_store import Store


class GuardianTests(unittest.TestCase):
    def run_guardian(self,foreign=False,freeze_error=False,thaw_error=False,parent_dead=False):
        temp=tempfile.TemporaryDirectory(dir=Path(__file__).resolve().parents[2]/'build');self.addCleanup(temp.cleanup)
        store=Store(Path(temp.name)/'machines','test');operation_id=str(uuid.uuid4());calls=[]
        class Parent:
            creation_time=123
            def __init__(self,*args):pass
            def poll(self):return 1 if parent_dead else None
            def close(self):pass
        class Channel:
            state='frozen' if foreign else 'thawed'
            def __init__(self,*args):pass
            def call(self,command,*args):
                calls.append(command)
                if command=='guest-fsfreeze-status':return self.state
                if command=='guest-fsfreeze-freeze':
                    self.state='frozen'
                    if freeze_error:raise TimeoutError('lost freeze reply')
                    return 1
                if command=='guest-fsfreeze-thaw':
                    if thaw_error:raise TimeoutError('lost thaw reply')
                    self.state='thawed';return 1
            def close(self):pass
        now=[0]
        def sleep(seconds):now[0]+=seconds
        with patch.object(agent,'AgentChannel',Channel),patch.object(agent,'ProcessWatcher',Parent),patch.object(agent.time,'monotonic',lambda:now[0]),patch.object(agent.time,'sleep',sleep):
            agent.guardian(store,operation_id,12345,23456,34567,os.getpid(),123,2)
        status=Store.read(store.folder/'agent'/(operation_id+'.json'))
        return status,calls

    def test_timeout_thaws_owned_freeze(self):
        status,calls=self.run_guardian();self.assertEqual(status['state'],'thawed');self.assertEqual(calls.count('guest-fsfreeze-thaw'),1)

    def test_parent_exit_thaws_owned_freeze(self):
        status,calls=self.run_guardian(parent_dead=True);self.assertEqual(status['state'],'thawed')

    def test_foreign_freeze_is_never_thawed(self):
        status,calls=self.run_guardian(foreign=True)
        self.assertEqual(status['state'],'error');self.assertNotIn('guest-fsfreeze-thaw',calls)

    def test_lost_freeze_reply_still_triggers_thaw(self):
        status,calls=self.run_guardian(freeze_error=True);self.assertEqual(status['state'],'thawed')

    def test_failed_thaw_is_durable_and_not_reported_as_success(self):
        status,calls=self.run_guardian(thaw_error=True);self.assertEqual(status['state'],'thaw-required')
        self.assertEqual(calls.count('guest-fsfreeze-thaw'),2)

    def test_no_arbitrary_guest_commands(self):
        with self.assertRaises(ValueError):agent.AgentChannel.call(None,'guest-exec')
