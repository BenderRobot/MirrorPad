#!/usr/bin/env python3
"""MirrorPad pour Linux (session X11) : l'iPad devient un écran supplémentaire tactile.

Crée un moniteur virtuel à droite des écrans existants (xrandr --setmonitor), le capture avec
ffmpeg (x11grab), l'envoie à l'app MirrorPad de l'iPad et rejoue les gestes avec xdotool.

Prérequis : sudo apt install ffmpeg xdotool x11-xserver-utils openssl zenity
Connexion chiffrée (TLS) ; appairage au premier lancement (même code sur le PC et l'iPad).
Usage     : python3 mirrorpad-sender.py [--ipad 192.168.1.130] [--fps 60] [--mbps 12]
Wayland n'est pas pris en charge (ouvrir une session « GNOME sur Xorg » / X11).
"""
import argparse, hashlib, hmac, json, os, secrets, select, signal, socket, ssl, subprocess, sys, threading, time, urllib.parse
from pathlib import Path

PORT, MONITOR = 7272, 'MirrorPad'   # canal chiffré unique (image + tactile + contrôle)
CONFIG = Path.home() / '.config' / 'mirrorpad' / 'config.json'
DEFAULT_SHORTCUTS = [('Copier', 'ctrl+c'), ('Coller', 'ctrl+v'), ('Couper', 'ctrl+x'), ('Annuler', 'ctrl+z'), ('Rétablir', 'ctrl+shift+z'),
                     ('Tout sélectionner', 'ctrl+a'), ('Enregistrer', 'ctrl+s'), ('Rechercher', 'ctrl+f'), ('Bureau', 'win+d'), ('Activités', 'win'),
                     ('Capture d\'écran', 'printscreen'), ('Terminal', 'ctrl+alt+t'), ('Verrouiller', 'win+l'), ('Fermer la fenêtre', 'alt+f4'),
                     ('Plein écran', 'f11'), ('Nouvel onglet', 'ctrl+t'), ('Volume −', 'voldown'), ('Volume +', 'volup'), ('Muet', 'mute'),
                     ('Lecture / Pause', 'playpause')]
ENCODER_NAMES = {'vaapi': 'VA-API (carte graphique)', 'x264': 'Processeur (x264)'}


def log(msg):
    print(time.strftime('%H:%M:%S'), msg, flush=True)


def run(*args):
    return subprocess.run(list(args), capture_output=True, text=True)


# --- Configuration -------------------------------------------------------------------
def load_config(args):
    cfg = {'ipad': '', 'fps': 30, 'mbps': 12, 'encoder': 'auto', 'detected': '', 'width': 2048, 'height': 1536,
           'pcid': secrets.token_hex(16), 'paired': {}}
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
        self.w, self.h, self.x, self.base, self.active = w, h, 0, None, False

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
        self.active = True
        log(f'écran virtuel {self.w}x{self.h} à droite des écrans (x={self.x})')

    def remove(self):
        run('xrandr', '--delmonitor', MONITOR)
        if self.base:
            run('xrandr', '--fb', f'{self.base[0]}x{self.base[1]}')
        if self.active:
            log('écran virtuel retiré')
        self.active = False

    def wake(self):
        if not self.active:
            self.create()

    def set_portrait(self, portrait):
        if portrait == (self.h > self.w):
            return False
        self.w, self.h = self.h, self.w
        run('xrandr', '--delmonitor', MONITOR)
        self.create()
        return True


# --- Identité chiffrée du PC (certificat auto-signé créé une fois avec openssl) ---------------
CERT, KEY = CONFIG.parent / 'pc-cert.pem', CONFIG.parent / 'pc-key.pem'


def identity():
    if not CERT.exists() or not KEY.exists():
        CONFIG.parent.mkdir(parents=True, exist_ok=True)
        r = run('openssl', 'req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-days', '10950', '-subj',
                '/CN=MirrorPad ' + socket.gethostname(), '-keyout', str(KEY), '-out', str(CERT))
        if r.returncode:
            sys.exit('openssl est nécessaire pour créer le certificat : sudo apt install openssl')
        os.chmod(KEY, 0o600)
        log('nouveau certificat du PC créé')
    ctx = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    ctx.minimum_version = ssl.TLSVersion.TLSv1_2
    ctx.load_cert_chain(str(CERT), str(KEY))
    der = ssl.PEM_cert_to_DER_cert(CERT.read_text())
    return ctx, der


def ask_pairing(name, code):
    text = f"« {name} » demande à se connecter à ce PC.\n\nVérifiez que l'iPad affiche ce code : {code[:3]} {code[3:]}\n\nAccepter ?"
    if os.environ.get('DISPLAY') and run('which', 'zenity').stdout:
        return run('zenity', '--question', '--title=MirrorPad – Appairage', '--width=420', '--text=' + text).returncode == 0
    if sys.stdin.isatty():
        return input(text + ' [o/N] ').strip().lower() in ('o', 'oui', 'y', 'yes')
    log('appairage refusé : aucune fenêtre ni terminal pour confirmer (installez zenity)')
    return False


