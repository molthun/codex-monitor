// CodexMonitor desktop widget for GNOME Shell, loaded by extension.js.
//
// Renders the metrics that codex-bridge.py writes to
// $XDG_RUNTIME_DIR/codex-monitor/sensors.json. The widget lives in the
// background group, so it sits on the desktop under all windows and is
// click-through, like the Rainmeter skin with AlwaysOnTop=-2. The panel
// icon menu opens the settings, checks for updates and restarts the widget.
//
// The loader imports a fresh copy of this module after every update, so a
// new version runs without logging out. Module-level state starts over then.

import GLib from 'gi://GLib';
import Gio from 'gi://Gio';
import St from 'gi://St';
import Clutter from 'gi://Clutter';
import Pango from 'gi://Pango';

import * as Main from 'resource:///org/gnome/shell/ui/main.js';
import * as MessageTray from 'resource:///org/gnome/shell/ui/messageTray.js';
import * as PanelMenu from 'resource:///org/gnome/shell/ui/panelMenu.js';
import * as PopupMenu from 'resource:///org/gnome/shell/ui/popupMenu.js';

// Oldest loader (extension.js) this module works with; see LOADER_API there.
export const LOADER_API_MIN = 1;

Gio._promisify(Gio.File.prototype, 'load_contents_async');
Gio._promisify(Gio.Subprocess.prototype, 'wait_check_async');
Gio._promisify(Gio.Subprocess.prototype, 'communicate_utf8_async');

const CONFIG_PATH = GLib.build_filenamev([GLib.get_user_config_dir(), 'codex-monitor', 'config.json']);
const DEFAULT_SENSORS_PATH = GLib.build_filenamev([GLib.get_user_runtime_dir(), 'codex-monitor', 'sensors.json']);
const UPDATER_PATH = GLib.build_filenamev([GLib.get_user_data_dir(), 'codex-monitor', 'update.sh']);
const UPDATE_LOG = GLib.build_filenamev([GLib.get_user_cache_dir(), 'codex-monitor', 'update.log']);
// Written by update.sh after an update, read by whichever widget version runs next.
const UPDATED_MARKER = GLib.build_filenamev([GLib.get_user_cache_dir(), 'codex-monitor', 'updated']);

// Outlives disable/enable (screen lock), so a dismissed update offer is not repeated this session.
let dismissedUpdateTag = null;
const STALE_SECONDS = 5;
const GRAPH_POINTS = 60;
const MAX_TOP_ROWS = 5;
const MAX_DISKS = 6;
// Internet share of the plan at which the link counts as maxed out.
const SATURATED = 0.9;
// Graph full-scale steps; the configured plan and link speeds are added to them.
const NICE_SCALES = [1, 2, 5, 10, 20, 50, 100, 200, 500, 1000, 2000, 2500, 5000, 10000, 25000, 40000, 100000];

// Palette and gradients copied from the Rainmeter skin [Variables].
const COLOR = {
    muted: 'rgba(154,166,178,0.92)',
    cpu: '#00e5ff', ram: '#45c997', gpu: '#9788ff', net: '#58b3ff', disk: '#74d684',
    ok: '#00e5ff', warm: '#ffc15e', hot: '#ff7171',
    wan: '#58b3ff', lan: '#1de9b6',
};
const RGB = {wan: [0x58, 0xb3, 0xff], lan: [0x1d, 0xe9, 0xb6]};
const GRADIENT = {
    cpu: ['#00e5ff', '#0091ea'],
    ram: ['#45c997', '#00e676'],
    gpu: ['#9788ff', '#e0c3fc'],
    wan: ['#2979ff', '#58b3ff'],
    lan: ['#00bfa5', '#1de9b6'],
    disk: ['#74d684', '#8bc34a'],
};

// 1080p skin geometry; larger profiles scale it (430 -> 540 / 720 px wide).
const BASE = {width: 430, pad: 18, barH: 7, barR: 3, rowGap: 2, barGap: 4, sectionGap: 11,
    title: 19, subtitle: 11, label: 13, section: 11, health: 12, icon: 14, graphH: 70, proc: 12};
const PROFILE_SCALE = {'1080p': 1, '2k': 540 / 430, '4k': 720 / 430};

const DEFAULT_WIDGET_CONFIG = {
    profile: 'Auto',
    autoProfileThresholds: {'2K': 1400, '4K': 2000},
    marginRight: 24,
    marginTop: 24,
    // Internet plan (bars mark it, the graph snaps to it); 0 = unknown.
    internetDownMbps: 0,
    internetUpMbps: 0,
    // Full scale of the Download/Upload bars; 0 = NIC link speed.
    lanMbps: 0,
    diskIOMaxMBs: 1000,
    fanMaxRpm: 2500,
    diskLabels: {},
    // Size: 0 = by profile; otherwise percent of the 1080p layout (430 px wide).
    scale: 0,
    // Shrink the widget when it is taller than the screen.
    fitToScreen: true,
    visible: true,
    panelIcon: true,
    topProcesses: 3,
    show: {health: true, performance: true, temperatures: true, cooling: true, network: true, diskIO: true, drives: true},
};

function level(value, warm, hot) {
    if (value === null || value === undefined)
        return 'muted';
    return value >= hot ? 'hot' : value >= warm ? 'warm' : 'ok';
}

function fmt(value, digits = 0, suffix = '') {
    if (value === null || value === undefined || Number.isNaN(value))
        return 'n/a';
    return `${value.toFixed(digits)}${suffix}`;
}

function fmtRate(mbps) {
    if (!(mbps > 0))
        return '0 Mbps';
    if (mbps < 1)
        return `${Math.round(mbps * 1000)} Kbps`;
    if (mbps < 1000)
        return `${mbps.toFixed(mbps < 10 ? 1 : 0)} Mbps`;
    return `${(mbps / 1000).toFixed(2)} Gbps`;
}

