// CodexMonitor desktop widget for GNOME Shell.
//
// Renders the metrics that codex-bridge.py writes to
// $XDG_RUNTIME_DIR/codex-monitor/sensors.json. The widget lives in the
// background group, so it sits on the desktop under all windows and is
// click-through, like the Rainmeter skin with AlwaysOnTop=-2.

import GLib from 'gi://GLib';
import Gio from 'gi://Gio';
import St from 'gi://St';
import Clutter from 'gi://Clutter';
import Pango from 'gi://Pango';

import {Extension} from 'resource:///org/gnome/shell/extensions/extension.js';
import * as Main from 'resource:///org/gnome/shell/ui/main.js';

Gio._promisify(Gio.File.prototype, 'load_contents_async');

const CONFIG_PATH = GLib.build_filenamev([GLib.get_user_config_dir(), 'codex-monitor', 'config.json']);
const DEFAULT_SENSORS_PATH = GLib.build_filenamev([GLib.get_user_runtime_dir(), 'codex-monitor', 'sensors.json']);
const STALE_SECONDS = 5;
const GRAPH_POINTS = 60;

// Palette and gradients copied from the Rainmeter skin [Variables].
const COLOR = {
    muted: 'rgba(154,166,178,0.92)',
    cpu: '#00e5ff', ram: '#45c997', gpu: '#9788ff', net: '#58b3ff', disk: '#74d684',
    ok: '#00e5ff', warm: '#ffc15e', hot: '#ff7171',
};
const GRADIENT = {
    cpu: ['#00e5ff', '#0091ea'],
    ram: ['#45c997', '#00e676'],
    gpu: ['#9788ff', '#e0c3fc'],
    net: ['#2979ff', '#00e5ff'],
    disk: ['#74d684', '#8bc34a'],
};

// 1080p skin geometry; larger profiles scale it (430 -> 540 / 720 px wide).
const BASE = {width: 430, pad: 18, barH: 7, barR: 3, rowGap: 2, barGap: 4, sectionGap: 11,
    title: 19, subtitle: 11, label: 13, section: 11, health: 12, icon: 14, graphH: 70};
const PROFILE_SCALE = {'1080p': 1, '2k': 540 / 430, '4k': 720 / 430};

