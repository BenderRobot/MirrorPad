#!/usr/bin/env python3
"""Installe MirrorPad sur l'iPad (SSH, même clé que les essais).

  python3 deploy.py                      # installe le .deb le plus récent de build/
  python3 deploy.py --deb chemin.deb --save   # installe et garde une copie pour le rattrapage
  python3 deploy.py --if-needed          # (cron) installe la copie gardée seulement si l'iPad est en retard
"""
import argparse, shutil, subprocess, sys
from pathlib import Path

KEY = '/home/bender/projets/bTV-IOS/outils/ipad-diagnostic'
SAVED = Path.home() / '.mirrorpad' / 'latest.deb'
SSH_OPTS = ['-i', KEY, '-o', 'BatchMode=yes', '-o', 'StrictHostKeyChecking=yes', '-o', 'ConnectTimeout=5']

def ssh(ipad, cmd, timeout=60):
    return subprocess.run(['ssh', *SSH_OPTS, f'root@{ipad}', cmd], capture_output=True, text=True, timeout=timeout)

def deb_version(deb):
    return subprocess.run(['dpkg-deb', '-f', str(deb), 'Version'], capture_output=True, text=True, check=True).stdout.strip()

def install(ipad, deb):
    subprocess.run(['scp', *SSH_OPTS, str(deb), f'root@{ipad}:/var/root/mirrorpad.deb'], check=True, timeout=60)
    # installe, rafraîchit l'écran d'accueil, puis relance l'app si elle tournait
    cmd = ('dpkg -i /var/root/mirrorpad.deb && uicache -p /Applications/MirrorPad.app; '
           'if killall MirrorPad 2>/dev/null; then sleep 1; uiopen mirrorpad:// 2>/dev/null || true; fi')
    r = ssh(ipad, cmd)
    print(r.stdout, r.stderr)
    return r.returncode

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--ipad', default='192.168.1.130')
    ap.add_argument('--deb')
    ap.add_argument('--save', action='store_true')
    ap.add_argument('--if-needed', action='store_true')
    a = ap.parse_args()

    if a.if_needed:
        if not SAVED.exists():
            return 0
        want = deb_version(SAVED)
        try:
            r = ssh(a.ipad, "dpkg-query -W -f='${Version}' local.btv.mirrorpad", timeout=15)
        except subprocess.TimeoutExpired:
            return 0
        if r.returncode == 255:        # iPad injoignable : on réessaiera plus tard
            return 0
        if r.stdout.strip() == want:
            return 0
        print(f'iPad en {r.stdout.strip() or "?"} -> installation de {want}')
        return install(a.ipad, SAVED)

    deb = Path(a.deb) if a.deb else max((Path(__file__).resolve().parent / 'build').glob('mirrorpad_*.deb'), key=lambda p: p.stat().st_mtime)
    if a.save:
        SAVED.parent.mkdir(exist_ok=True)
        shutil.copy(deb, SAVED)
    rc = install(a.ipad, deb)
    print('DEPLOY_STATUS', rc)
    return rc

if __name__ == '__main__':
    sys.exit(main())
