#!/usr/bin/env bash
# NitroPower development install (until the .deb of phase 4): the daemon, the
# CLI and the GUI from artifacts/, a systemd unit, the application launcher and
# the notification autostart.
#
#   packaging/build.sh                                  first, as your user
#   sudo packaging/install-dev.sh [install] [--replace-damx]
#   sudo packaging/install-dev.sh remove                undo (state and presets are kept)
#
# Replaces nitro-gpu-oc (GPU overclock) and the GNOME shortcut that opened DAMX
# on the Nitro key. --replace-damx also stops the DAMX daemon, hides its
# launcher (NitroPower's window does everything it did) and drops its sysfs
# group permissions. Everything replaced is remembered and restored by "remove".
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
LIB=/usr/lib/nitropower
UNIT=/etc/systemd/system/nitropowerd.service
BIN=/usr/local/bin/nitropower-cli
GUI_BIN=/usr/local/bin/nitropower
APPS=/usr/local/share/applications
ICONS=/usr/local/share/icons/hicolor
AUTOSTART=/etc/xdg/autostart/nitropower-notify.desktop
DAMX_TMPFILES=/etc/tmpfiles.d/linuwu_sense.conf
BACKUP=/var/lib/nitropower/legacy
GROUP=nitropower
KEYS=org.gnome.settings-daemon.plugins.media-keys
NITRO_KEY_PATH=/org/gnome/settings-daemon/plugins/media-keys/custom-keybindings/nitro-key/

die() { echo "install-dev: $*" >&2; exit 1; }
[[ $EUID -eq 0 ]] || die "run with sudo"
USER_NAME="${SUDO_USER:-}"

ACTION=install
REPLACE_DAMX=0
for arg in "$@"; do
    case "$arg" in
        install) ACTION=install ;;
        remove|uninstall) ACTION=remove ;;
        --replace-damx) REPLACE_DAMX=1 ;;
        *) die "unknown argument '$arg'" ;;
    esac
done

# gsettings in the desktop session of the user who ran sudo
as_user() {
    [[ -n "$USER_NAME" ]] || return 1
    local uid
    uid=$(id -u "$USER_NAME")
    [[ -S "/run/user/$uid/bus" ]] || return 1
    sudo -u "$USER_NAME" DBUS_SESSION_BUS_ADDRESS="unix:path=/run/user/$uid/bus" "$@"
}

# add|remove the nitro-key entry in the GNOME custom keybinding list
gnome_shortcut() {
    local list new
    list=$(as_user gsettings get "$KEYS" custom-keybindings 2>/dev/null) || return 0
    new=$(python3 -I - "$1" "$list" "$NITRO_KEY_PATH" <<'PY'
import ast, sys
action, text, path = sys.argv[1:4]
items = [] if text.startswith("@as") else ast.literal_eval(text)
if action == "remove":
    items = [x for x in items if x != path]
elif path not in items:
    items.append(path)
print(str(items) if items else "@as []")
PY
)
    if [[ "$new" != "$list" ]]; then
        as_user gsettings set "$KEYS" custom-keybindings "$new"
    fi
}

refresh_menus() {
    gtk-update-icon-cache -q -t -f "$ICONS" 2>/dev/null || true
    update-desktop-database -q "$APPS" 2>/dev/null || true
}

