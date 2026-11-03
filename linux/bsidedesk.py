#!/usr/bin/env python3
"""bSideDesk pour Debian / GNOME (Wayland) : l'iPad devient un vrai écran supplémentaire tactile.

GNOME crée lui-même l'écran de l'iPad (écran virtuel de Mutter, API « RemoteDesktop » + « ScreenCast »),
GStreamer le capture par PipeWire et l'encode en H.264 (carte graphique Intel si possible), et les gestes
de l'iPad sont rejoués par GNOME (multi-touch natif, souris, clavier).

Lancé automatiquement à l'ouverture de la session (paquet bsidedesk). Icône dans la barre du haut.
    bsidedesk            application (icône, connexion automatique)
    bsidedesk --pair     confirmer un appairage depuis un terminal (SSH), sans voir l'écran
    bsidedesk --update   rechercher une mise à jour maintenant
"""
import argparse, hashlib, hmac, json, os, secrets, select, signal, socket, ssl, subprocess, sys, threading, time, urllib.parse, urllib.request

import gi
gi.require_version('Gst', '1.0')
gi.require_version('Gtk', '3.0')
from gi.repository import Gio, GLib, Gst, Gtk  # noqa: E402

try:
    gi.require_version('AyatanaAppIndicator3', '0.1')
    from gi.repository import AyatanaAppIndicator3 as AppIndicator  # noqa: E402
except (ValueError, ImportError):
    AppIndicator = None

VERSION_FILE = '/usr/lib/bsidedesk/version.txt'
PORT = 7272
REPO = 'BenderRobot/bsidedesk'
DIR = os.path.join(GLib.get_user_config_dir(), 'bsidedesk')
CONFIG = os.path.join(DIR, 'config.json')
LOG = os.path.join(DIR, 'bsidedesk.log')
CERT, KEY = os.path.join(DIR, 'pc-cert.pem'), os.path.join(DIR, 'pc-key.pem')
PAIR_REQ, PAIR_ANS = os.path.join(DIR, 'pair-request.json'), os.path.join(DIR, 'pair-answer')
ICON = '/usr/share/pixmaps/bsidedesk.png'

DEFAULT_SHORTCUTS = [('Copier', 'ctrl+c'), ('Coller', 'ctrl+v'), ('Couper', 'ctrl+x'), ('Annuler', 'ctrl+z'), ('Rétablir', 'ctrl+shift+z'),
                     ('Tout sélectionner', 'ctrl+a'), ('Enregistrer', 'ctrl+s'), ('Rechercher', 'ctrl+f'), ('Activités', 'win'),
                     ('Capture d\'écran', 'printscreen'), ('Terminal', 'ctrl+alt+t'), ('Verrouiller', 'win+l'), ('Fermer la fenêtre', 'alt+f4'),
                     ('Plein écran', 'f11'), ('Nouvel onglet', 'ctrl+t'), ('Volume −', 'voldown'), ('Volume +', 'volup'), ('Muet', 'mute'),
                     ('Lecture / Pause', 'playpause')]
# Touch Bar pour GNOME : raccourcis par défaut de GNOME 43 (Super = touche Windows)
TOUCH_BAR = ['Clavier|@keyboard', 'Rechercher|win', 'Applications|win+a', 'Fenêtre suivante|alt+tab', 'Agrandir|win+up',
             '◀ Moitié|win+left', 'Moitié ▶|win+right', 'Réduire|win+h', 'Fermer|alt+f4', 'Notifications|win+v',
             'Copier|ctrl+c', 'Coller|ctrl+v', 'Annuler|ctrl+z', 'Capture|printscreen', 'Pavé numérique|@numpad']
ENCODER_NAMES = {'va': 'Intel VA-API', 'vaapi': 'Intel VA-API (gstreamer-vaapi)', 'x264': 'Processeur (x264)'}


def version():
    try:
        return open(VERSION_FILE).read().strip()
    except OSError:
        return '0.0.0'


def log(msg):
    line = time.strftime('%Y-%m-%d %H:%M:%S ') + msg
    print(line, flush=True)
    try:
        os.makedirs(DIR, exist_ok=True)
        if os.path.exists(LOG) and os.path.getsize(LOG) > 2_000_000:
            os.replace(LOG, LOG + '.1')
        with open(LOG, 'a') as f:
            f.write(line + '\n')
    except OSError:
        pass


def run(*args):
    return subprocess.run(list(args), capture_output=True, text=True)


# ======================================================================================
# Configuration
# ======================================================================================
def load_config():
    cfg = {'ipad': '', 'fps': 60, 'mbps': 12, 'encoder': 'auto', 'detected': '', 'width': 2048, 'height': 1536,
           'pcid': secrets.token_hex(16), 'paired': {}, 'auto_update': True,
           'ipad_only': True, 'scale': 'auto'}   # PC sans écran : l'iPad seul, interface agrandie ×2
    old = os.path.join(GLib.get_user_config_dir(), 'mirrorpad')   # ancien nom de l'app
    if not os.path.isdir(DIR) and os.path.isdir(old):
        import shutil
        shutil.copytree(old, DIR)
    try:
        with open(CONFIG) as f:
            cfg.update(json.load(f))
    except (OSError, ValueError):
        pass
    return cfg


def save_config(cfg):
    os.makedirs(DIR, exist_ok=True)
    tmp = CONFIG + '.tmp'
    with open(tmp, 'w') as f:
        json.dump(cfg, f, indent=2)
    os.replace(tmp, CONFIG)


# ======================================================================================
# Détection de l'iPad (mDNS « _bsidedesk._tcp.local »)
# ======================================================================================
def find_ipad(timeout=1.5):
    q = bytes([0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0])
    for label in ('_bsidedesk', '_tcp', 'local'):
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
            if b'_bsidedesk' in data:
                found = src[0]
                break
    for s in socks:
        s.close()
    return found