// Smallest step above the peak, capped at the link speed (the largest of `extra`).
function niceScale(peak, extra) {
    const limit = Math.max(...extra);
    const steps = [...NICE_SCALES, ...extra.filter(v => v > 0)]
        .filter(v => peak > limit || v <= limit)
        .sort((a, b) => a - b);
    return steps.find(v => v >= peak * 1.05) ?? steps[steps.length - 1];
}

function fmtBytes(bytes) {
    const units = ['B', 'KB', 'MB', 'GB', 'TB', 'PB'];
    let i = 0;
    while (bytes >= 1000 && i < units.length - 1) {
        bytes /= 1000;
        i++;
    }
    return `${bytes.toFixed(bytes < 10 && i > 0 ? 1 : 0)} ${units[i]}`;
}

class MonitorWidget {
    constructor(extensionPath, scale, widgetConfig, diskCount) {
        this._path = extensionPath;
        this._k = scale;
        this._cfg = widgetConfig;
        const show = widgetConfig.show;
        // Start with a flat baseline so the graph spans the full width at once.
        // [Internet down, LAN down, Internet up, LAN up] in Mbps.
        this._netHistory = Array.from({length: GRAPH_POINTS}, () => [0, 0, 0, 0]);
        this._graphMax = 10;

        const s = n => Math.round(n * scale);
        this._s = s;
        this._innerW = s(BASE.width) - 2 * s(BASE.pad);

        this.actor = new St.BoxLayout({
            style_class: 'codex-monitor',
            orientation: Clutter.Orientation.VERTICAL,
            style: `width: ${s(BASE.width)}px; padding: ${s(14)}px ${s(BASE.pad)}px ${s(BASE.pad)}px; border-radius: ${s(12)}px;`,
        });

        // Hidden sections are not built at all, so the widget gets shorter.
        this._buildHeader();
        if (show.health)
            this._buildHealth();

        if (show.performance) {
            this._section('cpu.png', 'PERFORMANCE');
            this._cpuLoad = this._row('CPU load');
            this._ram = this._row('RAM used');
            this._gpuLoad = this._row('GPU load');
            this._vram = this._row('VRAM used');
        }

        if (show.temperatures) {
            this._section('temp.png', 'TEMPERATURES');
            this._cpuTemp = this._row('CPU temp');
            this._gpuTemp = this._row('GPU temp');
        }

        if (show.cooling) {
            this._section('fan.png', 'COOLING');
            this._cpuFan = this._row('CPU cooler');
            this._caseFan = this._row('Case fan');
            this._psuFan = this._row('PSU fan');
            this._gpuFan = this._row('GPU fans');
        }

        if (show.network) {
            this._netTitle = this._section('net.png', 'NETWORK TRAFFIC', true);
            this._buildGraph();
            this._netDown = this._splitRow('Download');
            this._netUp = this._splitRow('Upload');
            this._buildTopProcesses(Math.min(widgetConfig.topProcesses, MAX_TOP_ROWS));
            this._buildNetFooter();
        }

        const disks = Array.from({length: Math.min(diskCount, MAX_DISKS)});
        this._diskIO = [];
        this._diskUsed = [];
        if (show.diskIO && disks.length) {
            this._section('disk.png', 'DISK I/O');
            this._diskIO = disks.map(() => this._row(''));
        }
        if (show.drives && disks.length) {
            this._section('disk.png', 'DRIVES USED');
            this._diskUsed = disks.map(() => this._row(''));
        }
    }

    destroy() {
        this.actor.destroy();
    }

    // ------------------------------------------------------------ building

    _label(text, sizePx, {muted = false, style = '', expand = false, align = Clutter.ActorAlign.START} = {}) {
        const label = new St.Label({
            text,
            style_class: muted ? 'codex-muted' : null,
            style: `font-size: ${this._s(sizePx)}px; ${style}`,
            x_expand: expand,
            x_align: align,
            y_align: Clutter.ActorAlign.CENTER,
        });
        label.clutter_text.ellipsize = Pango.EllipsizeMode.NONE;
        return label;
    }

    _rule(parent, marginTop, marginBottom) {
        const rule = new St.Widget({
            style_class: 'codex-rule',
            style: `height: 1px; margin-top: ${this._s(marginTop)}px; margin-bottom: ${this._s(marginBottom)}px;`,
            x_expand: true,
            y_align: Clutter.ActorAlign.CENTER,
        });
        parent.add_child(rule);
        return rule;
    }

    _buildHeader() {
        this.actor.add_child(this._label('System Monitor', BASE.title, {style: 'font-weight: 600;'}));
        this._subtitle = this._label('Hardware bridge / desktop widget', BASE.subtitle, {muted: true});
        this.actor.add_child(this._subtitle);
        this._rule(this.actor, 6, 10);
    }

    _buildHealth() {
        const s = this._s;
        const box = new St.BoxLayout({
            style_class: 'codex-health',
            style: `padding: ${s(7)}px ${s(10)}px ${s(9)}px; border-radius: ${s(BASE.barR)}px; spacing: ${s(10)}px;`,
            x_expand: true,
        });
        this._health = {};
        for (const key of ['cpu', 'gpu', 'ram', 'fans']) {
            const cell = new St.BoxLayout({orientation: Clutter.Orientation.VERTICAL, x_expand: true,
                style: `spacing: ${s(6)}px;`});
            const text = this._label('', BASE.health, {style: 'font-weight: 600;'});
            const bar = new St.Widget({x_expand: true, style: `height: ${s(3)}px; border-radius: ${s(2)}px;`});
            cell.add_child(text);
            cell.add_child(bar);
            box.add_child(cell);
            this._health[key] = {text, bar};
        }
        this.actor.add_child(box);
    }

