import sys, unittest
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parents[2]/'tools'))
from publish_patch import ci_ready
class PublicationChecks(unittest.TestCase):
    def setUp(self):
        self.env = dict(GITHUB_ACTIONS='true', GITHUB_SHA='tested', GITHUB_JOB='publish-alpha7-build1', GITHUB_RUN_ID='123')
        self.ok = dict(name='windows', status='completed', conclusion='success')
        self.own = dict(name='publish-alpha7-build1', status='in_progress', conclusion=None, details_url='https://github.com/ivan22822/QemuM/actions/runs/123/job/4')
    def test_completed_ci_is_required(self):
        self.assertFalse(ci_ready([], 'tested', {}))
        self.assertTrue(ci_ready([self.ok], 'tested', {}))
        self.assertFalse(ci_ready([dict(self.ok, conclusion='failure')], 'tested', {}))
    def test_only_own_publication_job_can_be_pending(self):
        self.assertTrue(ci_ready([self.ok, self.own], 'tested', self.env))
        self.assertFalse(ci_ready([self.own], 'tested', self.env))
        self.assertFalse(ci_ready([self.ok, dict(self.own, name='other')], 'tested', self.env))
    def test_wrong_commit_run_and_external_process_are_rejected(self):
        self.assertFalse(ci_ready([self.ok, self.own], 'other', self.env))
        self.assertFalse(ci_ready([self.ok, self.own], 'tested', dict(self.env, GITHUB_RUN_ID='456')))
        self.assertFalse(ci_ready([self.ok, self.own], 'tested', {}))