# --- Session chiffrée (port 7272) : le PC se connecte et joue le rôle de serveur TLS ------------
#   trames [type][longueur 3 octets][données] : 1 = ligne de contrôle, 2 = vidéo
class Session:
    def __init__(self, cfg, ctx, der):
        self.cfg, self.ctx, self.der = cfg, ctx, der
        self.lock, self.closed, self.name = threading.Lock(), False, ''
        raw = socket.create_connection((cfg['ipad'], PORT), timeout=3)
        raw.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
        self.sock = ctx.wrap_socket(raw, server_side=True)
        self.sock.settimeout(15)

    def frame(self, t, data):
        if self.closed:
            return
        try:
            with self.lock:
                self.sock.sendall(bytes([t, len(data) >> 16 & 255, len(data) >> 8 & 255, len(data) & 255]) + data)
        except OSError:
            self.close()

    def send(self, line):
        self.frame(1, line.encode())

    def read_exact(self, n):
        b = b''
        while len(b) < n:
            c = self.sock.recv(n - len(b))
            if not c:
                raise ConnectionError('connexion fermée')
            b += c
        return b

    def read_frame(self):
        h = self.read_exact(4)
        n = h[1] << 16 | h[2] << 8 | h[3]
        return h[0], self.read_exact(n) if n else b''

    def handshake(self):
        self.send(f"HELLO 2 {self.cfg['pcid']} {urllib.parse.quote(socket.gethostname())}")
        while True:
            t, d = self.read_frame()
            if t != 1:
                continue
            p = d.decode(errors='replace').split(' ')
            paired = self.cfg.setdefault('paired', {})
            if p[0] == 'AUTH' and len(p) >= 3:
                dev = paired.get(p[1])
                if dev and hmac.compare_digest(dev['token'], p[2]):
                    self.name = dev['name']
                    self.send('OK')
                    log('iPad authentifié : ' + self.name)
                    return True
                self.send('ERR AUTH')
                return False
            if p[0] == 'PAIR' and len(p) >= 4:
                nonce = bytes.fromhex(p[3])
                h = hashlib.sha256(self.der + nonce).digest()
                code = '%06d' % (int.from_bytes(h[:4], 'big') % 1000000)
                name = urllib.parse.unquote(p[2])
                log(f'demande d\'appairage : {name}, code {code}')
                if not ask_pairing(name, code):
                    self.send('ERR REFUSED')
                    return False
                token = secrets.token_hex(32)
                paired[p[1]] = {'name': name, 'token': token, 'added': time.strftime('%Y-%m-%d %H:%M')}
                save_config(self.cfg)
                self.name = name
                self.send('PAIRED ' + token)
                log('iPad appairé : ' + name)
                return True
            if p[0] == 'ERR':
                log("l'iPad refuse la connexion : " + ' '.join(p))
                return False

    def close(self):
        if not self.closed:
            self.closed = True
            try:
                self.sock.close()
            except OSError:
                pass


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

    def run(self, session):
        """Lance ffmpeg et envoie la vidéo dans la session jusqu'à l'arrêt de l'un ou de l'autre."""
        self.encoder = self.pick_encoder()
        pre, enc = self.encoder_args(self.encoder)
        cmd = ['ffmpeg', '-hide_banner', '-loglevel', 'error', *pre, *self.input_args(self.cfg['fps']), *enc,
               '-f', 'flv', '-flvflags', 'no_duration_filesize', '-flush_packets', '1', 'pipe:1']
        session.send(f"C {self.cfg['fps']} {self.cfg['mbps']} {urllib.parse.quote(ENCODER_NAMES.get(self.encoder, '-'))}")
        self.proc = subprocess.Popen(cmd, stdout=subprocess.PIPE)
        session.send('V NEW')
        try:
            while not session.closed:
                data = self.proc.stdout.read1(65536)
                if not data:
                    break
                session.frame(2, data)
        finally:
            self.restart()
            if not session.closed:
                session.send('V END')

    def restart(self):
        p = self.proc
        if p and p.poll() is None:
            p.terminate()


