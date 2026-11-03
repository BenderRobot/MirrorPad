#!/bin/sh
# Construit le paquet Debian de bSideDesk (GNOME / Wayland) : sh linux/build-deb.sh 1.0.60
# -> linux/build/bsidedesk_<version>_all.deb  (utilisé par GitHub Actions, publié dans chaque release)
set -e
V="${1:-0.0.0}"
cd "$(dirname "$0")"
R=build/pkg
rm -rf build
mkdir -p "$R/DEBIAN" "$R/usr/lib/bsidedesk" "$R/usr/bin" "$R/usr/share/applications" "$R/etc/xdg/autostart" \
         "$R/usr/share/pixmaps" "$R/usr/share/polkit-1/actions" "$R/usr/share/doc/bsidedesk"
install -m 755 bsidedesk.py "$R/usr/lib/bsidedesk/bsidedesk.py"
install -m 755 bsidedesk-update "$R/usr/lib/bsidedesk/bsidedesk-update"
echo "$V" > "$R/usr/lib/bsidedesk/version.txt"
printf '#!/bin/sh\nexec /usr/bin/python3 /usr/lib/bsidedesk/bsidedesk.py "$@"\n' > "$R/usr/bin/bsidedesk"
chmod 755 "$R/usr/bin/bsidedesk"
install -m 644 bsidedesk.desktop "$R/usr/share/applications/bsidedesk.desktop"
install -m 644 bsidedesk.desktop "$R/etc/xdg/autostart/bsidedesk.desktop"
install -m 644 ../windows/bSideDesk.png "$R/usr/share/pixmaps/bsidedesk.png"
install -m 644 fr.bsidedesk.update.policy "$R/usr/share/polkit-1/actions/fr.bsidedesk.update.policy"
install -m 644 ../README.md "$R/usr/share/doc/bsidedesk/README.md"
cat > "$R/DEBIAN/control" <<CTRL
Package: bsidedesk
Version: $V
Architecture: all
Section: x11
Priority: optional
Maintainer: Benjamin Laurens <noreply@github.com>
Depends: python3 (>= 3.9), python3-gi, python3-gst-1.0, gir1.2-gtk-3.0, gir1.2-gstreamer-1.0, gir1.2-gst-plugins-base-1.0,
 gstreamer1.0-pipewire, gstreamer1.0-plugins-base, gstreamer1.0-plugins-good, gstreamer1.0-plugins-bad, gstreamer1.0-plugins-ugly,
 gir1.2-ayatanaappindicator3-0.1, gnome-shell-extension-appindicator, openssl, pkexec | policykit-1, libnotify-bin
Recommends: gstreamer1.0-vaapi, intel-media-va-driver | i965-va-driver | mesa-va-drivers
Homepage: https://github.com/BenderRobot/bsidedesk
Description: L'iPad comme écran supplémentaire tactile (GNOME Wayland)
 bSideDesk crée un vrai écran supplémentaire dans GNOME (écran virtuel de Mutter),
 le diffuse en H.264 vers l'app bSideDesk de l'iPad et rejoue ses gestes
 (multi-touch, souris, clavier). Mises à jour automatiques depuis GitHub.
CTRL
dpkg-deb --root-owner-group --build "$R" "build/bsidedesk_${V}_all.deb"
echo "BUILD_OK build/bsidedesk_${V}_all.deb"
