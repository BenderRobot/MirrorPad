#!/usr/bin/env python3
"""Installe bSideDesk sur l'iPad (SSH, même clé que les essais).

  python3 deploy.py                      # installe le .deb le plus récent de build/
  python3 deploy.py --deb chemin.deb --save   # installe et garde une copie pour le rattrapage
  python3 deploy.py --if-needed          # (cron) installe la copie gardée seulement si l'iPad est en retard
"""
import argparse, shutil, subprocess, sys
from pathlib import Path

KEY = '/home/bender/projets/bTV-IOS/outils/ipad-diagnostic'
SAVED = Path.home() / '.bsidedesk' / 'latest.deb'
SSH_OPTS = ['-i', KEY, '-o', 'BatchMode=yes', '-o', 'StrictHostKeyChecking=yes', '-o', 'ConnectTimeout=5']

def ssh(ipad, cmd, timeout=60):
    return subprocess.run(['ssh', *SSH_OPTS, f'root@{ipad}', cmd], capture_output=True, text=True, timeout=timeout)

def deb_version(deb):
    return subprocess.run(['dpkg-deb', '-f', str(deb), 'Version'], capture_output=True, text=True, check=True).stdout.strip()

def install(ipad, deb):
    want = deb_version(deb)
    subprocess.run(['scp', *SSH_OPTS, str(deb), f'root@{ipad}:/var/root/bsidedesk.deb'], check=True, timeout=60)
    # installe, rafraîchit l'écran d'accueil, puis relance l'app si elle tournait
    # ancien nom (MirrorPad) : on le retire après l'installation ; ses réglages restent dans
    # /var/mobile/Library/Preferences et bSideDesk les reprend à son premier lancement
    cmd = ('set -e; dpkg -i /var/root/bsidedesk.deb; uicache -p /Applications/bSideDesk.app; '
           'if dpkg -s local.btv.mirrorpad >/dev/null 2>&1; then killall MirrorPad 2>/dev/null || true; dpkg -r local.btv.mirrorpad; '
           'uicache -u /Applications/MirrorPad.app 2>/dev/null || uicache 2>/dev/null; uiopen bsidedesk:// 2>/dev/null || true; fi; '
           'if killall bSideDesk 2>/dev/null; then sleep 1; uiopen bsidedesk:// 2>/dev/null || true; fi')
    r = ssh(ipad, cmd)
    print(r.stdout, r.stderr)
    if r.returncode:
        return r.returncode
    installed = ssh(ipad, "dpkg-query -W -f='${Version}' local.btv.bsidedesk", timeout=15)
    if installed.returncode:
        return installed.returncode
    if installed.stdout.strip() != want:
        print(f'Version inattendue après installation : {installed.stdout.strip()!r}, attendu {want!r}')
        return 1
    return 0

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--ipad', default='192.168.1.130')
    ap.add_argument('--deb')
    ap.add_argument('--save', action='store_true')
    ap.add_argument('--if-needed', action='store_true')
    ap.add_argument('--allow-offline', action='store_true', help="ignorer uniquement un iPad injoignable avant l'installation")
    a = ap.parse_args()

    if a.if_needed:
        if not SAVED.exists():
            return 0
        want = deb_version(SAVED)
        try:
            r = ssh(a.ipad, "dpkg-query -W -f='${Version}' local.btv.bsidedesk", timeout=15)
        except subprocess.TimeoutExpired:
            return 0
        if r.returncode == 255:        # iPad injoignable : on réessaiera plus tard
            return 0
        if r.stdout.strip() == want:
            return 0
        print(f'iPad en {r.stdout.strip() or "?"} -> installation de {want}')
        return install(a.ipad, SAVED)

    deb = Path(a.deb) if a.deb else max((Path(__file__).resolve().parent / 'build').glob('bsidedesk_*.deb'), key=lambda p: p.stat().st_mtime)
    if a.save:
        SAVED.parent.mkdir(exist_ok=True)
        shutil.copy(deb, SAVED)
    if a.allow_offline:
        try:
            probe = ssh(a.ipad, 'true', timeout=15)
        except subprocess.TimeoutExpired:
            print('iPad injoignable : installation différée au prochain rattrapage')
            return 0
        if probe.returncode == 255:
            print('Connexion SSH indisponible : installation différée au prochain rattrapage')
            return 0
        if probe.returncode:
            return probe.returncode
    rc = install(a.ipad, deb)
    print('DEPLOY_STATUS', rc)
    return rc

if __name__ == '__main__':
    sys.exit(main())