    _section(icon, title, withRightLabel = false) {
        const s = this._s;
        const row = new St.BoxLayout({x_expand: true,
            style: `spacing: ${s(6)}px; margin-top: ${s(BASE.sectionGap)}px; margin-bottom: ${s(6)}px;`});
        row.add_child(new St.Icon({
            gicon: Gio.FileIcon.new(Gio.File.new_for_path(`${this._path}/icons/${icon}`)),
            icon_size: s(BASE.icon),
            y_align: Clutter.ActorAlign.CENTER,
        }));
        row.add_child(this._label(title, BASE.section, {muted: true, style: 'letter-spacing: 1px; font-weight: 600;'}));
        this._rule(row, 0, 0);
        let right = null;
        if (withRightLabel) {
            right = this._label('', BASE.section, {muted: true});
            row.add_child(right);
        }
        this.actor.add_child(row);
        return right;
    }

    _bar() {
        const s = this._s;
        const track = new St.Widget({
            style_class: 'codex-track',
            style: `width: ${this._innerW}px; height: ${s(BASE.barH)}px; border-radius: ${s(BASE.barR)}px;`,
        });
        const fill = new St.Widget({style: `height: ${s(BASE.barH)}px;`});
        track.add_child(fill);
        return {track, fill};
    }

    _row(title, marker = null) {
        const s = this._s;
        const line = new St.BoxLayout({x_expand: true, style: `margin-top: ${s(BASE.rowGap)}px; spacing: ${s(6)}px;`});
        if (marker) {
            line.add_child(new St.Widget({
                style: `width: ${s(6)}px; height: ${s(6)}px; border-radius: 1px; background-color: ${marker};`,
                y_align: Clutter.ActorAlign.CENTER,
            }));
        }
        const label = this._label(title, BASE.label, {muted: true, expand: true});
        const value = this._label('', BASE.label, {align: Clutter.ActorAlign.END});
        line.add_child(label);
        line.add_child(value);
        this.actor.add_child(line);

        const bar = this._bar();
        bar.track.style += ` margin-top: ${s(BASE.barGap)}px;`;
        this.actor.add_child(bar.track);
        return {label, value, bar};
    }

    _buildGraph() {
        const s = this._s;
        this._graph = new St.DrawingArea({
            style_class: 'codex-graph',
            style: `width: ${this._innerW}px; height: ${s(BASE.graphH)}px; border-radius: 2px;`,
        });
        this._graph.connect('repaint', area => this._paintGraph(area));
        this.actor.add_child(this._graph);

        const legend = new St.BoxLayout({x_expand: true, style: `margin-top: ${s(4)}px; spacing: ${s(6)}px;`});
        for (const [color, text] of [[COLOR.wan, 'Internet'], [COLOR.lan, 'LAN']]) {
            legend.add_child(new St.Widget({
                style: `width: ${s(10)}px; height: ${s(6)}px; border-radius: 1px; background-color: ${color};`,
                y_align: Clutter.ActorAlign.CENTER,
            }));
            legend.add_child(this._label(text, BASE.subtitle, {muted: true, style: `margin-right: ${s(8)}px;`}));
        }
        legend.add_child(this._label('↓ above · ↑ below', BASE.subtitle, {muted: true}));
        this._scaleLabel = this._label('', BASE.subtitle, {muted: true, expand: true, align: Clutter.ActorAlign.END});
        legend.add_child(this._scaleLabel);
        this.actor.add_child(legend);
    }

    // Mirrored graph: download grows up from the middle, upload grows down.
    // Each side stacks Internet (bottom layer) and LAN on top of it.
    _paintGraph(area) {
        const cr = area.get_context();
        const [w, h] = area.get_surface_size();
        const history = this._netHistory;
        const max = this._graphMax;
        const mid = Math.round(h / 2) + 0.5;
        const half = h / 2 - 2;
        const step = w / (GRAPH_POINTS - 1);
        const x0 = w - (history.length - 1) * step;
        const x = i => x0 + i * step;
        const sides = [
            {y: v => mid - Math.min(v / max, 1) * half, wan: p => p[0], total: p => p[0] + p[1], cap: this._capDown},
            {y: v => mid + Math.min(v / max, 1) * half, wan: p => p[2], total: p => p[2] + p[3], cap: this._capUp},
        ];
        const rgba = (rgb, a) => cr.setSourceRGBA(rgb[0] / 255, rgb[1] / 255, rgb[2] / 255, a);
        const path = (side, value) => history.forEach((p, i) => (i ? cr.lineTo : cr.moveTo).call(cr, x(i), side.y(value(p))));

        cr.setLineWidth(1);
        cr.setSourceRGBA(1, 1, 1, 0.12);
        cr.moveTo(0, mid);
        cr.lineTo(w, mid);
        cr.stroke();

        for (const side of sides) {
            // Internet plan ceiling, once the scale grows past it (LAN traffic).
            if (side.cap > 0 && side.cap < max) {
                const yc = Math.round(side.y(side.cap)) + 0.5;
                cr.setDash([3 * this._k, 3 * this._k], 0);
                cr.setSourceRGBA(1, 1, 1, 0.28);
                cr.moveTo(0, yc);
                cr.lineTo(w, yc);
                cr.stroke();
                cr.setDash([], 0);
            }
            for (const [lower, upper, rgb] of [[() => 0, side.wan, RGB.wan], [side.wan, side.total, RGB.lan]]) {
                path(side, upper);
                for (let i = history.length - 1; i >= 0; i--)
                    cr.lineTo(x(i), side.y(lower(history[i])));
                cr.closePath();
                rgba(rgb, 0.3);
                cr.fill();
            }
            // LAN edge first, so the Internet edge wins where the LAN layer is empty.
            cr.setLineWidth(Math.max(1.2, 1.4 * this._k));
            for (const [value, rgb] of [[side.total, RGB.lan], [side.wan, RGB.wan]]) {
                path(side, value);
                rgba(rgb, 0.95);
                cr.stroke();
            }
        }
        cr.$dispose();
    }

