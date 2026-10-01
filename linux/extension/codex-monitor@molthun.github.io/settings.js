// CodexMonitor settings window, built by prefs.js from the newest installed copy.
//
// Everything is stored in ~/.config/codex-monitor/config.json, which both the widget and
// the bridge watch: changes apply right away. The hardware lists come from inventory.json,
// which the bridge writes with what this PC has (GPUs, fan chips with live RPM, drives).

import Adw from 'gi://Adw';
import Gio from 'gi://Gio';
import GLib from 'gi://GLib';
import Gtk from 'gi://Gtk';

Gio._promisify(Gio.Subprocess.prototype, 'communicate_utf8_async');
Gio._promisify(Gio.Subprocess.prototype, 'wait_check_async');

const CONFIG_PATH = GLib.build_filenamev([GLib.get_user_config_dir(), 'codex-monitor', 'config.json']);
const DATA_DIR = GLib.build_filenamev([GLib.get_user_data_dir(), 'codex-monitor']);
const UPDATER_PATH = GLib.build_filenamev([DATA_DIR, 'update.sh']);
const MAX_DISKS = 6;

const SECTIONS = [
    ['health', 'Health strip', 'CPU, GPU, RAM and fan status at the top'],
    ['performance', 'Performance', 'CPU, RAM, GPU and VRAM load'],
    ['temperatures', 'Temperatures', ''],
    ['cooling', 'Cooling', 'Fan speeds'],
    ['network', 'Network traffic', 'Internet/LAN bars, graph and top processes'],
    ['diskIO', 'Disk I/O', ''],
    ['drives', 'Drives used', ''],
];

function readJson(path) {
    try {
        const [, bytes] = GLib.file_get_contents(path);
        return JSON.parse(new TextDecoder().decode(bytes));
    } catch (e) {
        return null;
    }
}

function formatBytes(bytes) {
    const units = ['B', 'KB', 'MB', 'GB', 'TB'];
    let i = 0;
    while (bytes >= 1000 && i < units.length - 1) {
        bytes /= 1000;
        i++;
    }
    return `${bytes.toFixed(bytes < 10 && i > 0 ? 1 : 0)} ${units[i]}`;
}

/** config.json with dotted-path access; writes are batched so spin buttons don't flood the file. */
class Config {
    constructor() {
        this.json = readJson(CONFIG_PATH) ?? {};
    }

    get(path, fallback) {
        let node = this.json;
        for (const key of path.split('.')) {
            if (node === null || typeof node !== 'object' || !(key in node))
                return fallback;
            node = node[key];
        }
        return node ?? fallback;
    }

    set(path, value) {
        const keys = path.split('.');
        let node = this.json;
        for (const key of keys.slice(0, -1)) {
            if (node[key] === null || typeof node[key] !== 'object')
                node[key] = {};
            node = node[key];
        }
        node[keys.at(-1)] = value;
        this._scheduleSave();
    }

    _scheduleSave() {
        if (this._saveId)
            GLib.source_remove(this._saveId);
        this._saveId = GLib.timeout_add(GLib.PRIORITY_DEFAULT, 500, () => {
            this._saveId = 0;
            this.save();
            return GLib.SOURCE_REMOVE;
        });
    }

    save() {
        GLib.mkdir_with_parents(GLib.path_get_dirname(CONFIG_PATH), 0o755);
        GLib.file_set_contents(CONFIG_PATH, `${JSON.stringify(this.json, null, 2)}\n`);
    }

    // Pending changes must not be lost when the window closes right after an edit.
    flush() {
        if (this._saveId) {
            GLib.source_remove(this._saveId);
            this._saveId = 0;
            this.save();
        }
    }
}

// ------------------------------------------------------------------ rows

function switchRow(config, path, title, subtitle, fallback = true) {
    const row = new Adw.SwitchRow({title, subtitle: subtitle ?? '', active: config.get(path, fallback)});
    row.connect('notify::active', () => config.set(path, row.active));
    return row;
}

