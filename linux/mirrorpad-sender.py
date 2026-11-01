#!/usr/bin/env python3
"""MirrorPad pour Linux (session X11) : l'iPad devient un écran supplémentaire tactile.

Crée un moniteur virtuel à droite des écrans existants (xrandr --setmonitor), le capture avec
ffmpeg (x11grab), l'envoie à l'app MirrorPad de l'iPad et rejoue les gestes avec xdotool.

Prérequis : sudo apt install ffmpeg xdotool x11-xserver-utils
Usage     : python3 mirrorpad-sender.py [--ipad 192.168.1.130] [--fps 60] [--mbps 12]
Wayland n'est pas pris en charge (ouvrir une session « GNOME sur Xorg » / X11).
"""
import argparse, json, os, select, signal, socket, subprocess, sys, threading, time, urllib.parse
from pathlib import Path

VIDEO_PORT, INPUT_PORT, MONITOR = 7270, 7271, 'MirrorPad'
CONFIG = Path.home() / '.config' / 'mirrorpad' / 'config.json'
ENCODER_NAMES = {'vaapi': 'VA-API (carte graphique)', 'x264': 'Processeur (x264)'}


def log(msg):
    print(time.strftime('%H:%M:%S'), msg, flush=True)


def run(*args):
    return subprocess.run(list(args), capture_output=True, text=True)


# --- Configuration -------------------------------------------------------------------
def load_config(args):
    cfg = {'ipad': '', 'fps': 30, 'mbps': 12, 'encoder': 'auto', 'detected': '', 'width': 2048, 'height': 1536}
    try:
        cfg.update(json.loads(CONFIG.read_text()))
    except Exception:
        pass
    for k in ('ipad', 'fps', 'mbps', 'encoder'):
        if getattr(args, k) is not None:
            cfg[k] = getattr(args, k)
    return cfg


def save_config(cfg):
    CONFIG.parent.mkdir(parents=True, exist_ok=True)
    CONFIG.write_text(json.dumps(cfg, indent=2))


# --- Détection de l'iPad (mDNS « _mirrorpad._tcp.local ») -------------------------------
def find_ipad(timeout=1.5):
    q = bytes([0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0])
    for label in ('_mirrorpad', '_tcp', 'local'):
        q += bytes([len(label)]) + label.encode()
    q += bytes([0, 0, 12, 0x80, 1])
    addrs = [a for a in run('hostname', '-I').stdout.split() if '.' in a] or ['0.0.0.0']
    socks = []
    for a in addrs:
        try:
            s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
            s.bind((a, 0))
            s.setsockopt(socket.IPPROTO_IP, socket.IP_MULTICAST_IF, socket.inet_aton(a))
            s.sendto(q, ('224.0.0.251', 5353))
            socks.append(s)
        except OSError:
            pass
    found, end = None, time.time() + timeout
    while socks and not found and time.time() < end:
        ready, _, _ = select.select(socks, [], [], 0.1)
        for s in ready:
            data, src = s.recvfrom(4096)
            if b'_mirrorpad' in data:
                found = src[0]
                break
    for s in socks:
        s.close()
    return found


# --- Moniteur virtuel X11 ---------------------------------------------------------------
class VirtualMonitor:
    def __init__(self, w, h):
        self.w, self.h, self.x, self.base = w, h, 0, None

    @staticmethod
    def screen_size():
        for line in run('xrandr', '--current').stdout.splitlines():
            if 'current' in line:
                part = line.split('current')[1].split(',')[0]
                w, h = part.replace(' ', '').split('x')
                return int(w), int(h)
        raise RuntimeError('xrandr ne répond pas (session X11 requise, variable DISPLAY ?)')

    def create(self):
        if self.base is None:
            run('xrandr', '--delmonitor', MONITOR)
            self.base = self.screen_size()
        bw, bh = self.base
        self.x = bw
        mm = lambda px: int(px * 25.4 / 264)  # densité de l'écran iPad (264 ppp)
        r = run('xrandr', '--fb', f'{bw + self.w}x{max(bh, self.h)}')
        if r.returncode:
            raise RuntimeError('xrandr --fb a échoué : ' + r.stderr.strip())
        run('xrandr', '--setmonitor', MONITOR, f'{self.w}/{mm(self.w)}x{self.h}/{mm(self.h)}+{self.x}+0', 'none')
        log(f'écran virtuel {self.w}x{self.h} à droite des écrans (x={self.x})')

    def remove(self):
        run('xrandr', '--delmonitor', MONITOR)
        if self.base:
            run('xrandr', '--fb', f'{self.base[0]}x{self.base[1]}')

    def set_portrait(self, portrait):
        if portrait == (self.h > self.w):
            return False
        self.w, self.h = self.h, self.w
        run('xrandr', '--delmonitor', MONITOR)
        self.create()
        return True


