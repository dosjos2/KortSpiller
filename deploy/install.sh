#!/usr/bin/env bash
# Kjøres på Raspberry Pi-en:  sudo ./install.sh
# Forutsetter at mappen "player" (output fra publish.ps1) ligger ved siden av dette skriptet.
set -euo pipefail

if [[ $EUID -ne 0 ]]; then echo "Kjør med sudo."; exit 1; fi
HERE="$(cd "$(dirname "$0")" && pwd)"
SRC="$HERE/player"
DEST=/opt/kortspiller

[[ -d "$SRC" ]] || { echo "Fant ikke $SRC"; exit 1; }

# Bibliotek for GPIO (Bookworm = libgpiod2, Trixie = libgpiod3)
apt-get update -y
apt-get install -y rsync
apt-get install -y libgpiod2 || apt-get install -y libgpiod3 || true

id kortspiller &>/dev/null || useradd --system --create-home --home-dir /var/lib/kortspiller kortspiller
for g in gpio spi i2c input dialout; do getent group "$g" >/dev/null && usermod -aG "$g" kortspiller || true; done

mkdir -p "$DEST/data"
# Ikke overskriv konfig og token ved oppdatering
rsync -a --exclude 'appsettings.local.json' --exclude 'data' "$SRC"/ "$DEST"/
[[ -f "$DEST/appsettings.local.json" ]] || echo '{}' > "$DEST/appsettings.local.json"
chmod +x "$DEST/KortSpiller.Player"
chown -R kortspiller:kortspiller "$DEST"
chmod 600 "$DEST/appsettings.local.json"

install -m 644 "$HERE/kortspiller-player.service" /etc/systemd/system/kortspiller-player.service
systemctl daemon-reload
systemctl enable kortspiller-player.service
systemctl restart kortspiller-player.service

echo
echo "Ferdig. Se logg med:  journalctl -u kortspiller-player -f"
echo "Husk: legg Spotify ClientId og mapping-URL i $DEST/appsettings.local.json og kopier spotify-token.json til $DEST/data/"
