// CodexMonitor loader for GNOME Shell.
//
// GNOME Shell loads extension code only at login, so this file stays small and stable.
// The widget itself is widget.js: install.sh also copies it into a versioned folder
// (~/.local/share/codex-monitor/widget/<version>/) and points `current` at it. When that
// pointer changes, the loader imports the new copy and restarts the widget, so updates
// apply without logging out. Only a change of this file needs a new login.

import GLib from 'gi://GLib';
import Gio from 'gi://Gio';

import {Extension} from 'resource:///org/gnome/shell/extensions/extension.js';

import {CURRENT, WIDGET_DIR, moduleUri} from './modules.js';

// Bumped when the contract with widget.js changes (createController arguments, loader methods).
const LOADER_API = 1;

export default class CodexMonitorExtension extends Extension {
    enable() {
        this._generation = 0;
        GLib.mkdir_with_parents(WIDGET_DIR, 0o755);
        this._monitor = Gio.File.new_for_path(CURRENT).monitor_file(Gio.FileMonitorFlags.NONE, null);
        this._monitor.connect('changed', (_m, _f, _o, event) => {
            if (event === Gio.FileMonitorEvent.CHANGES_DONE_HINT || event === Gio.FileMonitorEvent.CREATED)
                this.reload();
        });
        this._load().catch(logError);
    }

    disable() {
        this._generation++;
        if (this._reloadId) {
            GLib.source_remove(this._reloadId);
            this._reloadId = 0;
        }
        this._monitor?.cancel();
        this._monitor = null;
        this._controller?.disable();
        this._controller = null;
    }

    // Restart the widget with the newest code (also the "Restart widget" menu item).
    reload() {
        if (this._reloadId)
            GLib.source_remove(this._reloadId);
        this._reloadId = GLib.timeout_add(GLib.PRIORITY_DEFAULT, 300, () => {
            this._reloadId = 0;
            this._load().catch(logError);
            return GLib.SOURCE_REMOVE;
        });
    }

    async _load() {
        const generation = ++this._generation;
        let module;
        try {
            module = await import(moduleUri(this.path, 'widget.js'));
        } catch (e) {
            logError(e, 'CodexMonitor: the installed widget failed to load, using the bundled one');
            module = await import(GLib.filename_to_uri(GLib.build_filenamev([this.path, 'widget.js']), null));
        }
        // Disabled or reloaded again while importing.
        if (generation !== this._generation)
            return;
        if ((module.LOADER_API_MIN ?? 1) > LOADER_API) {
            // Newer widget than this loader supports: keep the running one until the next login.
            this._controller?.loaderOutdated?.();
            return;
        }
        this._controller?.disable();
        this._controller = module.createController(this, {api: LOADER_API, reload: () => this.reload()});
        this._controller.enable();
    }
}