function spinRow(config, path, title, subtitle, min, max, step, fallback = 0) {
    const row = Adw.SpinRow.new_with_range(min, max, step);
    row.set({title, subtitle: subtitle ?? '', value: config.get(path, fallback)});
    row.connect('notify::value', () => config.set(path, Math.round(row.value)));
    return row;
}

/** Drop-down over [value, label] pairs; unknown stored values are added so nothing is lost. */
function comboRow(config, path, title, subtitle, choices, fallback) {
    const current = config.get(path, fallback);
    if (!choices.some(([value]) => value === current))
        choices = [...choices, [current, String(current)]];
    const row = new Adw.ComboRow({
        title,
        subtitle: subtitle ?? '',
        model: Gtk.StringList.new(choices.map(([, label]) => label)),
        selected: choices.findIndex(([value]) => value === current),
    });
    row.connect('notify::selected', () => config.set(path, choices[row.selected][0]));
    return row;
}

function iconButton(icon, tooltip, sensitive, onClick) {
    const button = new Gtk.Button({icon_name: icon, tooltip_text: tooltip, valign: Gtk.Align.CENTER, sensitive,
        css_classes: ['flat']});
    button.connect('clicked', onClick);
    return button;
}

// ------------------------------------------------------------------ fans

function fanReadings(inventory) {
    return Object.fromEntries((inventory?.fanChips ?? []).flatMap(chip =>
        Object.entries(chip.fans).map(([channel, rpm]) => [`${chip.name}/${channel}`, rpm])));
}

function splitFanId(id) {
    const slash = id.lastIndexOf('/');
    return [id.slice(0, slash), id.slice(slash + 1)];
}

/**
 * The fans shown in the widget: any number, from any chip, with names. Speeds update live
 * so fans can be told apart (load the CPU and watch which one speeds up).
 */
function fansGroup(config, inventory, onInventory) {
    const group = new Adw.PreferencesGroup({
        title: 'Fans',
        description: inventory.fanChips.length
            ? 'Shown in the widget in this order. To tell fans apart, watch the live speeds: load the CPU ' +
              'and its cooler speeds up. On most boards fan1…fan7 follow the header order in the BIOS ' +
              '(CPU_FAN, CHA_FAN1, …). Channels at 0 RPM are usually empty headers.'
            : 'No board fan sensors found. Many boards need a kernel module first, e.g. ' +
              '"sudo modprobe nct6775" or "sudo modprobe it87".',
    });
    // Until the list is edited, start from what the widget shows now (older config keys).
    const list = (config.get('fans.list', null) ?? inventory.fanList ?? [])
        .map(fan => ({id: fan.id, name: fan.name || fan.id, warn: fan.warn ?? true}));
    let readings = fanReadings(inventory);
    const rows = [];
    let live = [];
    const save = () => config.set('fans.list', list.map(fan => ({...fan})));
    const speed = id => (readings[id] === undefined ? 'not found' : `${readings[id]} RPM`);
    const describe = id => `${splitFanId(id).join(' · ')} · ${speed(id)}`;

    const render = () => {
        rows.forEach(row => group.remove(row));
        rows.length = 0;
        live = [];
        list.forEach((fan, index) => {
            const row = new Adw.ExpanderRow({title: fan.name, subtitle: describe(fan.id)});
            const name = new Adw.EntryRow({title: 'Name in the widget', text: fan.name});
            name.connect('changed', () => {
                fan.name = name.text || fan.id;
                row.title = fan.name;
                save();
            });
            const warn = new Adw.SwitchRow({title: 'Warn when it stops',
                subtitle: 'Turn off for fans that stop on purpose at low load', active: fan.warn});
            warn.connect('notify::active', () => {
                fan.warn = warn.active;
                save();
            });
            const move = (to) => () => {
                [list[index], list[to]] = [list[to], list[index]];
                save();
                render();
            };
            const actions = new Adw.ActionRow({title: 'Order'});
            actions.add_suffix(iconButton('go-up-symbolic', 'Move up', index > 0, move(index - 1)));
            actions.add_suffix(iconButton('go-down-symbolic', 'Move down', index < list.length - 1, move(index + 1)));
            actions.add_suffix(iconButton('user-trash-symbolic', 'Remove from the widget', true, () => {
                list.splice(index, 1);
                save();
                render();
            }));
            row.add_row(name);
            row.add_row(warn);
            row.add_row(actions);
            group.add(row);
            rows.push(row);
            live.push(() => (row.subtitle = describe(fan.id)));
        });

        const unused = Object.keys(readings).filter(id => !list.some(fan => fan.id === id));
        if (!unused.length)
            return;
        const add = new Adw.ExpanderRow({title: 'Add a fan', subtitle: `${unused.length} more channels on this board`});
        for (const id of unused) {
            const [chip, channel] = splitFanId(id);
            const row = new Adw.ActionRow({title: `${chip} · ${channel}`, subtitle: speed(id)});
            row.add_suffix(iconButton('list-add-symbolic', 'Add to the widget', true, () => {
                list.push({id, name: `Fan ${channel.replace(/^fan/, '')}`, warn: true});
                save();
                render();
            }));
            add.add_row(row);
            live.push(() => (row.subtitle = speed(id)));
        }
        group.add(add);
        rows.push(add);
    };
    onInventory(next => {
        readings = fanReadings(next);
        live.forEach(update => update());
    });
    render();
    return group;
}

