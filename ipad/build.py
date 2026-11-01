# Construction de MirrorPad sur SRVHOME (chaîne Theos existante).
# Usage : MP_VERSION=1.0.5 python3 build.py   (version par défaut : 0.3)
from pathlib import Path
import subprocess, plistlib, shutil, os

VERSION = os.environ.get('MP_VERSION', '0.3')

p = Path(__file__).resolve().parent
build = p / 'build'
shutil.rmtree(build, ignore_errors=True)
app = build / 'package/Applications/MirrorPad.app'
app.mkdir(parents=True)
info = {
    'CFBundleIdentifier': 'local.btv.mirrorpad', 'CFBundleName': 'MirrorPad', 'CFBundleDisplayName': 'MirrorPad',
    'CFBundleExecutable': 'MirrorPad', 'CFBundlePackageType': 'APPL', 'CFBundleVersion': VERSION,
    'CFBundleShortVersionString': VERSION, 'MinimumOSVersion': '12.0', 'UIDeviceFamily': [2],
    'LSRequiresIPhoneOS': True, 'UIRequiresFullScreen': True, 'UIStatusBarHidden': True,
    'UISupportedInterfaceOrientations': ['UIInterfaceOrientationLandscapeLeft', 'UIInterfaceOrientationLandscapeRight', 'UIInterfaceOrientationPortrait', 'UIInterfaceOrientationPortraitUpsideDown'],
    'UISupportedInterfaceOrientations~ipad': ['UIInterfaceOrientationLandscapeLeft', 'UIInterfaceOrientationLandscapeRight', 'UIInterfaceOrientationPortrait', 'UIInterfaceOrientationPortraitUpsideDown'],
    'UILaunchScreen': {},
    'CFBundleIconFiles': ['AppIcon76x76'],
    'CFBundleURLTypes': [{'CFBundleURLName': 'local.btv.mirrorpad', 'CFBundleURLSchemes': ['mirrorpad']}],
    'CFBundleIcons~ipad': {'CFBundlePrimaryIcon': {'CFBundleIconFiles': ['AppIcon76x76']}},
}
(app / 'Info.plist').write_bytes(plistlib.dumps(info))
for icon in [*p.glob('AppIcon*.png'), *p.glob('Logo*.png')]:
    shutil.copy(icon, app / icon.name)
ent = {'application-identifier': 'local.btv.mirrorpad', 'platform-application': True,
       'com.apple.private.security.no-container': True, 'com.apple.private.security.no-sandbox': True}
(build / 'entitlements.plist').write_bytes(plistlib.dumps(ent))

tool = '/home/bender/theos/toolchain/linux/iphone/bin/'
sdk = '/home/bender/theos/sdks/iPhoneOS16.5.sdk'
subprocess.run([tool + 'clang', '-target', 'arm64-apple-ios12.0', '-isysroot', sdk, '-B' + tool,
                '-fobjc-arc', '-fblocks', '-O2', '-Wall', str(p / 'MirrorPad.m'),
                '-framework', 'UIKit', '-framework', 'Foundation', '-framework', 'AVFoundation',
                '-framework', 'CoreMedia', '-framework', 'QuartzCore', '-framework', 'CoreGraphics',
                '-o', str(app / 'MirrorPad')], check=True)
subprocess.run([tool + 'ldid', '-S' + str(build / 'entitlements.plist'), str(app / 'MirrorPad')], check=True)
meta = build / 'package/DEBIAN'; meta.mkdir()
(meta / 'control').write_text('Package: local.btv.mirrorpad\nName: MirrorPad\nVersion: ' + VERSION + '\nArchitecture: iphoneos-arm\n'
                              'Maintainer: Benjamin\nDescription: Ecran secondaire pour PC Windows (flux H.264 sur Wi-Fi).\nSection: Utilities\n')
subprocess.run(['dpkg-deb', '--root-owner-group', '--build', str(build / 'package'),
                str(build / f'mirrorpad_{VERSION}_iphoneos-arm.deb')], check=True)
print('BUILD_OK', build / f'mirrorpad_{VERSION}_iphoneos-arm.deb')
