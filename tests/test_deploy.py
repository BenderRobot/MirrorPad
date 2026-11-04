import importlib.util
from pathlib import Path
import shutil
import subprocess
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('deploy', ROOT / 'ipad/deploy.py')
deploy = importlib.util.module_from_spec(spec)
spec.loader.exec_module(deploy)


def result(code=0, stdout=''):
    return subprocess.CompletedProcess([], code, stdout, '')


class DeployTests(unittest.TestCase):
    def install(self, replies):
        with patch.object(deploy, 'deb_version', return_value='1.0.123'), \
             patch.object(deploy.subprocess, 'run'), \
             patch.object(deploy, 'ssh', side_effect=replies) as ssh:
            rc = deploy.install('test-ipad', Path('test.deb'))
            return rc, ssh.call_count

    def test_failed_install_is_not_success(self):
        self.assertEqual(self.install([result(42)]), (42, 1))

    def test_installed_version_must_match(self):
        self.assertEqual(self.install([result(), result(stdout='old')]), (1, 2))
        self.assertEqual(self.install([result(), result(stdout='1.0.123\n')]), (0, 2))
        self.assertEqual(self.install([result(), result(7)]), (7, 2))

    def test_real_remote_shell_preserves_dpkg_failure(self):
        shell = shutil.which('sh')
        if not shell:
            candidate = Path(r'C:\Program Files\Git\bin\bash.exe')
            if candidate.exists():
                shell = str(candidate)
        if not shell:
            self.skipTest('POSIX shell unavailable')
        # Execute the real command with harmless shell functions replacing tools.
        real_run = subprocess.run
        def remote(ipad, cmd, timeout=60):
            prefix = 'dpkg() { return 42; }; uicache() { echo SHOULD_NOT_RUN; }; '
            r = real_run([shell, '-c', prefix + cmd], capture_output=True, text=True)
            self.assertNotIn('SHOULD_NOT_RUN', r.stdout)
            return r
        with patch.object(deploy, 'deb_version', return_value='1.0.123'), \
             patch.object(deploy.subprocess, 'run'), patch.object(deploy, 'ssh', side_effect=remote):
            self.assertEqual(deploy.install('test-ipad', Path('test.deb')), 42)

    def test_offline_is_deferred_only_before_install(self):
        args = ['deploy.py', '--deb', 'test.deb', '--allow-offline']
        with patch('sys.argv', args), patch.object(deploy, 'ssh', return_value=result(255)), \
             patch.object(deploy, 'install') as install:
            self.assertEqual(deploy.main(), 0)
            install.assert_not_called()
        with patch('sys.argv', args), patch.object(deploy, 'ssh', return_value=result()), \
             patch.object(deploy, 'install', return_value=42):
            self.assertEqual(deploy.main(), 42)


if __name__ == '__main__':
    unittest.main()