# ======================================================================================
# Identité chiffrée du PC et appairage
# ======================================================================================
def identity():
    if not os.path.exists(CERT) or not os.path.exists(KEY):
        os.makedirs(DIR, exist_ok=True)
        r = run('openssl', 'req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-days', '10950', '-subj',
                '/CN=bSideDesk ' + socket.gethostname(), '-keyout', KEY, '-out', CERT)
        if r.returncode:
            sys.exit('openssl est nécessaire pour créer le certificat')
        os.chmod(KEY, 0o600)
        log('nouveau certificat du PC créé')
    ctx = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    ctx.minimum_version = ssl.TLSVersion.TLSv1_2
    ctx.load_cert_chain(CERT, KEY)
    der = ssl.PEM_cert_to_DER_cert(open(CERT).read())
    return ctx, der


class Pairing:
    """Confirmation d'appairage : fenêtre sur le bureau GNOME, ou « bsidedesk --pair » dans un terminal (SSH)."""

    def __init__(self, app):
        self.app = app

    def ask(self, name, code):
        answer, done = [None], threading.Event()

        def show():
            d = Gtk.MessageDialog(message_type=Gtk.MessageType.QUESTION, buttons=Gtk.ButtonsType.NONE,
                                  text=f'« {name} » demande à se connecter à ce PC')
            d.format_secondary_markup(f'Vérifiez que l\'iPad affiche exactement ce code :\n\n'
                                      f'<span size="xx-large" weight="bold" foreground="#3B5BFF">{code[:3]} {code[3:]}</span>\n\n'
                                      'Si les codes sont différents, refusez.')
            d.add_buttons('Refuser', Gtk.ResponseType.NO, 'Accepter', Gtk.ResponseType.YES)
            d.set_keep_above(True)
            d.set_title('bSideDesk – Appairage')

            def resp(dlg, r):
                if answer[0] is None:
                    answer[0] = r == Gtk.ResponseType.YES
                dlg.destroy()
                done.set()
            d.connect('response', resp)
            d.show_all()
            self.dialog = d
            return False
        self.dialog = None
        GLib.idle_add(show)
        try:
            with open(PAIR_REQ, 'w') as f:
                json.dump({'name': name, 'code': code, 'time': time.time()}, f)
            if os.path.exists(PAIR_ANS):
                os.remove(PAIR_ANS)
        except OSError:
            pass
        end = time.time() + 120
        while not done.is_set() and time.time() < end:
            if os.path.exists(PAIR_ANS):   # réponse donnée par « bsidedesk --pair »
                try:
                    answer[0] = open(PAIR_ANS).read().strip() == 'yes'
                    os.remove(PAIR_ANS)
                except OSError:
                    pass
                if self.dialog:
                    GLib.idle_add(self.dialog.destroy)
                break
            done.wait(0.3)
        for p in (PAIR_REQ, PAIR_ANS):
            try:
                os.remove(p)
            except OSError:
                pass
        return bool(answer[0])


def pair_cli():
    """bsidedesk --pair : attend une demande d'appairage et la confirme dans le terminal."""
    print("Ouvrez bSideDesk sur l'iPad et choisissez ce PC… (Ctrl+C pour annuler)")
    while not os.path.exists(PAIR_REQ):
        time.sleep(0.3)
    req = json.load(open(PAIR_REQ))
    code = req['code']
    ok = input(f"« {req['name']} » demande à se connecter. Code affiché sur l'iPad : {code[:3]} {code[3:]} ?  Accepter [o/N] ").strip().lower() in ('o', 'oui', 'y', 'yes')
    with open(PAIR_ANS, 'w') as f:
        f.write('yes' if ok else 'no')
    print('Appairage accepté.' if ok else 'Appairage refusé.')


# ======================================================================================
# Session chiffrée (port 7272) : le PC se connecte et joue le rôle de serveur TLS
#   trames [type][longueur 3 octets][données] : 1 = ligne de contrôle, 2 = vidéo FLV
# ======================================================================================
class Session:
    def __init__(self, cfg, ctx, der, pairing):
        self.cfg, self.ctx, self.der, self.pairing = cfg, ctx, der, pairing
        self.lock, self.closed, self.name = threading.Lock(), False, ''
        raw = socket.create_connection((cfg['ipad'], PORT), timeout=3)
        raw.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
        raw.setsockopt(socket.SOL_SOCKET, socket.SO_SNDBUF, 96 * 1024)
        self.sock = ctx.wrap_socket(raw, server_side=True)
        self.sock.settimeout(20)

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
            p = d.decode(errors='replace').strip().split(' ')
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
                log(f"demande d'appairage : {name}, code {code} (confirmer ici ou avec « bsidedesk --pair »)")
                self.sock.settimeout(150)
                ok = self.pairing.ask(name, code)
                self.sock.settimeout(20)
                if not ok:
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


