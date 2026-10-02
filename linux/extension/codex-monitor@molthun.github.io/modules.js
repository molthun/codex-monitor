// Where the newest copy of widget.js / settings.js lives. Shared by the loader (extension.js)
// and prefs.js, so it may only use GLib: the preferences process has no GNOME Shell modules.

import GLib from 'gi://GLib';

export const WIDGET_DIR = GLib.build_filenamev([GLib.get_user_data_dir(), 'codex-monitor', 'widget']);
export const CURRENT = GLib.build_filenamev([WIDGET_DIR, 'current']);

// The versioned folder install.sh points `current` at, else the copy bundled with the extension.
export function moduleUri(extensionPath, file) {
    try {
        const [, bytes] = GLib.file_get_contents(CURRENT);
        const path = GLib.build_filenamev([WIDGET_DIR, new TextDecoder().decode(bytes).trim(), file]);
        if (GLib.file_test(path, GLib.FileTest.EXISTS))
            return GLib.filename_to_uri(path, null);
    } catch (e) {
        // No versioned copy yet.
    }
    return GLib.filename_to_uri(GLib.build_filenamev([extensionPath, file]), null);
}