# --- Encodage / flux vidéo ----------------------------------------------------------------
class Streamer:
    def __init__(self, cfg, mon):
        self.cfg, self.mon, self.proc, self.encoder = cfg, mon, None, ''
        self.display = os.environ.get('DISPLAY', ':0')

    def encoder_args(self, enc):
        c = self.cfg
        gop, rate = str(int(c['fps']) * 2), f"{c['mbps']}M"
        common = ['-bf', '0', '-g', gop, '-b:v', rate, '-maxrate', rate, '-bufsize', f"{max(1, int(c['mbps']) // 4)}M"]
        if enc == 'vaapi':
            return ['-vaapi_device', '/dev/dri/renderD128'], ['-vf', 'format=nv12,hwupload', '-c:v', 'h264_vaapi', '-profile:v', 'high'] + common
        return [], ['-pix_fmt', 'yuv420p', '-c:v', 'libx264', '-preset', 'ultrafast', '-tune', 'zerolatency', '-profile:v', 'high'] + common

    def input_args(self, fps):
        return ['-f', 'x11grab', '-draw_mouse', '1', '-framerate', str(fps),
                '-video_size', f'{self.mon.w}x{self.mon.h}', '-i', f'{self.display}+{self.mon.x},0']

    def pick_encoder(self):
        if self.cfg['encoder'] != 'auto':
            return self.cfg['encoder']
        if not self.cfg.get('detected'):
            found = 'x264'
            if os.path.exists('/dev/dri/renderD128'):
                pre, enc = self.encoder_args('vaapi')
                r = subprocess.run(['ffmpeg', '-hide_banner', '-loglevel', 'quiet', *pre, *self.input_args(30),
                                    '-frames:v', '10', *enc, '-f', 'null', '-'], capture_output=True)
                if r.returncode == 0:
                    found = 'vaapi'
            self.cfg['detected'] = found
            save_config(self.cfg)
            log('encodeur retenu : ' + ENCODER_NAMES[found])
        return self.cfg['detected']

    def start(self):
        self.encoder = self.pick_encoder()
        pre, enc = self.encoder_args(self.encoder)
        url = f"tcp://{self.cfg['ipad']}:{VIDEO_PORT}?tcp_nodelay=1"
        cmd = ['ffmpeg', '-hide_banner', '-loglevel', 'error', *pre, *self.input_args(self.cfg['fps']), *enc,
               '-f', 'flv', '-flvflags', 'no_duration_filesize', '-flush_packets', '1', url]
        self.proc = subprocess.Popen(cmd)
        return self.proc

    def restart(self):
        if self.proc and self.proc.poll() is None:
            self.proc.terminate()