# ======================================================================================
# Écran virtuel de GNOME (Mutter) : RemoteDesktop (gestes) + ScreenCast « RecordVirtual » (image)
# ======================================================================================
class VirtualScreen:
    RD, SC = 'org.gnome.Mutter.RemoteDesktop', 'org.gnome.Mutter.ScreenCast'

    def __init__(self):
        self.bus = Gio.bus_get_sync(Gio.BusType.SESSION, None)
        self.rd_path = self.sc_path = self.stream = None
        self.node, self.node_ready, self.sub = None, threading.Event(), None

    def _call(self, name, path, iface, method, args=None, rtype=None):
        return self.bus.call_sync(name, path, iface, method, args, GLib.VariantType(rtype) if rtype else None,
                                  Gio.DBusCallFlags.NONE, 5000, None)

    def start(self):
        """Crée l'écran virtuel ; renvoie le numéro du flux PipeWire à capturer."""
        self.stop()
        self.node_ready.clear()
        self.rd_path = self._call(self.RD, '/org/gnome/Mutter/RemoteDesktop', self.RD, 'CreateSession', None, '(o)').unpack()[0]
        sid = self._call(self.RD, self.rd_path, 'org.freedesktop.DBus.Properties', 'Get',
                         GLib.Variant('(ss)', (self.RD + '.Session', 'SessionId')), '(v)').unpack()[0]
        self.sc_path = self._call(self.SC, '/org/gnome/Mutter/ScreenCast', self.SC, 'CreateSession',
                                  GLib.Variant('(a{sv})', ({'remote-desktop-session-id': GLib.Variant('s', sid)},)), '(o)').unpack()[0]
        self.stream = self._call(self.SC, self.sc_path, self.SC + '.Session', 'RecordVirtual',
                                 GLib.Variant('(a{sv})', ({'cursor-mode': GLib.Variant('u', 1),   # curseur dessiné dans l'image
                                                           'is-platform': GLib.Variant('b', True)},)), '(o)').unpack()[0]

        def added(conn, sender, path, iface, signal, params):
            self.node = params.unpack()[0]
            self.node_ready.set()
        self.sub = self.bus.signal_subscribe(self.SC, self.SC + '.Stream', 'PipeWireStreamAdded', self.stream, None,
                                             Gio.DBusSignalFlags.NONE, added)
        self._call(self.RD, self.rd_path, self.RD + '.Session', 'Start')
        if not self.node_ready.wait(5):
            raise RuntimeError("GNOME n'a pas créé l'écran virtuel (flux PipeWire absent)")
        return self.node

    def stop(self):
        if self.sub is not None:
            self.bus.signal_unsubscribe(self.sub)
            self.sub = None
        if self.rd_path:
            try:
                self._call(self.RD, self.rd_path, self.RD + '.Session', 'Stop')
            except GLib.Error:
                pass
        self.rd_path = self.sc_path = self.stream = None

    @property
    def active(self):
        return self.rd_path is not None

    # ---- disposition des écrans (Paramètres > Écrans de GNOME, appliquée sans l'enregistrer) ----
    DC = 'org.gnome.Mutter.DisplayConfig'

    def layout(self, ipad_only, scale_pref):
        """Écran de l'iPad en écran principal (barre du haut, dock, bureau), interface ×2, autres écrans éteints
        (ipad_only) ou placés à sa droite. Réglage temporaire : GNOME revient à sa disposition au départ de l'iPad."""
        state = None
        for _ in range(60):   # l'écran virtuel apparaît quand la capture démarre
            state = self._call(self.DC, '/org/gnome/Mutter/DisplayConfig', self.DC, 'GetCurrentState', None,
                               '(ua((ssss)a(siiddada{sv})a{sv})a(iiduba(ssss)a{sv})a{sv})').unpack()
            if any(self._is_virtual(m[0]) for m in state[1]):
                break
            time.sleep(0.1)
        serial, monitors, logical = state[0], state[1], state[2]
        current = {}
        for spec, modes, props in monitors:
            mode = next((m for m in modes if m[6].get('is-current')), None) or next((m for m in modes if m[6].get('is-preferred')), modes[0] if modes else None)
            if mode:
                current[spec[0]] = (spec, mode)
        virt = next((c for c, (spec, mode) in current.items() if self._is_virtual(spec)), None)
        if not virt:
            raise RuntimeError("écran de l'iPad absent de la configuration de GNOME")
        spec, mode = current[virt]
        w, h, scales = mode[1], mode[2], mode[5]
        scale = 1.0
        if scale_pref == 'auto':
            for sc in (2.0,):
                if any(abs(x - sc) < 0.01 for x in scales) and w % 2 == 0 and h % 2 == 0:
                    scale = sc
        else:
            try:
                want = float(scale_pref)
                scale = min(scales, key=lambda x: abs(x - want)) if scales else 1.0
            except ValueError:
                pass
        conf = [(0, 0, scale, 0, True, [(virt, mode[0], {})])]
        x = int(w / scale)
        if not ipad_only:
            old = {lm[5][0][0]: lm for lm in logical if lm[5]}
            for conn, (sp, md) in current.items():
                if conn == virt or conn not in old:
                    continue
                lm = old[conn]
                conf.append((x, 0, lm[2], lm[3], False, [(conn, md[0], {})]))
                x += int(md[1] / lm[2])
        self._call(self.DC, '/org/gnome/Mutter/DisplayConfig', self.DC, 'ApplyMonitorsConfig',
                   GLib.Variant('(uua(iiduba(ssa{sv}))a{sv})', (serial, 1, conf, {})))
        return f"écran principal ×{scale:g}" + (', autres écrans éteints' if ipad_only else '')

    @staticmethod
    def _is_virtual(spec):
        conn, vendor, product = spec[0], spec[1], spec[2]
        return conn.startswith('Meta-') or 'Virtual' in product or vendor == 'MetaVendor'

    # ---- gestes : messages sans réponse attendue (rapides, dans l'ordre) ----
    def notify(self, method, sig, *args):
        path = self.rd_path
        if not path:
            return
        m = Gio.DBusMessage.new_method_call(self.RD, path, self.RD + '.Session', method)
        m.set_body(GLib.Variant(sig, args))
        m.set_flags(Gio.DBusMessageFlags.NO_REPLY_EXPECTED)
        try:
            self.bus.send_message(m, Gio.DBusSendMessageFlags.NONE)
        except GLib.Error:
            pass


