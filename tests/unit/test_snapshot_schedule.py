from datetime import datetime
import unittest
from vm_snapshot_schedule import checked,next_due,retention_candidates,Schedule
import test_snapshot_foundation as foundation


class ScheduleTests(unittest.TestCase):
    def test_policy_validation_has_no_hidden_service_or_unprotected_cleanup(self):
        for options in (dict(enabled=1),dict(preserve_protected=False),dict(keep_count=0),dict(time='25:00'),dict(frequency='cron'),dict(service=True)):
            with self.assertRaises(ValueError):checked(options)
        self.assertEqual(checked({})['when_offline'],'skip')

    def test_daily_weekly_and_interval_are_strictly_in_the_future(self):
        now=datetime(2026,10,22,12,0).timestamp()
        for frequency in ('daily','weekly','interval'):
            policy=checked(dict(frequency=frequency,time='12:00',weekday='mon',interval_days=2))
            due=next_due(policy,now);self.assertGreater(due,now)
            if frequency=='weekly':self.assertEqual(datetime.fromtimestamp(due).weekday(),0)
            if frequency=='interval':self.assertEqual(due-now,2*86400)

    def test_cleanup_excludes_manual_protected_checkpoint_partial_ram_and_external(self):
        groups=[dict(id=str(index),date=index,kind='disk',source='schedule',state='ready') for index in range(10)]
        groups[0]['protected']=True;groups[1]['checkpoint']=True;groups[2]['state']='partial';groups[3]['kind']='external';groups[4]['kind']='ram';groups[5]['source']='user'
        candidates=retention_candidates(groups,checked(dict(keep_count=2,keep_days=3650)),100)
        self.assertEqual([row['id'] for row in candidates],['7','6'])

    def test_missed_offline_occurrence_is_claimed_once_and_logged(self):
        fixture=foundation.SnapshotFoundationTests();fixture.setUp();self.addCleanup(fixture.doCleanups)
        service=fixture.service;service.processes=lambda:[]
        service.request=lambda *args:(_ for _ in ()).throw(ConnectionRefusedError())
        now=[1700000000];service.clock=lambda:now[0];schedule=Schedule(service)
        schedule.save(dict(enabled=True,frequency='interval',interval_days=1,when_offline='skip'),0)
        now[0]+=100*86400
        self.assertIsNone(schedule.tick());count=len(service.store.operations());self.assertIsNone(schedule.tick())
        self.assertEqual(len(service.store.operations()),count)
        self.assertTrue(any(row['action']=='schedule' and row['state']=='cancelled' for row in service.store.operations()))

    def test_stale_schedule_revision_does_not_replace_policy(self):
        fixture=foundation.SnapshotFoundationTests();fixture.setUp();self.addCleanup(fixture.doCleanups)
        schedule=Schedule(fixture.service);schedule.save(dict(enabled=False),0)
        with self.assertRaises(ValueError):schedule.save(dict(enabled=True),0)
        self.assertFalse(fixture.service.store.catalog()['settings']['schedule']['enabled'])