# --- Gestes et commandes reçus de l'iPad ------------------------------------------------------
class Input:
    def __init__(self, cfg, mon, streamer):
        self.cfg, self.mon, self.streamer, self.wheel, self.session = cfg, mon, streamer, 0, None

    def xy(self, p):
        return str(int(self.mon.x + float(p[1]) * (self.mon.w - 1))), str(int(float(p[2]) * (self.mon.h - 1)))

    # noms de touches MirrorPad -> noms xdotool
    KEYS = {'win': 'super', 'esc': 'Escape', 'enter': 'Return', 'del': 'Delete', 'back': 'BackSpace', 'tab': 'Tab',
            'space': 'space', 'ins': 'Insert', 'home': 'Home', 'end': 'End', 'pgup': 'Prior', 'pgdn': 'Next',
            'left': 'Left', 'right': 'Right', 'up': 'Up', 'down': 'Down', 'printscreen': 'Print', 'apps': 'Menu',
            'volup': 'XF86AudioRaiseVolume', 'voldown': 'XF86AudioLowerVolume', 'mute': 'XF86AudioMute',
            'playpause': 'XF86AudioPlay', 'next': 'XF86AudioNext', 'prev': 'XF86AudioPrev', 'stop': 'XF86AudioStop',
            'plus': 'plus', 'minus': 'minus'}

    def combo(self, text):
        parts = [self.KEYS.get(x, x.upper() if len(x) > 1 and x[0] == 'f' and x[1:].isdigit() else x)
                 for x in text.lower().split('+') if x]
        run('xdotool', 'key', '+'.join(parts))

    def handle(self, line):
        p = line.split(' ')
        k = p[0]
        # tactile natif et stylet n'existent pas sous X11 : on les rejoue comme une souris
        if k == 'F' and len(p) >= 2:
            f = p[1].split(':')
            if len(f) >= 4:
                k, p = {'d': 'D', 'u': 'U', 'c': 'U'}.get(f[3], 'M'), ['', f[1], f[2]]
        elif k in ('PD', 'PM', 'PU'):
            k = {'PD': 'D', 'PM': 'M', 'PU': 'U'}[k]
        if k == 'RM' and len(p) >= 3:
            run('xdotool', 'mousemove_relative', '--', p[1], p[2])
        elif k in ('CL', 'CR'):
            run('xdotool', 'click', '1' if k == 'CL' else '3')
        elif k in ('DD', 'DU'):
            run('xdotool', 'mousedown' if k == 'DD' else 'mouseup', '1')
        elif k == 'KS' and len(p) >= 2:
            self.combo(urllib.parse.unquote(p[1]))
        elif k in ('D', 'M', 'U', 'R') and len(p) >= 3:
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
        elif k == 'P' and self.session:
            self.session.send(line)
        elif k == 'O':
            if self.mon.set_portrait(p[1] == 'P'):
                self.streamer.restart()
        elif k == 'SLEEP' and self.session:
            log('iPad verrouillé')
            self.session.close()
            self.streamer.restart()
            self.mon.remove()   # les fenêtres reviennent sur les écrans réels
        elif k == 'S' and len(p) >= 3:
            self.cfg['fps'], self.cfg['mbps'] = int(p[1]), int(p[2])
            save_config(self.cfg)
            log(f"qualité demandée par l'iPad : {p[1]} i/s, {p[2]} Mb/s")
            self.streamer.restart()

    def read_loop(self, session):
        try:
            while not session.closed:
                t, d = session.read_frame()
                if t == 1:
                    self.handle(d.decode(errors='replace'))
        except (OSError, ConnectionError, ValueError):
            pass
        session.close()
        run('xdotool', 'mouseup', '1')


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
    save_config(cfg)
    ctx, der = identity()
    mon = VirtualMonitor(cfg['width'], cfg['height'])
    mon.create()
    streamer = Streamer(cfg, mon)
    inp = Input(cfg, mon, streamer)

    def stop(*_):
        streamer.restart()
        mon.remove()
        log('écran virtuel retiré, au revoir')
        os._exit(0)
    signal.signal(signal.SIGINT, stop)
    signal.signal(signal.SIGTERM, stop)

    fails, last_alive = 0, time.time()
    while True:
        if mon.active and time.time() - last_alive > 20:   # iPad absent : on retire l'écran virtuel
            mon.remove()
        if not cfg['ipad'] or fails >= 2:
            ip = find_ipad()
            if ip and ip != cfg['ipad']:
                log(f'iPad trouvé : {ip}')
                cfg['ipad'] = ip
                save_config(cfg)
            fails = 0
            if not cfg['ipad']:
                log("iPad introuvable (app MirrorPad ouverte ?), nouvel essai…")
                time.sleep(3)
                continue
        try:
            session = Session(cfg, ctx, der)
            if not session.handshake():
                session.close()
                time.sleep(5)
                continue
        except (OSError, ConnectionError, ValueError) as e:
            fails += 1
            if fails == 1:
                log(f"connexion impossible à {cfg['ipad']} : {e}")
            time.sleep(3)
            continue
        fails = 0
        mon.wake()
        inp.session = session
        threading.Thread(target=inp.read_loop, args=(session,), daemon=True).start()
        session.send('N ' + socket.gethostname())
        session.send('SC ' + ' '.join(urllib.parse.quote(f"{l}|{c}") for l, c in cfg.get('shortcuts', DEFAULT_SHORTCUTS)))
        while not session.closed:
            t0 = time.time()
            streamer.run(session)
            last_alive = time.time()
            if time.time() - t0 < 3 and not session.closed:
                time.sleep(3)
        last_alive = time.time()
        time.sleep(3)


if __name__ == '__main__':
    main()