// ------------------------------------------------------------------ pages

function widgetPage(config) {
    const page = new Adw.PreferencesPage({name: 'widget', title: 'Widget', icon_name: 'preferences-desktop-display-symbolic'});

    const size = new Adw.PreferencesGroup({
        title: 'Size',
        description: 'Automatic picks a size for the screen resolution. Use a custom size for unusual screens.',
    });
    const sizes = [['auto', 'Automatic (by screen)'], ['1080p', '1080p'], ['2K', '2K'], ['4K', '4K'], ['custom', 'Custom']];
    const currentSize = config.get('widget.scale', 0) > 0 ? 'custom' : String(config.get('widget.profile', 'Auto'));
    const sizeRow = new Adw.ComboRow({
        title: 'Widget size',
        model: Gtk.StringList.new(sizes.map(([, label]) => label)),
        selected: Math.max(0, sizes.findIndex(([value]) => value.toLowerCase() === currentSize.toLowerCase())),
    });
    const scaleRow = Adw.SpinRow.new_with_range(50, 250, 5);
    scaleRow.set({title: 'Custom size', subtitle: 'Percent of the 1080p layout (430 px wide)',
        value: config.get('widget.scale', 0) || 100});
    const applySize = () => {
        const [value] = sizes[sizeRow.selected];
        scaleRow.sensitive = value === 'custom';
        if (value === 'custom') {
            config.set('widget.scale', Math.round(scaleRow.value));
        } else {
            config.set('widget.scale', 0);
            config.set('widget.profile', value === 'auto' ? 'Auto' : value);
        }
    };
    scaleRow.sensitive = currentSize === 'custom';
    sizeRow.connect('notify::selected', applySize);
    scaleRow.connect('notify::value', () => {
        if (scaleRow.sensitive)
            config.set('widget.scale', Math.round(scaleRow.value));
    });
    size.add(sizeRow);
    size.add(scaleRow);
    size.add(switchRow(config, 'widget.fitToScreen', 'Fit to screen height',
        'Shrink the widget when it is taller than the screen'));
    size.add(spinRow(config, 'widget.marginRight', 'Distance from the right edge', 'Pixels', 0, 2000, 1, 24));
    size.add(spinRow(config, 'widget.marginTop', 'Distance from the top bar', 'Pixels', 0, 2000, 1, 24));
    page.add(size);

    const sections = new Adw.PreferencesGroup({
        title: 'Sections',
        description: 'Hide what you don\'t need; the widget gets shorter, which helps on small screens.',
    });
    for (const [key, title, subtitle] of SECTIONS)
        sections.add(switchRow(config, `widget.show.${key}`, title, subtitle));
    page.add(sections);

    const panel = new Adw.PreferencesGroup({title: 'Desktop and top bar'});
    panel.add(switchRow(config, 'widget.visible', 'Show the widget'));
    panel.add(switchRow(config, 'widget.panelIcon', 'Icon in the top bar',
        'Menu with these settings, update check and restart. Without it, open settings from the Extensions app.'));
    page.add(panel);
    return page;
}