# ======================================================================================
# Capture et encodage (GStreamer : PipeWire -> H.264 -> FLV)
# ======================================================================================
class Streamer:
    def __init__(self, cfg):
        self.cfg, self.pipe, self.encoder = cfg, None, ''

    def enc_chain(self, enc):
        c = self.cfg
        kbps, gop = int(c['mbps']) * 1000, int(c['fps']) * 2
        if enc == 'va':
            return (f'vapostproc ! video/x-raw(memory:VAMemory),format=NV12 ! '
                    f'vah264enc rate-control=cbr bitrate={kbps} key-int-max={gop} b-frames=0 target-usage=7')
        if enc == 'vaapi':
            return (f'vaapipostproc ! video/x-raw(memory:VASurface),format=NV12 ! '
                    f'vaapih264enc rate-control=cbr bitrate={kbps} keyframe-period={gop} max-bframes=0 tune=none')
        return (f'videoconvert n-threads=4 ! video/x-raw,format=I420 ! '
                f'x264enc tune=zerolatency speed-preset=ultrafast bitrate={kbps} key-int-max={gop} bframes=0 threads=4')

    def pick_encoder(self):
        if self.cfg['encoder'] != 'auto':
            return self.cfg['encoder']
        if not self.cfg.get('detected'):
            found = 'x264'
            for e in ('va', 'vaapi'):
                try:
                    p = Gst.parse_launch(f'videotestsrc num-buffers=15 ! video/x-raw,format=BGRx,width=640,height=480 ! '
                                         f'{self.enc_chain(e)} ! h264parse ! fakesink')
                    p.set_state(Gst.State.PLAYING)
                    msg = p.get_bus().timed_pop_filtered(8 * Gst.SECOND, Gst.MessageType.EOS | Gst.MessageType.ERROR)
                    p.set_state(Gst.State.NULL)
                    if msg and msg.type == Gst.MessageType.EOS:
                        found = e
                        break
                except GLib.Error:
                    continue
            self.cfg['detected'] = found
            save_config(self.cfg)
            log('encodeur retenu : ' + ENCODER_NAMES[found])
        return self.cfg['detected']

    def run(self, session, node, w, h):
        """Capture le flux PipeWire de l'écran virtuel et l'envoie à l'iPad jusqu'à l'arrêt."""
        self.encoder = self.pick_encoder()
        desc = (f'pipewiresrc path={node} do-timestamp=true keepalive-time=500 always-copy=true ! '
                f'video/x-raw,width={w},height={h} ! '
                f'queue leaky=downstream max-size-buffers=2 ! {self.enc_chain(self.encoder)} ! '
                'h264parse config-interval=-1 ! video/x-h264,stream-format=avc,alignment=au ! '
                'flvmux streamable=true latency=0 ! appsink name=out sync=false emit-signals=false')
        session.send(f"C {self.cfg['fps']} {self.cfg['mbps']} {urllib.parse.quote(ENCODER_NAMES.get(self.encoder, '-'))}")
        self.pipe = pipe = Gst.parse_launch(desc)
        sink = pipe.get_by_name('out')
        pipe.set_state(Gst.State.PLAYING)
        session.send('V NEW')
        bus = pipe.get_bus()
        started = time.time()
        try:
            while not session.closed and self.pipe is pipe:
                sample = sink.emit('try-pull-sample', 200 * Gst.MSECOND)
                if sample is not None:
                    buf = sample.get_buffer()
                    ok, info = buf.map(Gst.MapFlags.READ)
                    if ok:
                        data = bytes(info.data)
                        buf.unmap(info)
                        for i in range(0, len(data), 65536):
                            session.frame(2, data[i:i + 65536])
                msg = bus.pop_filtered(Gst.MessageType.ERROR | Gst.MessageType.EOS)
                if msg:
                    if msg.type == Gst.MessageType.ERROR:
                        err, dbg = msg.parse_error()
                        log(f'capture : {err.message}')
                    break
        finally:
            pipe.set_state(Gst.State.NULL)
            if self.pipe is pipe:
                self.pipe = None
            if not session.closed:
                session.send('V END')
        return time.time() - started

    def restart(self):
        p, self.pipe = self.pipe, None
        if p is not None:
            p.send_event(Gst.Event.new_eos())


# ======================================================================================
# Gestes et commandes reçus de l'iPad
# ======================================================================================
KEYSYMS = {'ctrl': 0xffe3, 'shift': 0xffe1, 'alt': 0xffe9, 'win': 0xffeb, 'super': 0xffeb, 'altgr': 0xfe03,
           'esc': 0xff1b, 'enter': 0xff0d, 'tab': 0xff09, 'back': 0xff08, 'del': 0xffff, 'ins': 0xff63, 'space': 0x20,
           'home': 0xff50, 'end': 0xff57, 'pgup': 0xff55, 'pgdn': 0xff56, 'left': 0xff51, 'up': 0xff52, 'right': 0xff53, 'down': 0xff54,
           'printscreen': 0xff61, 'apps': 0xff67, 'plus': 0x2b, 'minus': 0x2d, 'period': 0x2e, 'comma': 0x2c,
           'volup': 0x1008ff13, 'voldown': 0x1008ff11, 'mute': 0x1008ff12, 'playpause': 0x1008ff14, 'next': 0x1008ff17,
           'prev': 0x1008ff16, 'stop': 0x1008ff15, 'nummul': 0xffaa, 'numadd': 0xffab, 'numsub': 0xffad, 'numdec': 0xffae, 'numdiv': 0xffaf}
BTN_LEFT, BTN_RIGHT = 0x110, 0x111