    // One bar per direction: an Internet segment followed by a LAN segment, scaled to the link speed,
    // with a tick at the Internet plan speed.
    _splitRow(title) {
        const s = this._s;
        const line = new St.BoxLayout({x_expand: true, style: `margin-top: ${s(BASE.rowGap)}px; spacing: ${s(6)}px;`});
        const label = this._label(title, BASE.label, {muted: true, expand: true});
        // Separate labels: St recolors the first span of Pango markup.
        const wanValue = this._label('', BASE.label, {align: Clutter.ActorAlign.END});
        const lanValue = this._label('', BASE.label, {align: Clutter.ActorAlign.END});
        line.add_child(label);
        line.add_child(wanValue);
        line.add_child(lanValue);
        this.actor.add_child(line);

        const track = new St.Widget({
            style_class: 'codex-track',
            style: `width: ${this._innerW}px; height: ${s(BASE.barH)}px; border-radius: ${s(BASE.barR)}px; margin-top: ${s(BASE.barGap)}px;`,
        });
        const wan = new St.Widget();
        const lan = new St.Widget();
        const cap = new St.Widget();
        for (const child of [wan, lan, cap])
            track.add_child(child);
        this.actor.add_child(track);
        return {label, wanValue, lanValue, wan, lan, cap};
    }

    _buildTopProcesses(count) {
        this._procRows = [];
        if (count <= 0)
            return;
        const s = this._s;
        this.actor.add_child(this._label('Top processes', BASE.subtitle, {muted: true,
            style: `margin-top: ${s(8)}px; margin-bottom: ${s(2)}px;`}));
        for (let i = 0; i < count; i++) {
            const line = new St.BoxLayout({x_expand: true, style: `spacing: ${s(6)}px;`});
            const dot = new St.Widget({y_align: Clutter.ActorAlign.CENTER});
            const icon = new St.Icon({icon_size: s(BASE.icon), fallback_icon_name: 'application-x-executable-symbolic',
                y_align: Clutter.ActorAlign.CENTER});
            const name = this._label('', BASE.proc, {expand: true});
            name.clutter_text.ellipsize = Pango.EllipsizeMode.END;
            const value = this._label('', BASE.proc, {align: Clutter.ActorAlign.END});
            line.add_child(dot);
            line.add_child(icon);
            line.add_child(name);
            line.add_child(value);
            this.actor.add_child(line);
            this._procRows.push({dot, icon, name, value});
        }
    }

    _buildNetFooter() {
        const s = this._s;
        const row = new St.BoxLayout({x_expand: true, style: `margin-top: ${s(8)}px;`});
        this._ethFooter = this._label('', BASE.subtitle, {muted: true, expand: true});
        this._wifiFooter = this._label('', BASE.subtitle, {muted: true, align: Clutter.ActorAlign.END});
        row.add_child(this._ethFooter);
        row.add_child(this._wifiFooter);
        this.actor.add_child(row);
    }

    // ------------------------------------------------------------ updating

    _setBar(bar, fraction, colors) {
        const frac = Math.max(0, Math.min(1, fraction || 0));
        const width = Math.round(this._innerW * frac);
        const [start, end] = Array.isArray(colors) ? colors : [colors, colors];
        bar.fill.set_style(
            `width: ${width}px; height: ${this._s(BASE.barH)}px; border-radius: ${this._s(BASE.barR)}px;` +
            `background-gradient-direction: horizontal; background-gradient-start: ${start}; background-gradient-end: ${end};`);
    }

    _setRow(row, value, fraction, colors, state = 'ok') {
        if (!row)
            return;
        row.value.text = value;
        const alert = state === 'warm' || state === 'hot';
        row.label.set_style(`font-size: ${this._s(BASE.label)}px;${alert ? ` color: ${COLOR[state]};` : ''}`);
        this._setBar(row.bar, fraction, alert ? COLOR[state] : colors);
    }

    _setSplitRow(row, wan, lan, scale, cap, split) {
        const s = this._s;
        const h = s(BASE.barH);
        const r = s(BASE.barR);
        const width = this._innerW;
        const wanW = Math.round(width * Math.max(0, Math.min(1, wan / scale)));
        const lanW = Math.min(Math.round(width * Math.max(0, Math.min(1, lan / scale))), width - wanW);
        const segment = (actor, x, w, [start, end], roundLeft, roundRight) => {
            actor.visible = w > 0;
            actor.set_position(x, 0);
            const [left, right] = [roundLeft ? r : 0, roundRight ? r : 0];
            actor.set_style(`width: ${w}px; height: ${h}px; border-radius: ${left}px ${right}px ${right}px ${left}px;` +
                `background-gradient-direction: horizontal; background-gradient-start: ${start}; background-gradient-end: ${end};`);
        };
        segment(row.wan, 0, wanW, GRADIENT.wan, true, lanW === 0);
        segment(row.lan, wanW, lanW, GRADIENT.lan, wanW === 0, true);

        const maxed = split && cap > 0 && wan >= cap * SATURATED;
        row.cap.visible = cap > 0 && cap < scale;
        if (row.cap.visible) {
            const tickW = Math.max(1, s(2));
            row.cap.set_position(Math.round(width * cap / scale - tickW / 2), -s(2));
            row.cap.set_style(`width: ${tickW}px; height: ${h + 2 * s(2)}px;` +
                `background-color: ${maxed ? COLOR.warm : 'rgba(255,255,255,0.55)'};`);
        }

        row.label.set_style(`font-size: ${s(BASE.label)}px;${maxed ? ` color: ${COLOR.warm};` : ''}`);
        row.wanValue.text = split ? `Internet ${fmtRate(wan)}` : fmtRate(wan);
        row.wanValue.set_style(`font-size: ${s(BASE.label)}px;` +
            `${split ? ` color: ${COLOR.wan};` : ''}${maxed ? ' font-weight: bold;' : ''}`);
        row.lanValue.visible = split;
        row.lanValue.text = `LAN ${fmtRate(lan)}`;
        row.lanValue.set_style(`font-size: ${s(BASE.label)}px; color: ${COLOR.lan};`);
    }