# --- Canal tactile / contrôle ------------------------------------------------------------
class Control(threading.Thread):
    def __init__(self, cfg, mon, streamer):
        super().__init__(daemon=True)
        self.cfg, self.mon, self.streamer = cfg, mon, streamer
        self.sock, self.wheel = None, 0

    def send(self, line):
        try:
            self.sock.sendall((line + '\n').encode())
        except Exception:
            pass

    def send_config(self):
        name = ENCODER_NAMES.get(self.streamer.encoder, '-')
        self.send(f"C {self.cfg['fps']} {self.cfg['mbps']} {urllib.parse.quote(name)}")

    def xy(self, p):
        return str(int(self.mon.x + float(p[1]) * (self.mon.w - 1))), str(int(float(p[2]) * (self.mon.h - 1)))

    def handle(self, line):
        p = line.split(' ')
        k = p[0]
        if k in ('D', 'M', 'U', 'R') and len(p) >= 3:
            x, y = self.xy(p)
            extra = {'D': ['mousedown', '1'], 'U': ['mouseup', '1'], 'R': ['click', '3'], 'M': []}[k]
            run('xdotool', 'mousemove', x, y, *extra)
        elif k == 'W':
            self.wheel += int(p[1])
            n = int(self.wheel / 120)
            if n:
                self.wheel -= n * 120
                run('xdotool', 'click', '--repeat', str(abs(n)), '4' if n > 0 else '5')
        elif k == 'Z':
            n = int(p[1])
            run('xdotool', 'keydown', 'ctrl', 'click', '--repeat', str(abs(n)), '4' if n > 0 else '5', 'keyup', 'ctrl')
        elif k == 'T' and len(p) >= 2:
            run('xdotool', 'type', '--delay', '0', '--', urllib.parse.unquote(p[1]))
        elif k == 'K':
            run('xdotool', 'key', {'BACK': 'BackSpace', 'ENTER': 'Return'}.get(p[1], 'BackSpace'))
        elif k == 'P':
            self.send(line)
        elif k == 'O':
            if self.mon.set_portrait(p[1] == 'P'):
                self.streamer.restart()
        elif k == 'S' and len(p) >= 3:
            self.cfg['fps'], self.cfg['mbps'] = int(p[1]), int(p[2])
            save_config(self.cfg)
            log(f"qualité demandée par l'iPad : {p[1]} i/s, {p[2]} Mb/s")
            self.streamer.restart()

    def run(self):
        while True:
            ip = self.cfg.get('ipad')
            if not ip:
                time.sleep(2)
                continue
            try:
                self.sock = socket.create_connection((ip, INPUT_PORT), timeout=3)
                self.sock.settimeout(None)
                self.sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
                log('canal tactile connecté')
                self.send('N ' + socket.gethostname())
                self.send_config()
                buf = b''
                while True:
                    data = self.sock.recv(4096)
                    if not data:
                        break
                    buf += data
                    *lines, buf = buf.split(b'\n')
                    lines = [l.decode(errors='replace') for l in lines if l]
                    for i, l in enumerate(lines):
                        if l.startswith('M') and i + 1 < len(lines) and lines[i + 1].startswith('M'):
                            continue  # on ne rejoue que le dernier déplacement d'une rafale
                        self.handle(l)
            except OSError:
                pass
            finally:
                if self.sock:
                    self.sock.close()
                run('xdotool', 'mouseup', '1')
            time.sleep(2)


# --- Boucle principale ------------------------------------------------------------------
def main():
    ap = argparse.ArgumentParser(description='MirrorPad pour Linux (X11)')
    ap.add_argument('--ipad', help="adresse de l'iPad (sinon détection automatique)")
    ap.add_argument('--fps', type=int)
    ap.add_argument('--mbps', type=int)
    ap.add_argument('--encoder', choices=['auto', 'vaapi', 'x264'])
    args = ap.parse_args()
    for tool in ('ffmpeg', 'xdotool', 'xrandr'):
        if not run('which', tool).stdout:
            sys.exit(f'{tool} est introuvable : sudo apt install ffmpeg xdotool x11-xserver-utils')

    cfg = load_config(args)
    mon = VirtualMonitor(cfg['width'], cfg['height'])
    mon.create()
    streamer = Streamer(cfg, mon)
    ctrl = Control(cfg, mon, streamer)

    def stop(*_):
        streamer.restart()
        mon.remove()
        log('écran virtuel retiré, au revoir')
        os._exit(0)
    signal.signal(signal.SIGINT, stop)
    signal.signal(signal.SIGTERM, stop)

    ctrl.start()
    fails = 0
    while True:
        if not cfg['ipad'] or fails >= 2:
            ip = find_ipad()
            if ip:
                if ip != cfg['ipad']:
                    log(f'iPad trouvé : {ip}')
                cfg['ipad'] = ip
                save_config(cfg)
            fails = 0
            if not cfg['ipad']:
                log("iPad introuvable (app MirrorPad ouverte ?), nouvel essai…")
                time.sleep(3)
                continue
        t0 = time.time()
        proc = streamer.start()
        ctrl.send_config()
        proc.wait()
        fails = fails + 1 if time.time() - t0 < 3 else 0
        time.sleep(1 if fails == 0 else 3)


if __name__ == '__main__':
    main()