const DEFAULT_WIDGET_CONFIG = {
    profile: 'Auto',
    autoProfileThresholds: {'2K': 1400, '4K': 2000},
    marginRight: 24,
    marginTop: 24,
    netMaxMbps: 1000,
    diskIOMaxMBs: 1000,
    fanMaxRpm: 2500,
    diskLabels: {},
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
    constructor(extensionPath, scale, widgetConfig) {
        this._path = extensionPath;
        this._k = scale;
        this._cfg = widgetConfig;
        // Start with a flat baseline so the graph spans the full width at once.
        this._netHistory = Array.from({length: GRAPH_POINTS}, () => [0, 0]);

        const s = n => Math.round(n * scale);
        this._s = s;
        this._innerW = s(BASE.width) - 2 * s(BASE.pad);

        this.actor = new St.BoxLayout({
            style_class: 'codex-monitor',
            orientation: Clutter.Orientation.VERTICAL,
            style: `width: ${s(BASE.width)}px; padding: ${s(14)}px ${s(BASE.pad)}px ${s(BASE.pad)}px; border-radius: ${s(12)}px;`,
        });

        this._buildHeader();
        this._buildHealth();

        this._section('cpu.png', 'PERFORMANCE');
        this._cpuLoad = this._row('CPU load');
        this._ram = this._row('RAM used');
        this._gpuLoad = this._row('GPU load');
        this._vram = this._row('VRAM used');

        this._section('temp.png', 'TEMPERATURES');
        this._cpuTemp = this._row('CPU temp');
        this._gpuTemp = this._row('GPU temp');

        this._section('fan.png', 'COOLING');
        this._cpuFan = this._row('CPU cooler');
        this._caseFan = this._row('Case fan');
        this._psuFan = this._row('PSU fan');
        this._gpuFan = this._row('GPU fans');

        this._netTitle = this._section('net.png', 'NETWORK TRAFFIC', true);
        this._buildGraph();
        this._netDown = this._row('Download', COLOR.net);
        this._netUp = this._row('Upload', COLOR.gpu);
        this._buildNetFooter();

        this._section('disk.png', 'DISK I/O');
        this._diskIO = [0, 1, 2].map(() => this._row(''));

        this._section('disk.png', 'DRIVES USED');
        this._diskUsed = [0, 1, 2].map(() => this._row(''));
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
        for (const [color, text] of [[COLOR.net, 'Download'], [COLOR.gpu, 'Upload']]) {
            legend.add_child(new St.Widget({
                style: `width: ${s(10)}px; height: ${s(2)}px; background-color: ${color};`,
                y_align: Clutter.ActorAlign.CENTER,
            }));
            legend.add_child(this._label(text, BASE.subtitle, {muted: true, style: `margin-right: ${s(8)}px;`}));
        }
        legend.add_child(this._label('auto scale', BASE.subtitle, {muted: true, expand: true, align: Clutter.ActorAlign.END}));
        this.actor.add_child(legend);
    }

    _paintGraph(area) {
        const cr = area.get_context();
        const [w, h] = area.get_surface_size();
        const history = this._netHistory;
        if (history.length > 1) {
            const max = Math.max(1, ...history.map(p => Math.max(p[0], p[1]))) * 1.15;
            const step = w / (GRAPH_POINTS - 1);
            const x0 = w - (history.length - 1) * step;
            const y = v => h - 2 - (v / max) * (h - 4);

            // Download: filled area + line. Upload: line.
            cr.moveTo(x0, h);
            history.forEach((p, i) => cr.lineTo(x0 + i * step, y(p[0])));
            cr.lineTo(w, h);
            cr.closePath();
            cr.setSourceRGBA(0x58 / 255, 0xb3 / 255, 1, 0.22);
            cr.fill();

            for (const [idx, rgb] of [[0, [0x58, 0xb3, 0xff]], [1, [0x97, 0x88, 0xff]]]) {
                history.forEach((p, i) => (i ? cr.lineTo : cr.moveTo).call(cr, x0 + i * step, y(p[idx])));
                cr.setSourceRGBA(rgb[0] / 255, rgb[1] / 255, rgb[2] / 255, 0.95);
                cr.setLineWidth(Math.max(1.2, 1.4 * this._k));
                cr.stroke();
            }
        }
        cr.$dispose();
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
        row.value.text = value;
        const alert = state === 'warm' || state === 'hot';
        row.label.set_style(`font-size: ${this._s(BASE.label)}px;${alert ? ` color: ${COLOR[state]};` : ''}`);
        this._setBar(row.bar, fraction, alert ? COLOR[state] : colors);
    }

    _setHealth(key, text, state, okColor = COLOR.ok) {
        const color = state === 'ok' ? okColor : COLOR[state];
        const cell = this._health[key];
        cell.text.text = text;
        cell.text.set_style(`font-size: ${this._s(BASE.health)}px; font-weight: 600; color: ${color};`);
        cell.bar.set_style(`height: ${this._s(3)}px; border-radius: ${this._s(2)}px; background-color: ${color};`);
    }

    update(d) {
        const cfg = this._cfg;
        const stale = !d || (Date.now() / 1000 - (d.Timestamp || 0)) > STALE_SECONDS;
        this._subtitle.text = stale ? 'Bridge offline: codex-monitor-bridge.service' : 'Hardware bridge / desktop widget';
        this._subtitle.set_style(`font-size: ${this._s(BASE.subtitle)}px;${stale ? ` color: ${COLOR.hot};` : ''}`);
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

        // Network
        const down = d.NetDownMbps ?? 0;
        const up = d.NetUpMbps ?? 0;
        this._netHistory.push([down, up]);
        if (this._netHistory.length > GRAPH_POINTS)
            this._netHistory.shift();
        this._graph.queue_repaint();
        this._netTitle.text = `DL ${down.toFixed(1)} / UL ${up.toFixed(1)} Mbps`;
        this._setRow(this._netDown, `${down.toFixed(1)} Mbps`, down / cfg.netMaxMbps, GRADIENT.net);
        this._setRow(this._netUp, `${up.toFixed(1)} Mbps`, up / cfg.netMaxMbps, GRADIENT.gpu);
        this._ethFooter.text = `ETH DL/UL ${fmt(d.NetEthInMbps, 1)}/${fmt(d.NetEthOutMbps, 1)} Mbps`;
        const mode = d.NetWifiActiveMode || 'Off';
        this._wifiFooter.text = mode === 'Off' ? 'Wi-Fi off'
            : `${mode === 'AP' ? 'AP' : 'Wi-Fi'} DL/UL ${fmt(d.NetWifiActiveDlMbps, 1)}/${fmt(d.NetWifiActiveUlMbps, 1)} Mbps`;

        // Disks
        const disks = d.Disks || [];
        for (let i = 0; i < 3; i++) {
            const disk = disks[i];
            const io = this._diskIO[i];
            const used = this._diskUsed[i];
            for (const row of [io, used]) {
                row.label.get_parent().visible = !!disk;
                row.bar.track.visible = !!disk;
            }
            if (!disk)
                continue;
            const name = cfg.diskLabels[disk.mount] || disk.label;
            io.label.text = name;
            this._setRow(io, `R ${disk.readMBs.toFixed(1)} / W ${disk.writeMBs.toFixed(1)} MB/s`,
                (disk.readMBs + disk.writeMBs) / cfg.diskIOMaxMBs, GRADIENT.disk);
            const pct = disk.totalB ? disk.usedB / disk.totalB * 100 : 0;
            used.label.text = `${name} ${pct.toFixed(0)}%`;
            this._setRow(used, `${fmtBytes(disk.usedB)} / ${fmtBytes(disk.totalB)}`, pct / 100, GRADIENT.disk,
                level(pct, 85, 95));
        }
    }
}

