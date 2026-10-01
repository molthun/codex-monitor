// CodexMonitor settings window (the extension's preferences). Like extension.js, this
// stays small: the window is built by settings.js, imported from the newest installed copy.

import {ExtensionPreferences} from 'resource:///org/gnome/Shell/Extensions/js/extensions/prefs.js';

import {moduleUri} from './modules.js';

export default class CodexMonitorPreferences extends ExtensionPreferences {
    async fillPreferencesWindow(window) {
        const settings = await import(moduleUri(this.path, 'settings.js'));
        settings.fillPreferencesWindow(window, this);
    }
}
