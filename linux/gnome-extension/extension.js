// bSideDesk : après une rotation de l'iPad (écran virtuel redimensionné), GNOME 43 laisse le contenu des fenêtres
// plein écran décalé : il « centre » la surface de l'application comme si elle était plus petite que l'écran
// (MetaSurfaceContainerActorWayland à -128 px), alors qu'elle fait déjà la bonne taille. On recale ce contenu.
const { GLib } = imports.gi;
const Main = imports.ui.main;

const typeName = a => (a && a.constructor && a.constructor.$gtype) ? a.constructor.$gtype.name : '';

class BSideDeskExtension {
    enable() {
        this._timers = [];
        this._sig = Main.layoutManager.connect('monitors-changed', () => this._schedule());
        // entrée / sortie du plein écran : on revérifie aussi (et on retire notre correction si elle ne sert plus)
        this._fsSig = global.display.connect('in-fullscreen-changed', () => this._schedule());
    }

    disable() {
        if (this._sig)
            Main.layoutManager.disconnect(this._sig);
        if (this._fsSig)
            global.display.disconnect(this._fsSig);
        this._sig = this._fsSig = 0;
        this._timers.forEach(id => GLib.source_remove(id));
        this._timers = [];
        if (this._idle)
            GLib.source_remove(this._idle);
        this._idle = 0;
        for (const actor of global.get_window_actors()) {   // remettre les fenêtres telles que GNOME les place
            const c = this._parts(actor).container;
            if (c) {
                if (c._bsdHook)
                    c.disconnect(c._bsdHook);
                c._bsdHook = 0;
                c.set_translation(0, 0, 0);
            }
        }
    }

    _schedule() {
        this._timers.forEach(id => GLib.source_remove(id));
        this._timers = [];
        [[150, 1], [600, 2], [1500, 2], [3000, 2], [5000, 2], [5200, 9]].forEach(([ms, step]) => {
            const id = GLib.timeout_add(GLib.PRIORITY_DEFAULT, ms, () => {
                this._timers = this._timers.filter(t => t !== id);
                this._refit(step);
                return GLib.SOURCE_REMOVE;
            });
            this._timers.push(id);
        });
    }

    _soon() {
        if (this._idle)
            return;
        this._idle = GLib.idle_add(GLib.PRIORITY_DEFAULT_IDLE, () => {
            this._idle = 0;
            this._refit(2);
            return GLib.SOURCE_REMOVE;
        });
    }

    // fond noir de GNOME (ClutterActor) et conteneur des surfaces de l'application
    _parts(actor) {
        let background = null, container = null;
        for (const c of actor.get_children()) {
            const n = typeName(c);
            if (n === 'MetaSurfaceContainerActorWayland')
                container = c;
            else if (n === 'ClutterActor')
                background = c;
        }
        return { background, container };
    }

    // ce que GNOME affiche réellement : pour chaque élément, mise en page, correction et position à l'écran
    _tree(a, depth, out) {
        if (depth > 4)
            return;
        for (const c of a.get_children()) {
            const [x, y] = c.get_position();
            const [w, h] = c.get_size();
            const [sx, sy] = c.get_transformed_position();
            const [sw, sh] = c.get_transformed_size();
            out.push(`${depth}:${typeName(c) || 'acteur'} ${Math.round(w)}x${Math.round(h)}+${Math.round(x)}+${Math.round(y)}` +
                ` t${Math.round(c.translation_x)},${Math.round(c.translation_y)} éch${c.scale_x}` +
                ` écran ${Math.round(sw)}x${Math.round(sh)}+${Math.round(sx)}+${Math.round(sy)}${c.visible ? '' : ' (caché)'}`);
            this._tree(c, depth + 1, out);
        }
    }

    // step 1 : demander à GNOME de refaire la mise en page ; 2 : compenser à l'affichage ce qui reste décalé.
    // La compensation tient compte de celle déjà appliquée : elle suit GNOME s'il bouge encore le contenu.
    // Passe 9 : contrôle, tout ce qui est affiché dans la fenêtre est écrit dans le journal.
    _refit(step) {
        for (const actor of global.get_window_actors()) {
            const win = actor.meta_window;
            try {
                if (!win)
                    continue;
                const { background, container } = this._parts(actor);
                if (!container)
                    continue;
                if (!container._bsdHook)   // GNOME refait la mise en page plus tard : notre correction suit
                    container._bsdHook = container.connect('notify::allocation', () => this._soon());
                const [cx, cy] = container.get_position();
                const [cw, ch] = container.get_size();
                const [tx, ty] = [container.translation_x, container.translation_y];
                const covers = win.is_fullscreen() && background &&
                    Math.abs(background.get_width() - cw) < 1 && Math.abs(background.get_height() - ch) < 1;
                if (!covers) {
                    if (tx || ty) {
                        container.set_translation(0, 0, 0);
                        log(`bSideDesk: « ${win.get_title()} » correction retirée`);
                    }
                    continue;
                }
                const [bx, by] = background.get_position();
                const dx = cx + tx - bx, dy = cy + ty - by;   // décalage affiché, en unités de la fenêtre
                if (step === 9) {
                    const tree = [];
                    this._tree(actor, 0, tree);
                    log(`bSideDesk: contrôle « ${win.get_title()} » fenêtre ${actor.get_transformed_size().map(Math.round).join('x')} ` +
                        `+${actor.get_transformed_position().map(Math.round).join('+')} : ${tree.join(' | ')}`);
                    continue;
                }
                if (Math.abs(dx) < 0.5 && Math.abs(dy) < 0.5)
                    continue;
                if (step === 1 && !tx && !ty) {
                    actor.queue_relayout();
                    log(`bSideDesk: « ${win.get_title()} » contenu ${Math.round(cw)}x${Math.round(ch)} décalé de ${Math.round(dx)},${Math.round(dy)} : nouvelle mise en page demandée`);
                    continue;
                }
                container.set_translation(tx - dx, ty - dy, 0);
                log(`bSideDesk: « ${win.get_title()} » contenu ${Math.round(cw)}x${Math.round(ch)} mise en page +${Math.round(cx)}+${Math.round(cy)}, ` +
                    `affiché décalé de ${Math.round(dx)},${Math.round(dy)} -> correction ${Math.round(tx - dx)},${Math.round(ty - dy)}`);
            } catch (e) {
                logError(e, 'bSideDesk');
            }
        }
    }
}

function init() {
    return new BSideDeskExtension();
}