def keysym(name):
    n = name.lower()
    if n in KEYSYMS:
        return KEYSYMS[n]
    if len(n) >= 2 and n[0] == 'f' and n[1:].isdigit():
        return 0xffbe + int(n[1:]) - 1
    if n.startswith('num') and n[3:].isdigit():
        return 0xffb0 + int(n[3:])
    if len(name) == 1:
        return char_keysym(name.lower())
    return None


def char_keysym(c):
    o = ord(c)
    if c == '\n':
        return 0xff0d
    if c == '\t':
        return 0xff09
    return o if o < 0x100 else 0x01000000 + o


class Input:
    def __init__(self, app):
        self.app, self.screen, self.wheel, self.zoom = app, app.screen, 0, 0
        self.left = False

    @property
    def size(self):
        return self.app.size

    def abs(self, nx, ny):
        w, h = self.size
        self.screen.notify('NotifyPointerMotionAbsolute', '(sdd)', self.screen.stream or '', float(nx) * (w - 1), float(ny) * (h - 1))

    def button(self, b, down):
        self.screen.notify('NotifyPointerButton', '(ib)', b, down)

    def key(self, sym, down):
        self.screen.notify('NotifyKeyboardKeysym', '(ub)', sym, down)

    def combo(self, text):
        syms = [keysym(x) for x in text.split('+') if x]
        syms = [s for s in syms if s is not None]
        for s in syms:
            self.key(s, True)
        for s in reversed(syms):
            self.key(s, False)

    def type_text(self, text):
        for ch in text:
            s = char_keysym(ch)
            self.key(s, True)
            self.key(s, False)

    def touch(self, parts):
        """« F id:x:y:d|m|u|c … » : vrai multi-touch dans GNOME (gestes à 3 et 4 doigts compris)."""
        w, h = self.size
        stream = self.screen.stream or ''
        for item in parts:
            f = item.split(':')
            if len(f) < 4:
                continue
            slot, x, y, st = int(f[0]), float(f[1]) * (w - 1), float(f[2]) * (h - 1), f[3]
            if st == 'd':
                self.screen.notify('NotifyTouchDown', '(sudd)', stream, slot, x, y)
            elif st == 'm':
                self.screen.notify('NotifyTouchMotion', '(sudd)', stream, slot, x, y)
            else:
                self.screen.notify('NotifyTouchUp', '(u)', slot)

    def release(self):
        if self.left:
            self.button(BTN_LEFT, False)
            self.left = False

    def handle(self, line):
        p = line.strip().split(' ')
        k = p[0]
        try:
            if k in ('D', 'M', 'U', 'R') and len(p) >= 3:
                self.abs(p[1], p[2])
                if k == 'D':
                    self.button(BTN_LEFT, True); self.left = True
                elif k == 'U':
                    self.release()
                elif k == 'R':
                    self.button(BTN_RIGHT, True); self.button(BTN_RIGHT, False)
            elif k == 'F':
                self.touch(p[1:])
            elif k in ('PD', 'PM', 'PU'):   # stylet : pas de tablette graphique via GNOME, on dessine à la souris
                if len(p) >= 3:
                    self.abs(p[1], p[2])
                if k == 'PD':
                    self.button(BTN_LEFT, True); self.left = True
                elif k == 'PU':
                    self.release()
            elif k == 'RM' and len(p) >= 3:
                self.screen.notify('NotifyPointerMotionRelative', '(dd)', float(p[1]), float(p[2]))
            elif k in ('CL', 'CR'):
                b = BTN_LEFT if k == 'CL' else BTN_RIGHT
                self.button(b, True); self.button(b, False)
            elif k == 'DD':
                self.button(BTN_LEFT, True); self.left = True
            elif k == 'DU':
                self.release()
            elif k == 'W' and len(p) >= 2:
                # défilement fin : 120 = un cran de molette ; positif = vers le haut
                self.screen.notify('NotifyPointerAxis', '(ddu)', 0.0, -int(p[1]) / 120.0 * 10.0, 1 << 3)
            elif k == 'Z' and len(p) >= 2:
                n = int(p[1])
                self.key(KEYSYMS['ctrl'], True)
                self.screen.notify('NotifyPointerAxisDiscrete', '(ui)', 0, -1 if n > 0 else 1)
                self.key(KEYSYMS['ctrl'], False)
            elif k == 'KS' and len(p) >= 2:
                self.combo(urllib.parse.unquote(p[1]))
            elif k == 'T' and len(p) >= 2:
                self.type_text(urllib.parse.unquote(' '.join(p[1:])))
            elif k == 'K' and len(p) >= 2:
                s = 0xff08 if p[1] == 'BACK' else 0xff0d
                self.key(s, True); self.key(s, False)
            else:
                self.app.command(p)
        except (ValueError, IndexError):
            pass