    // Icon from the bridge: a theme icon name or an absolute file path.
    _appIcon(icon) {
        this._icons ??= new Map();
        icon ||= 'application-x-executable-symbolic';
        if (!this._icons.has(icon)) {
            this._icons.set(icon, icon.startsWith('/')
                ? Gio.FileIcon.new(Gio.File.new_for_path(icon)) : Gio.ThemedIcon.new(icon));
        }
        return this._icons.get(icon);
    }

    _setTopProcesses(d, total) {
        const entries = (d.NetTopProcesses || []).map(p => ({
            name: p.name,
            icon: p.icon,
            down: p.wanDown + p.lanDown,
            up: p.wanUp + p.lanUp,
            color: p.lanDown + p.lanUp > p.wanDown + p.wanUp ? COLOR.lan : COLOR.wan,
        }));
        // UDP (QUIC, games) has no per-process counters; show it when it is a real share of the traffic.
        const otherDown = d.NetOtherDownMbps ?? 0;
        const otherUp = d.NetOtherUpMbps ?? 0;
        if (otherDown + otherUp >= 1 && otherDown + otherUp > 0.2 * total)
            entries.push({name: 'UDP / other', icon: 'network-transmit-receive-symbolic', down: otherDown, up: otherUp, color: COLOR.muted});
        entries.sort((a, b) => b.down + b.up - (a.down + a.up));

        const s = this._s;
        this._procRows.forEach((row, i) => {
            const e = entries[i];
            row.dot.set_style(`width: ${s(6)}px; height: ${s(6)}px; border-radius: ${s(3)}px;` +
                `background-color: ${e ? e.color : 'transparent'};`);
            row.icon.gicon = this._appIcon(e?.icon);
            row.icon.opacity = e ? 255 : 0;
            row.name.text = e ? e.name : i === 0 ? 'No active transfers' : ' ';
            row.name.set_style(`font-size: ${s(BASE.proc)}px;${e ? '' : ` color: ${COLOR.muted};`}`);
            row.value.text = e ? `↓ ${fmtRate(e.down)}  ↑ ${fmtRate(e.up)}` : '';
        });
    }

    _setHealth(key, text, state, okColor = COLOR.ok) {
        if (!this._health)
            return;
        const color = state === 'ok' ? okColor : COLOR[state];
        const cell = this._health[key];
        cell.text.text = text;
        cell.text.set_style(`font-size: ${this._s(BASE.health)}px; font-weight: 600; color: ${color};`);
        cell.bar.set_style(`height: ${this._s(3)}px; border-radius: ${this._s(2)}px; background-color: ${color};`);
    }