function hardwarePage(config, inventory, onInventory) {
    const page = new Adw.PreferencesPage({name: 'hardware', title: 'Hardware', icon_name: 'computer-symbolic'});
    if (!inventory) {
        const group = new Adw.PreferencesGroup({
            title: 'Hardware not listed yet',
            description: 'The bridge lists this PC\'s graphics cards, fans and drives. Start it with ' +
                '"systemctl --user start codex-monitor-bridge" and reopen this window.',
        });
        page.add(group);
        return page;
    }

    const gpu = new Adw.PreferencesGroup({title: 'Graphics card'});
    gpu.add(comboRow(config, 'gpu.device', 'Card shown in the widget',
        'Automatic prefers NVIDIA, then the AMD card with the most memory, then Intel',
        [['auto', 'Automatic'], ...inventory.gpus.map(g => [g.id, `${g.name} (${g.driver})`]), ['none', 'None']],
        'auto'));
    page.add(gpu);

    page.add(fansGroup(config, inventory, onInventory));

    const drives = new Adw.PreferencesGroup({
        title: 'Drives',
        description: `Up to ${MAX_DISKS} drives, in the order you turn them on.`,
    });
    const selected = [...config.get('disks', ['/'])];
    for (const mount of inventory.mounts) {
        const row = new Adw.ExpanderRow({
            title: mount.mount,
            subtitle: `${mount.device} · ${mount.fs} · ${formatBytes(mount.sizeB)}`,
            show_enable_switch: true,
            enable_expansion: selected.includes(mount.mount),
        });
        const name = new Adw.EntryRow({title: 'Name in the widget',
            text: config.get('widget.diskLabels', {})[mount.mount] ?? ''});
        name.connect('changed', () => {
            const labels = {...config.get('widget.diskLabels', {})};
            if (name.text)
                labels[mount.mount] = name.text;
            else
                delete labels[mount.mount];
            config.set('widget.diskLabels', labels);
        });
        row.add_row(name);
        row.connect('notify::enable-expansion', () => {
            const index = selected.indexOf(mount.mount);
            if (row.enable_expansion && index < 0) {
                if (selected.length >= MAX_DISKS) {
                    row.enable_expansion = false;
                    return;
                }
                selected.push(mount.mount);
            } else if (!row.enable_expansion && index >= 0) {
                selected.splice(index, 1);
            }
            config.set('disks', [...selected]);
        });
        drives.add(row);
    }
    page.add(drives);
    return page;
}

function networkPage(config, inventory) {
    const page = new Adw.PreferencesPage({name: 'network', title: 'Network', icon_name: 'network-wired-symbolic'});
    const speeds = new Adw.PreferencesGroup({
        title: 'Speeds',
        description: 'Your Internet plan marks the bars and turns amber when it is maxed out.',
    });
    speeds.add(spinRow(config, 'widget.internetDownMbps', 'Internet download', 'Mbps, 0 = not set', 0, 100000, 10));
    speeds.add(spinRow(config, 'widget.internetUpMbps', 'Internet upload', 'Mbps, 0 = not set', 0, 100000, 10));
    const link = inventory?.linkMbps ? ` (now ${Math.round(inventory.linkMbps)} Mbps)` : '';
    speeds.add(spinRow(config, 'widget.lanMbps', 'LAN full scale',
        `Mbps, 0 = the network card's link speed${link}`, 0, 100000, 100));
    page.add(speeds);

    const apps = new Adw.PreferencesGroup({title: 'Applications'});
    apps.add(spinRow(config, 'widget.topProcesses', 'Top processes rows',
        'Applications using the network now; 0 hides the list', 0, 5, 1, 3));
    page.add(apps);
    return page;
}

async function runUpdater(args) {
    const proc = Gio.Subprocess.new(['bash', UPDATER_PATH, ...args],
        Gio.SubprocessFlags.STDOUT_PIPE | Gio.SubprocessFlags.STDERR_MERGE);
    const [stdout] = await proc.communicate_utf8_async(null, null);
    return {ok: proc.get_successful(), output: stdout.trim()};
}

