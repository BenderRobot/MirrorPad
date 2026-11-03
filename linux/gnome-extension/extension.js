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
        this._timers.forEach(id => GLib.source_remove(id));
        this._timers = [];
        [[250, 1], [900, 1], [1800, 2], [3000, 3]].forEach(([ms, step]) => {
            const id = GLib.timeout_add(GLib.PRIORITY_DEFAULT, ms, () => {
                this._timers = this._timers.filter(t => t !== id);
                this._refit(step);
                return GLib.SOURCE_REMOVE;
            });
            this._timers.push(id);
        });
    }

    // ce que GNOME affiche réellement dans la fenêtre : surfaces de l'application (et sous-surfaces), position et taille
    _tree(a, depth, out) {
        if (depth > 4)
            return;
        for (const c of a.get_children()) {
            const [x, y] = c.get_position();
            const [w, h] = c.get_size();
            const name = (c.constructor && c.constructor.$gtype) ? c.constructor.$gtype.name : 'acteur';
            out.push(`${depth}:${name} ${Math.round(w)}x${Math.round(h)}+${Math.round(x)}+${Math.round(y)}${c.visible ? '' : ' (caché)'}`);
            this._tree(c, depth + 1, out);
        }
    }

    // step 1 : recaler le cadre ; 2 : forcer un déplacement ; 3 : dernier recours, plein écran retiré/remis d'un coup
    _refit(step) {
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
                const b = win.get_buffer_rect();
                const [ax, ay] = actor.get_position();
                const same = r => r.x === g.x && r.y === g.y && r.width === g.width && r.height === g.height;
                const ok = same(f) && b.x === g.x && b.y === g.y && Math.round(ax) === g.x && Math.round(ay) === g.y;
                const tree = [];
                this._tree(actor, 0, tree);
                log(`bSideDesk: passe ${step} ×${global.display.get_monitor_scale(i)} ` +
                    `${win.get_client_type() === 0 ? 'Wayland' : 'X11'} contenu : ${tree.join(' | ')}`);
                log(`bSideDesk: passe ${step} « ${win.get_title()} » écran ${g.width}x${g.height}+${g.x}+${g.y} ` +
                    `cadre ${f.width}x${f.height}+${f.x}+${f.y} tampon ${b.width}x${b.height}+${b.x}+${b.y} ` +
                    `acteur +${Math.round(ax)}+${Math.round(ay)} ${ok ? 'OK' : 'décalé'}`);
                if (ok)
                    continue;
                if (step === 1) {
                    win.move_resize_frame(false, g.x, g.y, g.width, g.height);
                } else if (step === 2) {
                    win.move_frame(false, g.x + 1, g.y + 1);   // un vrai déplacement : GNOME renvoie la position au client
                    win.move_frame(false, g.x, g.y);
                } else {
                    win.unmake_fullscreen();
                    win.make_fullscreen();
                }
            } catch (e) {
                logError(e, 'bSideDesk');
            }
        }
    }
}

function init() {
    return new BSideDeskExtension();
}
