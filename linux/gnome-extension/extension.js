// bSideDesk : quand l'écran de l'iPad change de taille (rotation), GNOME 43 laisse les fenêtres plein écran
// (vidéo YouTube…) à leur ancienne taille. On les recale sur leur écran, sans quitter le plein écran.
const { GLib } = imports.gi;
const Main = imports.ui.main;

class BSideDeskExtension {
    enable() {
        this._timers = [];
        this._sig = Main.layoutManager.connect('monitors-changed', () => this._schedule());
    }

    disable() {
        if (this._sig)
            Main.layoutManager.disconnect(this._sig);
        this._sig = 0;
        this._timers.forEach(id => GLib.source_remove(id));
        this._timers = [];
    }

    _schedule() {
        // plusieurs passes : l'application suit la nouvelle taille (et l'échelle ×2) avec un peu de retard
        for (const ms of [250, 900, 2000]) {
            const id = GLib.timeout_add(GLib.PRIORITY_DEFAULT, ms, () => {
                this._timers = this._timers.filter(t => t !== id);
                this._refit();
                return GLib.SOURCE_REMOVE;
            });
            this._timers.push(id);
        }
    }

    _refit() {
        for (const actor of global.get_window_actors()) {
            const win = actor.meta_window;
            try {
                if (!win || !win.is_fullscreen())
                    continue;
                const i = win.get_monitor();
                if (i < 0)
                    continue;
                const g = global.display.get_monitor_geometry(i);
                const f = win.get_frame_rect();
                if (f.x === g.x && f.y === g.y && f.width === g.width && f.height === g.height)
                    continue;
                log(`bSideDesk: plein écran recalé « ${win.get_title()} » ${f.width}x${f.height}+${f.x}+${f.y} -> ${g.width}x${g.height}+${g.x}+${g.y}`);
                win.move_resize_frame(false, g.x, g.y, g.width, g.height);
            } catch (e) {
                logError(e, 'bSideDesk');
            }
        }
    }
}

function init() {
    return new BSideDeskExtension();
}