install_all() {
    [[ -x "$ROOT/artifacts/nitropowerd/nitropowerd" && -x "$ROOT/artifacts/cli/nitropower-cli" &&
       -x "$ROOT/artifacts/gui/nitropower" ]] ||
        die "build first, as your user: $ROOT/packaging/build.sh"
    [[ -d /usr/lib/dotnet/shared/Microsoft.NETCore.App ]] || die "the .NET 10 runtime is missing (dotnet-runtime-10.0)"

    getent group "$GROUP" >/dev/null || groupadd --system "$GROUP"
    if [[ -n "$USER_NAME" ]]; then
        usermod -aG "$GROUP" "$USER_NAME"
    fi

    systemctl stop nitropowerd.service 2>/dev/null || true
    rm -rf "$LIB"
    install -d -o root -g root -m 755 "$LIB"
    # Never keep the builder's ownership: root runs these files, so only root may change them
    cp -r --no-preserve=ownership "$ROOT/artifacts/nitropowerd" "$LIB/daemon"
    cp -r --no-preserve=ownership "$ROOT/artifacts/cli" "$LIB/cli"
    cp -r --no-preserve=ownership "$ROOT/artifacts/gui" "$LIB/gui"
    chown -R root:root "$LIB"
    chmod -R u+rwX,go+rX,go-w "$LIB"
    printf '#!/bin/sh\nexec %s/cli/nitropower-cli "$@"\n' "$LIB" > "$BIN"
    printf '#!/bin/sh\nexec %s/gui/nitropower "$@"\n' "$LIB" > "$GUI_BIN"
    chmod 755 "$BIN" "$GUI_BIN"
    install -D -m 644 "$ROOT/packaging/xdg/nitropower.desktop" "$APPS/nitropower.desktop"
    install -D -m 644 "$ROOT/src/NitroPower.Gui/Assets/nitropower.svg" "$ICONS/scalable/apps/nitropower.svg"
    install -D -m 644 "$ROOT/src/NitroPower.Gui/Assets/nitropower-256.png" "$ICONS/256x256/apps/nitropower.png"
    install -D -m 644 "$ROOT/src/NitroPower.Gui/Assets/nitropower-64.png" "$ICONS/64x64/apps/nitropower.png"
    install -D -m 644 "$ROOT/src/NitroPower.Gui/Assets/nitropower-32.png" "$ICONS/32x32/apps/nitropower.png"
    refresh_menus
    install -m 644 "$ROOT/packaging/systemd/nitropowerd.service" "$UNIT"
    install -m 644 "$ROOT/packaging/xdg/nitropower-notify.desktop" "$AUTOSTART"

    install -d -m 755 "$BACKUP"
    if systemctl is-enabled nitro-gpu-oc.service >/dev/null 2>&1; then
        touch "$BACKUP/nitro-gpu-oc.enabled"
        systemctl disable --now nitro-gpu-oc.service
        echo "nitro-gpu-oc disabled (nitropowerd applies the GPU overclock)"
    fi
    if as_user gsettings get "$KEYS" custom-keybindings 2>/dev/null | grep -q "nitro-key"; then
        touch "$BACKUP/gnome-nitro-key"
        gnome_shortcut remove
        echo "GNOME Nitro key shortcut removed (nitropowerd handles the key)"
    fi
    if [[ $REPLACE_DAMX == 1 ]] && systemctl is-enabled damx-daemon.service >/dev/null 2>&1; then
        touch "$BACKUP/damx-daemon.enabled"
        systemctl disable --now damx-daemon.service
        echo "damx-daemon disabled"
    fi
    if [[ $REPLACE_DAMX == 1 && -f "$DAMX_TMPFILES" ]]; then
        # DAMX let the linuwu_sense group write fans and battery settings
        # directly; with NitroPower only the daemon writes the hardware
        mv "$DAMX_TMPFILES" "$BACKUP/linuwu_sense.tmpfiles.conf"
        for attr in fan_speed battery_limiter battery_calibration usb_charging; do
            f="/sys/devices/platform/acer-wmi/nitro_sense/$attr"
            [[ -e "$f" ]] && { chgrp root "$f" && chmod 644 "$f"; } || true
        done
        echo "DAMX's sysfs group permissions removed (nitropowerd is the only writer)"
    fi
    if [[ $REPLACE_DAMX == 1 && -f /usr/share/applications/damx.desktop && ! -e "$APPS/damx.desktop" ]]; then
        # An entry in /usr/local/share overrides the one in /usr/share
        printf '[Desktop Entry]\nType=Application\nName=DAMX\nNoDisplay=true\nHidden=true\n' > "$APPS/damx.desktop"
        touch "$BACKUP/damx-launcher.hidden"
        refresh_menus
        echo "DAMX launcher hidden (use NitroPower)"
    fi

    systemctl daemon-reload
    systemctl enable --now nitropowerd.service
    sleep 1
    systemctl --no-pager --lines=15 status nitropowerd.service || true
    echo
    echo "Installed. Log out and back in once: then '$USER_NAME' is in the '$GROUP' group,"
    echo "NitroPower (in the applications menu) and nitropower-cli work without sudo,"
    echo "and the notifications start with the session."
}

remove_all() {
    systemctl disable --now nitropowerd.service 2>/dev/null || true
    rm -f "$UNIT" "$BIN" "$GUI_BIN" "$AUTOSTART" "$APPS/nitropower.desktop"
    rm -f "$ICONS"/{scalable/apps/nitropower.svg,256x256/apps/nitropower.png,64x64/apps/nitropower.png,32x32/apps/nitropower.png}
    rm -rf "$LIB"
    if [[ -e "$BACKUP/linuwu_sense.tmpfiles.conf" ]]; then
        mv "$BACKUP/linuwu_sense.tmpfiles.conf" "$DAMX_TMPFILES"
        systemd-tmpfiles --create "$DAMX_TMPFILES" || true
    fi
    if [[ -e "$BACKUP/damx-launcher.hidden" ]]; then
        rm -f "$APPS/damx.desktop" "$BACKUP/damx-launcher.hidden"
    fi
    refresh_menus
    systemctl daemon-reload
    if [[ -e "$BACKUP/nitro-gpu-oc.enabled" ]]; then
        systemctl enable --now nitro-gpu-oc.service && rm -f "$BACKUP/nitro-gpu-oc.enabled"
    fi
    if [[ -e "$BACKUP/damx-daemon.enabled" ]]; then
        systemctl enable --now damx-daemon.service && rm -f "$BACKUP/damx-daemon.enabled"
    fi
    if [[ -e "$BACKUP/gnome-nitro-key" ]]; then
        gnome_shortcut add && rm -f "$BACKUP/gnome-nitro-key"
    fi
    echo "Removed. Kept: /var/lib/nitropower (state), /etc/nitropower (presets) and the '$GROUP' group."
}

if [[ $ACTION == install ]]; then
    install_all
else
    remove_all
fi
