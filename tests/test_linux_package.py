from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]


@unittest.skipUnless(shutil.which('dpkg-deb') and shutil.which('sh'), 'Debian packaging tools required')
class LinuxPackageTests(unittest.TestCase):
    def test_package_retires_timer_and_migrates_existing_installations(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            shutil.copytree(ROOT / 'linux', root / 'linux', ignore=shutil.ignore_patterns('build', '__pycache__'))
            (root / 'windows').mkdir()
            shutil.copy(ROOT / 'windows/bSideDesk.png', root / 'windows')
            shutil.copy(ROOT / 'README.md', root)
            subprocess.run(['sh', str(root / 'linux/build-deb.sh'), '1.0.999'], check=True, capture_output=True)
            package = root / 'linux/build/bsidedesk_1.0.999_all.deb'
            contents = subprocess.check_output(['dpkg-deb', '-c', str(package)], text=True)
            self.assertNotIn('bsidedesk-update.timer', contents)
            self.assertNotIn('bsidedesk-update.service', contents)
            postinst = (root / 'linux/build/pkg/DEBIAN/postinst').read_text()
            self.assertNotIn('enable', postinst)
            # Exercise the packaged migration on a simulated systemd host.
            # Only the host-detection guard is replaced; systemctl is a harmless stub.
            postinst = postinst.replace('[ -d /run/systemd/system ]', 'true')
            prefix = 'systemctl() { echo "$@" >> "$CALLS"; }; set -- configure; '
            calls = root / 'calls'
            import os
            subprocess.run(['sh', '-c', prefix + postinst], check=True,
                           env=dict(os.environ, CALLS=str(calls)))
            self.assertEqual(calls.read_text().splitlines(), [
                'stop bsidedesk-update.timer', 'disable bsidedesk-update.timer', 'daemon-reload'])
            # Do not kill the updater service: it may be installing this package.


if __name__ == '__main__':
    unittest.main()