    update(d) {
        const cfg = this._cfg;
        const stale = !d || (Date.now() / 1000 - (d.Timestamp || 0)) > STALE_SECONDS;
        const update = !stale && d.UpdateAvailable;
        this._subtitle.text = stale ? 'Bridge offline: codex-monitor-bridge.service'
            : update ? `Update ${d.UpdateAvailable} available: see notifications` : 'Hardware bridge / desktop widget';
        this._subtitle.set_style(`font-size: ${this._s(BASE.subtitle)}px;` +
            `${stale ? ` color: ${COLOR.hot};` : update ? ` color: ${COLOR.ok};` : ''}`);
        d = d || {};

        // Health strip — thresholds from the skin's IfCondition blocks.
        const cpuState = level(d.CPU, 65, 80);
        const gpuState = level(d.GPUCore, 70, 83);
        const ramState = level(d.RAMPct, 85, 95);
        const word = {ok: 'OK', warm: 'WARM', hot: 'HOT', muted: 'N/A'};
        this._setHealth('cpu', `CPU ${word[cpuState]} ${fmt(d.CPU, 0, '°C')}`, cpuState);
        this._setHealth('gpu', `GPU ${word[gpuState]} ${fmt(d.GPUCore, 0, '°C')}`, gpuState, COLOR.gpu);
        this._setHealth('ram', `RAM ${ramState === 'ok' ? 'OK' : ramState === 'warm' ? 'HIGH' : 'CRIT'} ${fmt(d.RAMPct, 0, '%')}`, ramState);
        if (d.FansAvailable) {
            const low = (d.CPUFan ?? 0) < 500 || (d.CaseFan ?? 300) < 300 || (d.PSUFan ?? 300) < 300 ||
                ((d.GPUCore ?? 0) >= 60 && (d.GPUFanPct ?? 0) <= 0);
            this._setHealth('fans', low ? 'FANS LOW' : 'FANS OK', low ? 'hot' : 'ok');
        } else {
            this._setHealth('fans', 'FANS N/A', 'muted');
        }

        // Performance
        this._setRow(this._cpuLoad, fmt(d.CPULoad, 0, '%'), (d.CPULoad ?? 0) / 100, GRADIENT.cpu);
        this._setRow(this._ram, `${fmt(d.RAMPct, 0, '%')}  ${d.RAMUsedB ? `${fmtBytes(d.RAMUsedB)} / ${fmtBytes(d.RAMTotalB)}` : ''}`,
            (d.RAMPct ?? 0) / 100, GRADIENT.ram, ramState);
        this._setRow(this._gpuLoad, fmt(d.GPULoad, 0, '%'), (d.GPULoad ?? 0) / 100, GRADIENT.gpu);
        this._setRow(this._vram,
            d.VRAMTotalMB ? `${(d.VRAMUsedMB / 1024).toFixed(1)} GB / ${(d.VRAMTotalMB / 1024).toFixed(1)} GB` : 'n/a',
            (d.VRAMPct ?? 0) / 100, GRADIENT.gpu);

        // Temperatures
        this._setRow(this._cpuTemp, fmt(d.CPU, 0, '°C'), (d.CPU ?? 0) / 100, GRADIENT.cpu, cpuState);
        this._setRow(this._gpuTemp, fmt(d.GPUCore, 0, '°C'), (d.GPUCore ?? 0) / 100, GRADIENT.cpu, gpuState);

        // Cooling
        const rpm = v => (v === null || v === undefined ? 'n/a' : `${Math.round(v)} RPM`);
        const maxRpm = cfg.fanMaxRpm;
        this._setRow(this._cpuFan, rpm(d.CPUFan), (d.CPUFan ?? 0) / maxRpm, GRADIENT.cpu);
        this._setRow(this._caseFan, rpm(d.CaseFan), (d.CaseFan ?? 0) / maxRpm, GRADIENT.cpu);
        this._setRow(this._psuFan, rpm(d.PSUFan), (d.PSUFan ?? 0) / maxRpm, GRADIENT.cpu);
        this._setRow(this._gpuFan, fmt(d.GPUFanPct, 0, '%'), (d.GPUFanPct ?? 0) / 100, GRADIENT.gpu);

        if (this._graph)
            this._updateNetwork(d, cfg);

        // Disks
        const disks = d.Disks || [];
        const rows = Math.max(this._diskIO.length, this._diskUsed.length);
        for (let i = 0; i < rows; i++) {
            const disk = disks[i];
            const io = this._diskIO[i];
            const used = this._diskUsed[i];
            for (const row of [io, used].filter(r => r)) {
                row.label.get_parent().visible = !!disk;
                row.bar.track.visible = !!disk;
            }
            if (!disk)
                continue;
            const name = cfg.diskLabels[disk.mount] || disk.label;
            if (io) {
                io.label.text = name;
                this._setRow(io, `R ${disk.readMBs.toFixed(1)} / W ${disk.writeMBs.toFixed(1)} MB/s`,
                    (disk.readMBs + disk.writeMBs) / cfg.diskIOMaxMBs, GRADIENT.disk);
            }
            if (used) {
                const pct = disk.totalB ? disk.usedB / disk.totalB * 100 : 0;
                used.label.text = `${name} ${pct.toFixed(0)}%`;
                this._setRow(used, `${fmtBytes(disk.usedB)} / ${fmtBytes(disk.totalB)}`, pct / 100, GRADIENT.disk,
                    level(pct, 85, 95));
            }
        }
    }

    _updateNetwork(d, cfg) {
        const mode = d.NetSplitMode || 'none';
        const down = d.NetDownMbps ?? 0;
        const up = d.NetUpMbps ?? 0;
        const point = mode === 'none'
            ? [down, 0, up, 0]
            : [d.NetWanDownMbps ?? 0, d.NetLanDownMbps ?? 0, d.NetWanUpMbps ?? 0, d.NetLanUpMbps ?? 0];
        const link = cfg.lanMbps || d.NetLinkMbps || 1000;
        this._capDown = cfg.internetDownMbps;
        this._capUp = cfg.internetUpMbps;
        this._netHistory.push(point);
        if (this._netHistory.length > GRAPH_POINTS)
            this._netHistory.shift();
        const peak = Math.max(...this._netHistory.map(p => Math.max(p[0] + p[1], p[2] + p[3])));
        this._graphMax = niceScale(peak, [this._capDown, this._capUp, link]);
        this._graph.queue_repaint();
        this._scaleLabel.text = `${fmtRate(this._graphMax)}${mode === 'estimate' ? ' · est.' : ''}`;
        this._netTitle.text = `↓ ${fmtRate(down)}  ↑ ${fmtRate(up)}`;
        this._setSplitRow(this._netDown, point[0], point[1], link, this._capDown, mode !== 'none');
        this._setSplitRow(this._netUp, point[2], point[3], link, this._capUp, mode !== 'none');
        this._setTopProcesses(d, down + up);
        this._ethFooter.text = `ETH DL/UL ${fmt(d.NetEthInMbps, 1)}/${fmt(d.NetEthOutMbps, 1)} Mbps`;
        const wifiMode = d.NetWifiActiveMode || 'Off';
        this._wifiFooter.text = wifiMode === 'Off' ? 'Wi-Fi off'
            : `${wifiMode === 'AP' ? 'AP' : 'Wi-Fi'} DL/UL ${fmt(d.NetWifiActiveDlMbps, 1)}/${fmt(d.NetWifiActiveUlMbps, 1)} Mbps`;
    }
}

function readConfig() {
    try {
        const [, bytes] = GLib.file_get_contents(CONFIG_PATH);
        return JSON.parse(new TextDecoder().decode(bytes));
    } catch (e) {
        return {};
    }
}