# ======================================================================================
# Mises à jour : nouvelle version publiée sur GitHub -> installée par l'outil système (pkexec, sans mot de passe)
# ======================================================================================
def check_update(manual=False):
    try:
        req = urllib.request.Request(f'https://api.github.com/repos/{REPO}/releases/latest', headers={'User-Agent': 'bSideDesk'})
        rel = json.load(urllib.request.urlopen(req, timeout=15))
        remote = rel['tag_name'].lstrip('v')
        if tuple(int(x) for x in remote.split('.')) <= tuple(int(x) for x in version().split('.')):
            if manual:
                notify('Déjà à jour (version ' + version() + ').')
            return False
        if not any(a['name'].endswith('_all.deb') for a in rel.get('assets', [])):
            return False
        log(f'mise à jour disponible : {remote}')
        helper = '/usr/lib/bsidedesk/bsidedesk-update'
        if os.environ.get('XDG_SESSION_TYPE') in ('wayland', 'x11'):
            r = run('pkexec', helper)   # session GNOME ouverte : sans mot de passe (règle polkit)
        else:   # terminal SSH : polkit ne connaît pas cette session, on passe par sudo (mot de passe demandé)
            r = subprocess.run(['sudo', helper], text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
        if r.returncode == 0:
            log(f'mise à jour installée : {remote}')
            return True
        log('mise à jour impossible : ' + (r.stderr or r.stdout).strip()[-300:])
    except Exception as e:  # noqa: BLE001
        if manual:
            notify('Recherche de mise à jour impossible : ' + str(e))
    return False


def notify(text):
    run('notify-send', '-a', 'bSideDesk', '-i', ICON, 'bSideDesk', text)


# ======================================================================================
# Application : icône dans la barre du haut, connexion automatique, réglages
# ======================================================================================
class App:
    def __init__(self):
        Gst.init(None)
        self.cfg = load_config()
        if self.cfg.get('detected') == 'x264':
            self.cfg['detected'] = ''   # pilote Intel installé entre-temps ? on refait le test de l'encodeur matériel
        save_config(self.cfg)
        self.ctx, self.der = identity()
        self.screen = VirtualScreen()
        self.streamer = Streamer(self.cfg)
        self.input = Input(self)
        self.pairing = Pairing(self)
        self.session = None
        self.wanted = True
        self.size = (self.cfg['width'], self.cfg['height'])
        self.want_size = self.size
        self.state = 'Démarrage…'
        self.sleeping = False
        self.standby = False
        self.indicator = None
        self.status_item = None
        log(f'bSideDesk {version()} démarré ({os.environ.get("XDG_SESSION_TYPE", "?")}, {os.environ.get("XDG_CURRENT_DESKTOP", "?")})')
        if os.environ.get('XDG_SESSION_TYPE') != 'wayland':
            log("attention : bSideDesk pour Linux utilise l'écran virtuel de GNOME sous Wayland")

    # ---------- icône ----------
    def build_tray(self):
        menu = Gtk.Menu()
        self.status_item = Gtk.MenuItem(label=self.state)
        self.status_item.set_sensitive(False)
        menu.append(self.status_item)
        ver = Gtk.MenuItem(label='Version ' + version())
        ver.set_sensitive(False)
        menu.append(ver)
        menu.append(Gtk.SeparatorMenuItem())
        self.toggle_item = Gtk.MenuItem(label='Déconnecter')
        self.toggle_item.connect('activate', lambda *_: self.toggle())
        menu.append(self.toggle_item)
        for label, cb in (('Paramètres…', lambda *_: self.show_settings()),
                          ('Rechercher une mise à jour', lambda *_: threading.Thread(target=self.update_now, args=(True,), daemon=True).start()),
                          ('Ouvrir le journal', lambda *_: run('xdg-open', LOG)),
                          ('Quitter', lambda *_: self.quit())):
            if label == 'Quitter':
                menu.append(Gtk.SeparatorMenuItem())
            it = Gtk.MenuItem(label=label)
            it.connect('activate', cb)
            menu.append(it)
        menu.show_all()
        if AppIndicator:
            run('gnome-extensions', 'enable', 'appindicatorsupport@rgcjonas.gmail.com')   # icônes dans la barre de GNOME
            self.indicator = AppIndicator.Indicator.new('bsidedesk', ICON if os.path.exists(ICON) else 'video-display',
                                                        AppIndicator.IndicatorCategory.APPLICATION_STATUS)
            self.indicator.set_status(AppIndicator.IndicatorStatus.ACTIVE)
            self.indicator.set_title('bSideDesk')
            self.indicator.set_menu(menu)
        else:
            log("icône indisponible (paquet gir1.2-ayatanaappindicator3-0.1 manquant)")

    def set_state(self, text):
        if text == self.state:
            return
        self.state = text
        GLib.idle_add(lambda: (self.status_item.set_label(text) if self.status_item else None) and False)

    def toggle(self):
        self.wanted = not self.wanted
        self.toggle_item.set_label('Déconnecter' if self.wanted else "Connecter l'iPad")
        if not self.wanted:
            s = self.session
            if s:
                s.close()
            self.streamer.restart()

    def quit(self):
        self.wanted = False
        s = self.session
        if s:
            s.close()
        self.streamer.restart()
        self.screen.stop()
        log('bSideDesk arrêté')
        Gtk.main_quit()

    def update_now(self, manual):
        if check_update(manual):
            self.quit_and_restart()

    def quit_and_restart(self):
        s = self.session
        if s:
            s.close()
        self.screen.stop()
        os.execv(sys.executable, [sys.executable, '/usr/lib/bsidedesk/bsidedesk.py'])   # même processus : le lanceur continue de veiller

    # ---------- réglages ----------
    def show_settings(self):
        d = Gtk.Dialog(title='bSideDesk – Paramètres')
        d.add_buttons('Annuler', Gtk.ResponseType.CANCEL, 'Enregistrer', Gtk.ResponseType.OK)
        d.set_default_size(420, -1)
        box = d.get_content_area()
        box.set_spacing(8)
        box.set_border_width(14)
        head = Gtk.Label()
        head.set_markup(f'<span size="large" weight="bold" foreground="#3B5BFF">bSideDesk</span>  version {version()}\n<small>{GLib.markup_escape_text(self.state)}</small>')
        head.set_xalign(0)
        box.add(head)
        grid = Gtk.Grid(column_spacing=12, row_spacing=8)
        ip = Gtk.Entry(text=self.cfg['ipad'], placeholder_text='détection automatique')
        fps = Gtk.ComboBoxText()
        for v in ('30', '60'):
            fps.append(v, v + ' images/s')
        fps.set_active_id(str(self.cfg['fps']))
        mbps = Gtk.SpinButton.new_with_range(2, 40, 1)
        mbps.set_value(self.cfg['mbps'])
        enc = Gtk.ComboBoxText()
        for kname, label in (('auto', 'Automatique'), ('va', ENCODER_NAMES['va']), ('vaapi', ENCODER_NAMES['vaapi']), ('x264', ENCODER_NAMES['x264'])):
            enc.append(kname, label)
        enc.set_active_id(self.cfg['encoder'])
        upd = Gtk.CheckButton(label='Mises à jour automatiques')
        upd.set_active(self.cfg.get('auto_update', True))
        only = Gtk.CheckButton(label="iPad seul écran (éteint les autres pendant la connexion)")
        only.set_active(self.cfg.get('ipad_only', True))
        big = Gtk.CheckButton(label="Interface agrandie ×2 sur l'iPad (recommandé)")
        big.set_active(str(self.cfg.get('scale', 'auto')) != '1')
        for i, (label, w) in enumerate((("Adresse de l'iPad", ip), ('Fluidité', fps), ('Débit (Mb/s)', mbps), ('Encodeur', enc))):
            lab = Gtk.Label(label=label, xalign=0)
            grid.attach(lab, 0, i, 1, 1)
            grid.attach(w, 1, i, 1, 1)
        grid.attach(upd, 0, 4, 2, 1)
        grid.attach(only, 0, 5, 2, 1)
        grid.attach(big, 0, 6, 2, 1)
        box.add(grid)
        box.add(Gtk.Label(label='iPad appairés', xalign=0))
        store = Gtk.ListStore(str, str)
        for dev_id, dev in self.cfg.get('paired', {}).items():
            store.append([dev_id, f"{dev['name']}  ·  {dev.get('added', '')}"])
        view = Gtk.TreeView(model=store, headers_visible=False)
        view.append_column(Gtk.TreeViewColumn('', Gtk.CellRendererText(), text=1))
        box.add(view)
        forget = Gtk.Button(label='Oublier cet iPad')

        def do_forget(*_):
            m, it = view.get_selection().get_selected()
            if it:
                self.cfg['paired'].pop(m[it][0], None)
                m.remove(it)
                save_config(self.cfg)
        forget.connect('clicked', do_forget)
        box.add(forget)
        d.show_all()
        if d.run() == Gtk.ResponseType.OK:
            self.cfg['ipad'] = ip.get_text().strip()
            self.cfg['fps'] = int(fps.get_active_id() or 60)
            self.cfg['mbps'] = int(mbps.get_value())
            if enc.get_active_id() != self.cfg['encoder']:
                self.cfg['encoder'], self.cfg['detected'] = enc.get_active_id(), ''
            self.cfg['auto_update'] = upd.get_active()
            self.cfg['ipad_only'] = only.get_active()
            self.cfg['scale'] = 'auto' if big.get_active() else '1'
            if self.session and not self.session.closed:
                threading.Thread(target=self.apply_layout, daemon=True).start()
            save_config(self.cfg)
            self.streamer.restart()
        d.destroy()

    # ---------- commandes de l'iPad (hors gestes) ----------
    def command(self, p):
        k, s = p[0], self.session
        if k == 'P' and s:
            s.send(' '.join(p))
        elif k == 'RES' and len(p) >= 3:
            w, h = int(p[1]) & ~1, int(p[2]) & ~1
            if w >= 320 and h >= 320 and (w, h) != self.want_size:
                log(f'écran de l\'iPad : {w}x{h} demandé')
                self.want_size = (w, h)
                self.streamer.restart()   # l'écran virtuel est recréé à la nouvelle taille
        elif k == 'STANDBY':   # l'iPad affiche un autre PC : on garde la liaison, sans image ni écran virtuel
            if not self.standby:
                log("iPad passé sur un autre PC : en veille")
            self.standby = True
            self.input.release()
            self.streamer.restart()
        elif k == 'ACTIVE':
            if self.standby:
                log("iPad revenu sur ce PC")
            self.standby = False
            if s:
                s.send('TB ' + urllib.parse.quote('GNOME') + ' ' + ' '.join(urllib.parse.quote(b) for b in TOUCH_BAR))
            self.streamer.restart()   # toujours un nouveau flux (« V NEW ») : l'iPad ignore celui envoyé pendant la veille
        elif k == 'SLEEP':
            log('iPad verrouillé')
            self.sleeping = True
            if s:
                s.close()
            self.streamer.restart()
        elif k == 'S' and len(p) >= 3:
            self.cfg['fps'], self.cfg['mbps'] = int(p[1]), int(p[2])
            save_config(self.cfg)
            log(f"qualité demandée par l'iPad : {p[1]} i/s, {p[2]} Mb/s")
            self.streamer.restart()

    def apply_layout(self):
        try:
            log("écran de l'iPad : " + self.screen.layout(self.cfg.get('ipad_only', True), str(self.cfg.get('scale', 'auto'))))
        except (GLib.Error, RuntimeError, IndexError, TypeError) as e:
            log(f"disposition des écrans impossible : {getattr(e, 'message', e)}")

    def keepalive(self, session):
        """Une ligne par seconde vers l'iPad (débit pour son panneau d'infos) : il répond « P », la liaison reste vivante."""
        while not session.closed:
            session.send(f"Q 0 {self.cfg['mbps']}")
            if time.time() - session.last_rx > 30:
                log("l'iPad ne répond plus depuis 30 s : déconnexion")
                session.close()
                break
            time.sleep(1)

    def read_loop(self, session):
        reason = ''
        session.sock.settimeout(None)   # le silence est surveillé par keepalive()
        try:
            while not session.closed:
                t, d = session.read_frame()
                session.last_rx = time.time()
                if t == 1:
                    for line in d.decode(errors='replace').split('\n'):
                        if line:
                            self.input.handle(line)
        except (OSError, ConnectionError, ValueError) as e:
            reason = str(e)
        if not session.closed:
            log('session terminée' + (' : ' + reason if reason else ''))
        session.close()
        self.input.release()

    # ---------- connexion (fil à part) ----------
    def connection_loop(self):
        fails, last_update = 0, 0
        while True:
            if self.cfg.get('auto_update', True) and time.time() - last_update > 1800:
                last_update = time.time()
                if check_update():
                    GLib.idle_add(self.quit_and_restart)
                    return
            if not self.wanted:
                self.set_state('Arrêté')
                time.sleep(1)
                continue
            if not self.cfg['ipad'] or fails >= 2:
                ip = find_ipad()
                if ip and ip != self.cfg['ipad']:
                    log(f'iPad trouvé : {ip}')
                    self.cfg['ipad'] = ip
                    save_config(self.cfg)
                fails = 0
                if not self.cfg['ipad']:
                    self.set_state("Recherche de l'iPad…")
                    time.sleep(3)
                    continue
            self.set_state(f"Connexion à {self.cfg['ipad']}…")
            try:
                session = Session(self.cfg, self.ctx, self.der, self.pairing)
                if not session.handshake():
                    session.close()
                    time.sleep(5)
                    continue
            except (OSError, ConnectionError, ValueError) as e:
                fails += 1
                if fails == 1:
                    log(f"connexion impossible à {self.cfg['ipad']} : {e}")
                self.set_state("En attente de l'iPad")
                time.sleep(3)
                continue
            fails = 0
            self.session, self.sleeping = session, False
            session.last_rx = time.time()
            self.standby = False   # avant la lecture : un « STANDBY » de l'iPad peut arriver tout de suite
            threading.Thread(target=self.read_loop, args=(session,), daemon=True).start()
            threading.Thread(target=self.keepalive, args=(session,), daemon=True).start()
            session.send('N ' + socket.gethostname())
            session.send('SC ' + ' '.join(urllib.parse.quote(f'{l}|{c}') for l, c in self.cfg.get('shortcuts', DEFAULT_SHORTCUTS)))
            session.send('TB ' + urllib.parse.quote('GNOME') + ' ' + ' '.join(urllib.parse.quote(b) for b in TOUCH_BAR))
            time.sleep(0.4)   # l'iPad annonce la taille de sa zone d'image (« RES w h ») dès la connexion
            while not session.closed and self.wanted:
                if self.standby:
                    if self.screen.active:
                        self.screen.stop()
                        log('écran virtuel retiré (veille)')
                        self.set_state(f'En veille : {session.name} affiche un autre PC')
                    time.sleep(0.5)
                    continue
                self.size = self.want_size
                try:
                    node = self.screen.start()
                except (GLib.Error, RuntimeError) as e:
                    log(f'écran virtuel impossible : {getattr(e, "message", e)}')
                    self.set_state('Écran virtuel impossible (voir le journal)')
                    time.sleep(5)
                    continue
                log(f'écran virtuel {self.size[0]}x{self.size[1]} créé (flux PipeWire {node})')
                self.set_state(f'Connecté : {session.name} · {self.size[0]}x{self.size[1]}')
                threading.Thread(target=self.apply_layout, daemon=True).start()
                try:
                    lasted = self.streamer.run(session, node, *self.size)
                except GLib.Error as e:
                    log(f'capture impossible : {e.message}')
                    lasted = 0
                if self.want_size != self.size or session.closed:
                    continue   # nouvelle taille : l'écran est recréé ; session finie : on sort
                if lasted < 3:
                    time.sleep(3)
            self.screen.stop()
            log('écran virtuel retiré')
            self.session = None
            self.set_state("En attente de l'iPad")
            time.sleep(1 if not getattr(self, 'sleeping', False) else 3)

    def watch_version(self):
        """Paquet mis à jour par le service système (minuterie bsidedesk-update) : on relance la nouvelle version."""
        if version() != self.started_version:
            log(f'nouvelle version installée ({version()}) : redémarrage')
            self.quit_and_restart()
        return True

    def run(self):
        self.started_version = version()
        GLib.timeout_add_seconds(60, self.watch_version)
        self.build_tray()
        threading.Thread(target=self.connection_loop, daemon=True).start()
        signal.signal(signal.SIGTERM, lambda *_: GLib.idle_add(self.quit))
        GLib.unix_signal_add(GLib.PRIORITY_DEFAULT, signal.SIGINT, lambda *_: (self.quit(), False)[1])
        Gtk.main()


def main():
    ap = argparse.ArgumentParser(description='bSideDesk pour GNOME (Wayland)')
    ap.add_argument('--pair', action='store_true', help="confirmer l'appairage d'un iPad depuis ce terminal")
    ap.add_argument('--update', action='store_true', help='rechercher et installer une mise à jour')
    ap.add_argument('--version', action='store_true')
    a = ap.parse_args()
    if a.version:
        print(version())
        return
    if a.pair:
        pair_cli()
        return
    if a.update:
        print('mise à jour installée' if check_update(True) else 'pas de mise à jour installée')
        return
    # une seule instance par session
    import fcntl
    os.makedirs(DIR, exist_ok=True)
    lock = open(os.path.join(DIR, 'instance.lock'), 'w')
    try:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
    except OSError:
        print('bSideDesk est déjà lancé (icône dans la barre du haut).')
        return
    App().run()


if __name__ == '__main__':
    main()