export default class CodexMonitorExtension extends Extension {
    enable() {
        this._cancellable = new Gio.Cancellable();
        this._loadConfig();
        this._rebuild();

        this._monitorsChangedId = Main.layoutManager.connect('monitors-changed', () => this._rebuild());

        this._configMonitor = Gio.File.new_for_path(CONFIG_PATH).monitor_file(Gio.FileMonitorFlags.NONE, null);
        this._configMonitor.connect('changed', (_m, _f, _o, event) => {
            if (event === Gio.FileMonitorEvent.CHANGES_DONE_HINT || event === Gio.FileMonitorEvent.CREATED) {
                this._loadConfig();
                this._rebuild();
            }
        });

        this._timerId = GLib.timeout_add(GLib.PRIORITY_DEFAULT, 1000, () => {
            this._refresh().catch(logError);
            return GLib.SOURCE_CONTINUE;
        });
        this._refresh().catch(logError);
    }

    disable() {
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
        this._widget?.destroy();
        this._widget = null;
    }

    _loadConfig() {
        let json = {};
        try {
            const [, bytes] = GLib.file_get_contents(CONFIG_PATH);
            json = JSON.parse(new TextDecoder().decode(bytes));
        } catch (e) {
            // No config yet: defaults are fine.
        }
        this._widgetConfig = {...DEFAULT_WIDGET_CONFIG, ...(json.widget || {})};
        this._sensorsPath = json.bridge?.outputFile
            ? json.bridge.outputFile.replace(/^~/, GLib.get_home_dir())
            : DEFAULT_SENSORS_PATH;
    }

    _scaleForMonitor(monitor) {
        const profile = String(this._widgetConfig.profile).toLowerCase();
        if (profile in PROFILE_SCALE)
            return PROFILE_SCALE[profile];
        // Auto: pick by the physical height, like Switch-WidgetSize.ps1.
        const thresholds = {'2K': 1400, '4K': 2000, ...this._widgetConfig.autoProfileThresholds};
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
        // Shrink to fit short screens (e.g. 1080p with a tall panel or dock).
        const [, natHeight] = this._widget.actor.get_preferred_height(-1);
        if (natHeight > available) {
            scale *= available / natHeight;
            this._createWidget(scale);
        }

        const [, natWidth] = this._widget.actor.get_preferred_width(-1);
        this._widget.actor.set_position(monitor.x + monitor.width - natWidth - cfg.marginRight, top);
    }

    _createWidget(scale) {
        this._widget?.destroy();
        this._widget = new MonitorWidget(this.path, scale, this._widgetConfig);
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
        this._lastData = data;
        this._widget?.update(data);
    }
}