function updatesPage(config) {
    const page = new Adw.PreferencesPage({name: 'updates', title: 'Updates', icon_name: 'software-update-available-symbolic'});
    const group = new Adw.PreferencesGroup({
        title: 'Updates',
        description: 'Updates install in the background and the widget restarts by itself.',
    });
    const version = readText(GLib.build_filenamev([DATA_DIR, 'VERSION'])) ?? 'unknown';
    group.add(new Adw.ActionRow({title: 'Installed version', subtitle: version}));
    group.add(comboRow(config, 'update.mode', 'When a new version is out', '', [
        ['notify', 'Show a notification with an Update button'],
        ['auto', 'Install it automatically'],
        ['off', 'Don\'t check'],
    ], config.get('update.check', true) === false ? 'off' : 'notify'));

    const check = new Adw.ActionRow({title: 'Check for updates', subtitle: 'Ask GitHub now'});
    const checkButton = new Gtk.Button({label: 'Check now', valign: Gtk.Align.CENTER});
    const installButton = new Gtk.Button({label: 'Install', valign: Gtk.Align.CENTER, visible: false,
        css_classes: ['suggested-action']});
    check.add_suffix(installButton);
    check.add_suffix(checkButton);
    checkButton.connect('clicked', async () => {
        checkButton.sensitive = false;
        check.subtitle = 'Checking…';
        try {
            const result = JSON.parse((await runUpdater(['--check'])).output);
            installButton.visible = result.newer;
            installButton.tag = result.latest;
            check.subtitle = result.newer ? `${result.latest} is available` : `Up to date (latest release ${result.latest})`;
        } catch (e) {
            check.subtitle = 'GitHub is not reachable right now';
        }
        checkButton.sensitive = true;
    });
    installButton.connect('clicked', async () => {
        installButton.sensitive = checkButton.sensitive = false;
        check.subtitle = `Installing ${installButton.tag}…`;
        const result = await runUpdater([installButton.tag]);
        check.subtitle = result.ok
            ? `Installed ${installButton.tag}; the widget restarted with it`
            : `Update failed: ${result.output.split('\n').pop()}`;
        installButton.visible = !result.ok;
        installButton.sensitive = checkButton.sensitive = true;
    });
    group.add(check);
    page.add(group);
    return page;
}

function readText(path) {
    try {
        const [, bytes] = GLib.file_get_contents(path);
        return new TextDecoder().decode(bytes).trim();
    } catch (e) {
        return null;
    }
}

/**
 * Fills the extension preferences window.
 *
 * @param {Adw.PreferencesWindow} window the window GNOME opened for the extension
 * @param {object} _prefs the ExtensionPreferences object
 */
export function fillPreferencesWindow(window, _prefs) {
    const config = new Config();
    const runtime = config.get('bridge.outputFile', '')
        ? GLib.path_get_dirname(config.get('bridge.outputFile').replace(/^~/, GLib.get_home_dir()))
        : GLib.build_filenamev([GLib.get_user_runtime_dir(), 'codex-monitor']);
    const inventoryPath = GLib.build_filenamev([runtime, 'inventory.json']);
    const inventory = readJson(inventoryPath);
    // The bridge rewrites inventory.json every 2 s; pass it on for live fan speeds.
    const listeners = [];
    const onInventory = listener => listeners.push(listener);
    const timer = GLib.timeout_add_seconds(GLib.PRIORITY_DEFAULT, 2, () => {
        const next = readJson(inventoryPath);
        if (next)
            listeners.forEach(listener => listener(next));
        return GLib.SOURCE_CONTINUE;
    });

    window.set_default_size(680, 760);
    window.add(widgetPage(config));
    window.add(hardwarePage(config, inventory, onInventory));
    window.add(networkPage(config, inventory));
    window.add(updatesPage(config));
    window.connect('close-request', () => {
        GLib.source_remove(timer);
        config.flush();
        return false;
    });
}
