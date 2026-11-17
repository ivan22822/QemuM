import sys, unittest
from pathlib import Path
sys.path.append(str(Path(__file__).resolve().parents[2]/'tools'))
from publish_patch import ci_ready, archived_metadata
class PublicationChecks(unittest.TestCase):
    def test_release_cleanup_preserves_live_feeds_packages_and_installers(self):
        for name in ('update-build.1.json','update-before-build.3.json','SHA256SUMS-build.2.txt','SHA256SUMS-before-build.1.txt'):
            self.assertTrue(archived_metadata(name), name)
        for name in ('update.json','SHA256SUMS.txt','QemuM-0.1.0-alpha.7-app.zip','QemuM-0.1.0-alpha.7-build.1-app.zip','QemuM-0.1.0-alpha.7-Setup.exe','QemuM.exe','QemuM-Refresh.exe','update-build.4.json.extra','../update-build.4.json'):
            self.assertFalse(archived_metadata(name), name)
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