// Changes one widget option in config.json; the config monitor applies it.
function writeWidgetOption(key, value) {
    const json = readConfig();
    json.widget = {...json.widget, [key]: value};
    GLib.mkdir_with_parents(GLib.path_get_dirname(CONFIG_PATH), 0o755);
    GLib.file_set_contents(CONFIG_PATH, `${JSON.stringify(json, null, 2)}\n`);
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
 * Everything the extension does while enabled. Created by the loader (extension.js)
 * from whichever copy of this module is current.
 *
 * @param {object} extension the Extension object (path, metadata, openPreferences)
 * @param {{api: number, reload: Function}} loader reload() restarts the widget with the newest code
 * @returns {Controller}
 */
export function createController(extension, loader) {
    return new Controller(extension, loader);
}

class Controller {
    constructor(extension, loader) {
        this._ext = extension;
        this._loader = loader;
    }

    get path() {
        return this._ext.path;
    }

    enable() {
        this._enabled = true;
        this._cancellable = new Gio.Cancellable();
        this._loadConfig();
        this._rebuild();
        this._buildIndicator();

        this._monitorsChangedId = Main.layoutManager.connect('monitors-changed', () => this._rebuild());

        this._configMonitor = Gio.File.new_for_path(CONFIG_PATH).monitor_file(Gio.FileMonitorFlags.NONE, null);
        this._configMonitor.connect('changed', (_m, _f, _o, event) => {
            if (event === Gio.FileMonitorEvent.CHANGES_DONE_HINT || event === Gio.FileMonitorEvent.CREATED) {
                this._loadConfig();
                this._rebuild();
                this._indicator?.destroy();
                this._buildIndicator();
            }
        });

        this._timerId = GLib.timeout_add(GLib.PRIORITY_DEFAULT, 1000, () => {
            this._refresh().catch(logError);
            return GLib.SOURCE_CONTINUE;
        });
        this._refresh().catch(logError);
    }

    disable() {
        this._enabled = false;
        if (this._timerId) {
            GLib.source_remove(this._timerId);
            this._timerId = 0;
        }
        this._cancellable?.cancel();
        this._cancellable = null;
        this._configMonitor?.cancel();
        this._configMonitor = null;
        if (this._monitorsChangedId) {
            Main.layoutManager.disconnect(this._monitorsChangedId);
            this._monitorsChangedId = 0;
        }
        this._indicator?.destroy();
        this._indicator = null;
        this._widget?.destroy();
        this._widget = null;
        this._source?.destroy();
        this._source = null;
        this._offeredUpdateTag = null;
    }

    // The running loader is older than this module needs: the widget keeps working,
    // but logging out once is required to finish the update.
    loaderOutdated() {
        this._notify('CodexMonitor needs a log out', 'Log out and back in once to finish the update.');
    }

    _loadConfig() {
        const json = readConfig();
        this._widgetConfig = {...DEFAULT_WIDGET_CONFIG, ...(json.widget || {})};
        this._widgetConfig.show = {...DEFAULT_WIDGET_CONFIG.show, ...(json.widget?.show || {})};
        // Older configs only had netMaxMbps.
        if (json.widget?.lanMbps === undefined && json.widget?.netMaxMbps)
            this._widgetConfig.lanMbps = json.widget.netMaxMbps;
        this._diskCount = Array.isArray(json.disks) ? json.disks.length : 1;
        this._sensorsPath = json.bridge?.outputFile
            ? json.bridge.outputFile.replace(/^~/, GLib.get_home_dir())
            : DEFAULT_SENSORS_PATH;
    }

    _scaleForMonitor(monitor) {
        const cfg = this._widgetConfig;
        if (cfg.scale > 0)
            return cfg.scale / 100;
        const profile = String(cfg.profile).toLowerCase();
        if (profile in PROFILE_SCALE)
            return PROFILE_SCALE[profile];
        // Auto: pick by the physical height, like Switch-WidgetSize.ps1.
        const thresholds = {'2K': 1400, '4K': 2000, ...cfg.autoProfileThresholds};
        const physicalHeight = monitor.height * (monitor.geometry_scale || 1);
        if (physicalHeight >= thresholds['4K'])
            return PROFILE_SCALE['4k'];
        return physicalHeight >= thresholds['2K'] ? PROFILE_SCALE['2k'] : PROFILE_SCALE['1080p'];
    }

    _rebuild() {
        const monitor = Main.layoutManager.primaryMonitor;
        if (!monitor)
            return;
        const cfg = this._widgetConfig;
        const top = monitor.y + Main.panel.height + cfg.marginTop;
        const available = monitor.y + monitor.height - top - cfg.marginTop;

        let scale = this._scaleForMonitor(monitor);
        this._createWidget(scale);
        // Shrink to fit short screens (e.g. a laptop, or 1080p with a tall panel or dock).
        const [, natHeight] = this._widget.actor.get_preferred_height(-1);
        if (cfg.fitToScreen && natHeight > available) {
            scale *= available / natHeight;
            this._createWidget(scale);
        }

        const [, natWidth] = this._widget.actor.get_preferred_width(-1);
        this._widget.actor.set_position(monitor.x + monitor.width - natWidth - cfg.marginRight, top);
        this._widget.actor.visible = cfg.visible;
    }

    _createWidget(scale) {
        this._widget?.destroy();
        this._widget = new MonitorWidget(this.path, scale, this._widgetConfig, this._diskCount);
        Main.layoutManager._backgroundGroup.add_child(this._widget.actor);
        this._widget.update(this._lastData);
    }

    async _refresh() {
        let data = null;
        try {
            const [bytes] = await Gio.File.new_for_path(this._sensorsPath).load_contents_async(this._cancellable);
            data = JSON.parse(new TextDecoder().decode(bytes));
        } catch (e) {
            if (e.matches?.(Gio.IOErrorEnum, Gio.IOErrorEnum.CANCELLED))
                return;
            // Missing/partial file: render as offline.
        }
        if (!this._enabled)
            return;
        this._lastData = data;
        this._widget?.update(data);
        this._updateIndicator(data);
        this._announceUpdate();
        this._offerUpdate(data);
    }

    // ------------------------------------------------------------ panel menu

    _buildIndicator() {
        if (!this._widgetConfig.panelIcon) {
            this._indicator = null;
            return;
        }
        const indicator = new PanelMenu.Button(0.0, 'CodexMonitor', false);
        indicator.add_child(new St.Icon({
            gicon: Gio.FileIcon.new(Gio.File.new_for_path(`${this.path}/icons/app_icon_colored.png`)),
            style_class: 'system-status-icon',
        }));

        const menu = indicator.menu;
        this._versionItem = new PopupMenu.PopupMenuItem('CodexMonitor', {reactive: false});
        menu.addMenuItem(this._versionItem);
        this._installItem = new PopupMenu.PopupMenuItem('');
        this._installItem.connect('activate', () => this._runUpdate(this._installItem.tag).catch(logError));
        this._installItem.visible = false;
        menu.addMenuItem(this._installItem);
        menu.addMenuItem(new PopupMenu.PopupSeparatorMenuItem());

        const showItem = new PopupMenu.PopupSwitchMenuItem('Show widget', this._widgetConfig.visible);
        showItem.connect('toggled', (_item, state) => writeWidgetOption('visible', state));
        menu.addMenuItem(showItem);
        menu.addAction('Settings', () => this._ext.openPreferences());
        menu.addAction('Check for updates', () => this._checkForUpdates().catch(logError));
        menu.addAction('Restart widget', () => this._loader.reload());

        Main.panel.addToStatusArea('codex-monitor', indicator);
        this._indicator = indicator;
        this._updateIndicator(this._lastData);
    }

    _updateIndicator(data) {
        if (!this._indicator)
            return;
        this._versionItem.label.text = `CodexMonitor ${data?.Version ?? ''}`.trim();
        const tag = data?.UpdateAvailable;
        this._installItem.visible = !!tag && !this._updating;
        this._installItem.tag = tag;
        this._installItem.label.text = tag ? `Install update ${tag}` : '';
    }

    // ------------------------------------------------------------ updates

    _notify(title, body, actions = []) {
        if (!this._enabled)
            return null;
        if (!this._source) {
            this._source = new MessageTray.Source({
                title: 'CodexMonitor',
                icon: Gio.FileIcon.new(Gio.File.new_for_path(`${this.path}/icons/app_icon_colored.png`)),
            });
            this._source.connect('destroy', () => {
                this._source = null;
            });
            Main.messageTray.add(this._source);
        }
        const notification = new MessageTray.Notification({source: this._source, title, body});
        for (const [label, callback] of actions)
            notification.addAction(label, callback);
        this._source.addNotification(notification);
        return notification;
    }

    _offerUpdate(data, force = false) {
        const tag = data?.UpdateAvailable;
        if (!tag || this._updating || (!force && (tag === this._offeredUpdateTag || tag === dismissedUpdateTag)))
            return;
        this._offeredUpdateTag = tag;
        if (data.UpdateMode === 'auto') {
            this._runUpdate(tag).catch(logError);
            return;
        }
        const actions = [['Update now', () => this._runUpdate(tag).catch(logError)]];
        if (data.UpdateUrl)
            actions.push(['Release notes', () => Gio.AppInfo.launch_default_for_uri(data.UpdateUrl, null)]);
        const notification = this._notify(`CodexMonitor ${tag} is available`,
            `Installed version: ${data.Version || 'unknown'}.`, actions);
        notification?.connect('destroy', (_n, reason) => {
            if (reason === MessageTray.NotificationDestroyedReason.DISMISSED)
                dismissedUpdateTag = tag;
        });
    }

    // "Check for updates" in the panel menu: asks GitHub now instead of waiting for the bridge.
    async _checkForUpdates() {
        let result;
        try {
            const proc = Gio.Subprocess.new(['bash', UPDATER_PATH, '--check'],
                Gio.SubprocessFlags.STDOUT_PIPE | Gio.SubprocessFlags.STDERR_SILENCE);
            const [stdout] = await proc.communicate_utf8_async(null, null);
            result = JSON.parse(stdout);
        } catch (e) {
            this._notify('Could not check for updates', 'GitHub is not reachable right now. Try again later.');
            return;
        }
        if (result.newer) {
            this._offerUpdate({...this._lastData, UpdateMode: 'notify', UpdateAvailable: result.latest,
                UpdateUrl: result.url, Version: result.local}, true);
        } else {
            this._notify('CodexMonitor is up to date', `Installed version: ${result.local}.`);
        }
    }

    async _runUpdate(tag) {
        if (!tag || this._updating)
            return;
        this._updating = true;
        this._updateIndicator(this._lastData);
        this._notify(`Updating CodexMonitor to ${tag}`, 'The widget restarts by itself when it is done.');
        try {
            const proc = Gio.Subprocess.new(['bash', UPDATER_PATH, tag],
                Gio.SubprocessFlags.STDOUT_SILENCE | Gio.SubprocessFlags.STDERR_SILENCE);
            await proc.wait_check_async(null);
            // Success is announced by the restarted widget (see _announceUpdate).
        } catch (e) {
            let reason = e.message;
            const log = readText(UPDATE_LOG);
            if (log)
                reason = log.split('\n').pop() || reason;
            this._notify('CodexMonitor update failed', `${reason}\nLog: ${UPDATE_LOG}`,
                [['Retry', () => this._runUpdate(tag).catch(logError)]]);
        } finally {
            this._updating = false;
        }
    }

    // update.sh leaves the installed tag here; the new widget (loaded after the update) reports it.
    _announceUpdate() {
        const tag = readText(UPDATED_MARKER);
        if (tag === null)
            return;
        GLib.unlink(UPDATED_MARKER);
        // The loader only changes at login: compare the running one with the installed file.
        const installed = Number(readText(`${this.path}/extension.js`)?.match(/const LOADER_API = (\d+)/)?.[1] ?? 0);
        const relogin = installed > this._loader.api;
        this._notify(`CodexMonitor updated to ${tag}`, relogin
            ? 'The panel loader changed too: log out and back in once to finish.'
            : 'The new version is running.');
    }
}
