"""Tiny Rainmeter emulator for checking generated CodexMonitor skins without Windows.

Generate a skin with "CodexBridge.exe --build-skin --out skin.ini --screen-height 1080" (or the
SkinBuilder from a test project), take a temps.txt the bridge wrote, then render both here.

Evaluates variables, WebParser (RegExp + StringIndex), Calc formulas, IfCondition / IfMatch
bangs (!SetOption, !SetVariable, !ShowMeter, !HideMeter), then draws String, Shape, Image,
Histogram and Line meters with PIL. Not a full Rainmeter, but close enough to see layout bugs.
Usage: python3 rainmeter-emulator.py skin.ini temps.txt out.png  (needs Pillow and DejaVu fonts)
"""
import math
import os
import re
import sys

from PIL import Image, ImageDraw, ImageFont

ICONS = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "@Resources", "Icons")
FONT = "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf"
FONT_BOLD = "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf"
problems = []


def parse(path):
    raw = open(path, "rb").read()
    # Generated skins are UTF-16 LE (what Rainmeter reads); older presets were UTF-8.
    text = (raw.decode("utf-16") if raw[:2] == b"\xff\xfe" else raw.decode("utf-8-sig")).replace("\r\n", "\n")
    sections, current = [], None
    for line in text.split("\n"):
        m = re.match(r"^\[([^\]]+)\]\s*$", line)
        if m:
            current = (m.group(1), {})
            sections.append(current)
        elif current and "=" in line and not line.startswith(";"):
            k, v = line.split("=", 1)
            current[1][k.strip()] = v
    return sections


# ------------------------------------------------------------------ formulas

class Formula:
    TOKEN = re.compile(r"\s*(?:(\d+\.?\d*(?:e[+-]?\d+)?)|([A-Za-z_][A-Za-z0-9_]*)|(&&|\|\||<=|>=|==|!=|[-+*/%()?:<>=,]))")

    def __init__(self, text, lookup):
        self.tokens, pos = [], 0
        text = text.strip()
        while pos < len(text):
            m = self.TOKEN.match(text, pos)
            if not m or m.end() == pos:
                raise ValueError(f"bad formula near {text[pos:pos + 20]!r} in {text!r}")
            self.tokens.append(m.group(1) or m.group(2) or m.group(3))
            pos = m.end()
        self.i, self.lookup = 0, lookup

    def peek(self):
        return self.tokens[self.i] if self.i < len(self.tokens) else None

    def take(self, expected=None):
        tok = self.peek()
        if expected and tok != expected:
            raise ValueError(f"expected {expected} got {tok}")
        self.i += 1
        return tok

    def value(self):
        v = self.ternary()
        if self.peek() is not None:
            raise ValueError(f"trailing {self.tokens[self.i:]}")
        return v

    def ternary(self):
        cond = self.logic()
        if self.peek() == "?":
            self.take()
            a = self.ternary()
            self.take(":")
            b = self.ternary()
            return a if cond else b
        return cond

    def logic(self):
        v = self.compare()
        while self.peek() in ("&&", "||"):
            op = self.take()
            w = self.compare()
            v = float(bool(v) and bool(w)) if op == "&&" else float(bool(v) or bool(w))
        return v

    def compare(self):
        v = self.add()
        while self.peek() in ("<", ">", "<=", ">=", "=", "==", "!="):
            op, w = self.take(), self.add()
            v = float({"<": v < w, ">": v > w, "<=": v <= w, ">=": v >= w, "=": v == w, "==": v == w, "!=": v != w}[op])
        return v

    def add(self):
        v = self.mul()
        while self.peek() in ("+", "-"):
            op, w = self.take(), self.mul()
            v = v + w if op == "+" else v - w
        return v

    def mul(self):
        v = self.unary()
        while self.peek() in ("*", "/", "%"):
            op, w = self.take(), self.unary()
            v = v * w if op == "*" else (v / w if w else 0.0) if op == "/" else (v % w if w else 0.0)
        return v

    def unary(self):
        if self.peek() == "-":
            self.take()
            return -self.unary()
        return self.atom()

    def atom(self):
        tok = self.take()
        if tok == "(":
            v = self.ternary()
            self.take(")")
            return v
        if re.match(r"\d", tok):
            return float(tok)
        if self.peek() == "(":
            self.take("(")
            args = [self.ternary()]
            while self.peek() == ",":
                self.take()
                args.append(self.ternary())
            self.take(")")
            f = {"clamp": lambda x, a, b: max(a, min(b, x)), "max": max, "min": min, "round": round, "abs": abs}[tok.lower()]
            return float(f(*args))
        return float(self.lookup(tok))


# ------------------------------------------------------------------ skin

class Skin:
    def __init__(self, path, temps):
        self.sections = parse(path)
        self.by_name = {n: o for n, o in self.sections}
        self.vars = dict(self.by_name.get("Variables", {}))
        self.values, self.strings = {}, {}
        self.geometry = {}
        self.hidden = set()
        self.temps = temps
        self.fake = {"MeasureCPU": 23, "MeasureRAM": 13e9, "MeasureRAMTotal": 32e9, "MeasureGPU": 12}

    def var(self, text, depth=0):
        if depth > 10:
            return text
        new = re.sub(r"#(\w+|@)#", lambda m: "@RES@" if m.group(1) == "@" else self.vars.get(m.group(1), m.group(0)), text)
        new = re.sub(r"\[#(\w+)\]", lambda m: self.vars.get(m.group(1), m.group(0)), new)
        return new if new == text else self.var(new, depth + 1)

    def sectionvars(self, text):
        def repl(m):
            name, prop = m.group(1), m.group(2)
            if prop is None:
                if name in self.strings:
                    return self.strings[name]
                problems.append(f"unknown [{name}]")
                return ""
            if prop == "":
                return str(self.values.get(name, 0))
            g = self.geometry.get(name)
            if g is None:
                problems.append(f"[{name}:{prop}] before it is drawn")
                return "0"
            return str({"X": g[0], "Y": g[1], "W": g[2], "H": g[3]}[prop])
        return re.sub(r"\[([A-Za-z]\w*)(?::(\w*))?\]", repl, text)

    def num(self, text, opts=None):
        text = self.sectionvars(self.var(text))
        try:
            return Formula(text, lambda name: self.values.get(name, 0)).value()
        except (ValueError, KeyError, IndexError) as e:
            problems.append(f"formula {text!r}: {e}")
            return 0.0

    # -------------------------------------------------------- measures

    def run_measures(self):
        raw = None
        for name, o in self.sections:
            kind = o.get("Measure")
            if not kind:
                continue
            value, string = 0.0, ""
            if name in self.fake:
                value = self.fake[name]
            elif kind == "WebParser" and "RegExp" in o:
                text = open(self.temps, encoding="utf-8").read()
                m = re.search(self.var(o["RegExp"]).replace("(?i)", ""), text, re.I)
                if not m:
                    problems.append("RegExp does not match temps.txt")
                raw = m
            elif kind == "WebParser":
                idx = int(o["StringIndex"])
                string = raw.group(idx) if raw and idx <= len(raw.groups()) else ""
                if not raw or idx > len(raw.groups()):
                    problems.append(f"{name}: StringIndex {idx} out of range")
                try:
                    value = float(string)
                except ValueError:
                    value = 0.0
            elif kind == "Calc":
                value = self.num(o["Formula"])
            elif kind == "Plugin" and o.get("Plugin") == "PerfMon":
                value = 12e6 if "Read" in o.get("PerfMonCounter", "") else 4e6
            elif kind == "Plugin" and o.get("Plugin") == "UsageMonitor":
                value = 12
            elif kind == "FreeDiskSpace":
                total = 1e12
                used = {"C:": 0.43, "D:": 0.62, "E:": 0.97}.get(o.get("Drive"), 0.3) * total
                value = total if o.get("Total") == "1" else used if o.get("InvertMeasure") == "1" else total - used
            elif kind in ("CPU", "PhysicalMemory"):
                value = self.fake.get(name, 0)
            if not string and kind != "WebParser":
                string = f"{value:g}"
            self.values[name], self.strings[name] = value, string
        # conditions after all values exist (Rainmeter runs them per update; one pass is enough here)
        for name, o in self.sections:
            if "Measure" not in o:
                continue
            for i in [""] + [str(n) for n in range(2, 40)]:
                cond = o.get(f"IfCondition{i}")
                if cond is None:
                    continue
                key = "IfTrueAction" if self.num(cond) else "IfFalseAction"
                self.bangs(o.get(f"{key}{i}", ""), name)
            for i in [""] + [str(n) for n in range(2, 10)]:
                pattern = o.get(f"IfMatch{i}")
                if pattern is None:
                    continue
                hit = re.search(pattern, self.strings[name])
                self.bangs(o.get(f"{'IfMatchAction' if hit else 'IfNotMatchAction'}{i}", ""), name)

    def bangs(self, text, measure):
        for bang in re.findall(r"\[!(.*?)\](?=\[|$)", text):
            parts = re.findall(r'"[^"]*"|\S+', bang)
            parts = [p.strip('"') for p in parts]
            cmd = parts[0]
            # %1 in bang text keeps working through the meter's MeasureName; section variables resolve now
            args = [self.sectionvars(p) if "[" in p and "%" not in p else p for p in parts[1:]]
            if cmd == "SetVariable":
                self.vars[args[0]] = self.var(args[1])
            elif cmd == "SetOption":
                if args[0] not in self.by_name:
                    problems.append(f"!SetOption on missing meter {args[0]}")
                else:
                    self.by_name[args[0]][args[1]] = args[2]
            elif cmd == "ShowMeter":
                self.hidden.discard(args[0])
                self.by_name[args[0]]["Hidden"] = "0"
            elif cmd == "HideMeter":
                self.by_name[args[0]]["Hidden"] = "1"
            else:
                problems.append(f"unknown bang {cmd}")

    # -------------------------------------------------------- drawing

    def color(self, text):
        parts = [int(float(p)) for p in self.var(text).split(",")]
        return tuple(parts + [255] * (4 - len(parts)))[:4]

    def option(self, o, key, default=None):
        # MeterStyle inheritance
        if key in o:
            return o[key]
        style = o.get("MeterStyle")
        if style and style in self.by_name:
            return self.by_name[style].get(key, default)
        return default

    def format_measure(self, o, idx):
        key = "MeasureName" if idx == 1 else f"MeasureName{idx}"
        name = self.option(o, key)
        if not name:
            return None
        if name not in self.values:
            problems.append(f"MeasureName {name} missing")
            return ""
        v, s = self.values[name], self.strings[name]
        try:
            float(s)
        except ValueError:
            return s
        decimals = int(self.option(o, "NumOfDecimals", "0"))
        if self.option(o, "AutoScale") == "1":
            for unit, size in (("T", 1e12), ("G", 1e9), ("M", 1e6), ("k", 1e3)):
                if abs(v) >= size:
                    return f"{v / size:.{decimals}f} {unit}"
        return f"{v:.{decimals}f}"

    def draw(self, out):
        w, h = int(self.num(self.vars["W"])), int(self.num(self.vars["H"]))
        img = Image.new("RGBA", (w + 20, h + 20), (20, 40, 90, 255))
        layer = Image.new("RGBA", img.size, (0, 0, 0, 0))
        d = ImageDraw.Draw(layer)
        ox, oy = 10, 10
        for name, o in self.sections:
            meter = o.get("Meter")
            if not meter or self.option(o, "Hidden") == "1":
                continue
            x = self.num(self.option(o, "X", "0"))
            y = self.num(self.option(o, "Y", "0"))
            if meter == "String":
                size = float(self.var(self.option(o, "FontSize", "10"))) * 4 / 3
                bold = self.option(o, "FontWeight", "400") in ("600", "700")
                font = ImageFont.truetype(FONT_BOLD if bold else FONT, max(6, int(round(size * 0.92))))
                text = self.var(self.option(o, "Text", "%1"))
                for i in range(1, 4):
                    val = self.format_measure(o, i)
                    if val is not None:
                        text = text.replace(f"%{i}", val)
                text = self.sectionvars(text) if "[" in text else text
                tw = d.textlength(text, font=font)
                th = size * 1.25
                align = self.option(o, "StringAlign", "Left").lower()
                left = x - tw if align == "right" else x - tw / 2 if align == "center" else x
                if self.option(o, "ClipString") == "1" and self.option(o, "W"):
                    limit = self.num(self.option(o, "W"))
                    while text and d.textlength(text + "…", font=font) > limit:
                        text = text[:-1]
                    text = text + "…" if text != self.var(self.option(o, "Text", "")) else text
                    tw = d.textlength(text, font=font)
                d.text((ox + left, oy + y), text, font=font, fill=self.color(self.option(o, "FontColor", "255,255,255")))
                self.geometry[name] = (left, y, tw, th)
            elif meter == "Shape":
                for key in sorted((k for k in o if re.fullmatch(r"Shape\d*", k)), key=lambda k: int(k[5:] or 1)):
                    self.shape(d, o, o[key], ox + x, oy + y)
                self.geometry[name] = (x, y, 0, 0)
            elif meter == "Image":
                W = self.num(self.option(o, "W", "16"))
                H = self.num(self.option(o, "H", "16"))
                if "SolidColor" in o:
                    d.rectangle([ox + x, oy + y, ox + x + W, oy + y + H], fill=self.color(o["SolidColor"]))
                else:
                    path = self.sectionvars(self.var(o.get("ImageName", "")))
                    if "AppIcons" not in path:
                        try:
                            icon = Image.open(f"{ICONS}/{path.split(chr(92))[-1]}").convert("RGBA").resize((max(1, int(W)), max(1, int(H))))
                            if "ImageTint" in o:
                                tint = self.color(o["ImageTint"])
                                icon = Image.composite(Image.new("RGBA", icon.size, tint), icon, icon.split()[3])
                                icon.putalpha(Image.open(f"{ICONS}/{path.split(chr(92))[-1]}").convert("RGBA").resize(icon.size).split()[3])
                            layer.alpha_composite(icon, (int(ox + x), int(oy + y)))
                        except OSError as e:
                            problems.append(f"icon {path}: {e}")
                    else:
                        d.rounded_rectangle([ox + x, oy + y, ox + x + W, oy + y + H], 3, fill=(255, 210, 60, 255))
                self.geometry[name] = (x, y, W, H)
            elif meter == "Histogram":
                W, H = self.num(self.option(o, "W")), self.num(self.option(o, "H"))
                prim = self.values.get(o["MeasureName"], 0) / 100
                sec = self.values.get(o["MeasureName2"], 0) / 100
                flip = o.get("Flip") == "1"
                cols = [(0.3 + 0.7 * (i / W) ** 3) for i in range(int(W))]  # ramp up so it looks like history
                for i, f in enumerate(cols):
                    for frac, color in ((sec, o["SecondaryColor"]), (prim, o["BothColor"])):
                        hh = H * min(1, frac * f)
                        top, bottom = (oy + y, oy + y + hh) if flip else (oy + y + H - hh, oy + y + H)
                        d.line([ox + x + i, top, ox + x + i, bottom], fill=self.color(color))
                self.geometry[name] = (x, y, W, H)
            elif meter == "Line":
                W, H = self.num(self.option(o, "W")), self.num(self.option(o, "H"))
                v = self.values.get(o["MeasureName"], 0) / 100
                d.line([ox + x, oy + y + H * (1 - v), ox + x + W, oy + y + H * (1 - v)], fill=self.color(o.get("LineColor", "255,255,255")))
                self.geometry[name] = (x, y, W, H)
            if y > h + 2:
                problems.append(f"{name} at y={y:.0f} is below the skin height {h}")
        img.alpha_composite(layer)
        img.save(out)

    def shape(self, d, o, spec, x0, y0):
        parts = [p.strip() for p in self.var(spec).split("|")]
        head = parts[0].split(" ", 1)
        kind, params = head[0], self.split_args(head[1]) if len(head) > 1 else []
        nums = [self.num(p) for p in params]
        fill, stroke, width = None, None, 1
        for mod in parts[1:]:
            if mod.startswith("Fill Color"):
                fill = self.color(mod[10:].strip())
            elif mod.startswith("Fill LinearGradient"):
                grad = self.var(o.get(mod.split()[-1], self.vars.get(mod.split()[-1], "")))
                cols = re.findall(r"(\d+,\d+,\d+,\d+)", grad)
                fill = self.color(cols[-1]) if cols else (255, 0, 255, 255)
            elif mod.startswith("Stroke Color"):
                stroke = self.color(mod[12:].strip())
            elif mod.startswith("Stroke LinearGradient"):
                stroke = (255, 255, 255, 30)
            elif mod.startswith("StrokeWidth"):
                width = self.num(mod[11:])
        # Rainmeter blends translucent fills; PIL would overwrite the pixels, so draw on a temporary layer.
        base = d._image
        tmp = Image.new("RGBA", base.size, (0, 0, 0, 0))
        dd = ImageDraw.Draw(tmp)
        self._shape(dd, kind, nums, fill, stroke, width, x0, y0)
        base.alpha_composite(tmp)

    def _shape(self, d, kind, nums, fill, stroke, width, x0, y0):
        if kind == "Rectangle":
            x, y, w, h = nums[:4]
            r = nums[4] if len(nums) > 4 else 0
            if w <= 0 or h <= 0:
                return
            d.rounded_rectangle([x0 + x, y0 + y, x0 + x + w, y0 + y + h], min(r, w / 2, h / 2), fill=fill,
                                outline=stroke if width > 0 else None, width=int(width) if width > 0 else 0)
        elif kind == "Line":
            d.line([x0 + nums[0], y0 + nums[1], x0 + nums[2], y0 + nums[3]], fill=stroke or (255, 255, 255, 60), width=max(1, int(width)))
        elif kind == "Ellipse":
            rx = nums[2]
            ry = nums[3] if len(nums) > 3 else rx
            d.ellipse([x0 + nums[0] - rx, y0 + nums[1] - ry, x0 + nums[0] + rx, y0 + nums[1] + ry], fill=fill)

    @staticmethod
    def split_args(text):
        args, depth, cur = [], 0, ""
        for ch in text:
            if ch == "," and depth == 0:
                args.append(cur)
                cur = ""
                continue
            depth += ch == "("
            depth -= ch == ")"
            cur += ch
        args.append(cur)
        return args


if __name__ == "__main__":
    skin = Skin(sys.argv[1], sys.argv[2])
    skin.run_measures()
    skin.draw(sys.argv[3])
    uniq = sorted(set(problems))
    print(f"{sys.argv[1].rsplit('/', 1)[-1]}: {len(uniq)} problems")
    for p in uniq[:30]:
        print("  ", p)
