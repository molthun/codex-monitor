import importlib.util
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("bridge", ROOT / "linux/bridge/codex-bridge.py")
bridge = importlib.util.module_from_spec(spec)
spec.loader.exec_module(bridge)


class TrafficTests(unittest.TestCase):
    def split(self, down, up, processes, exact=None):
        return bridge.traffic_split({"NetDownMbps": down, "NetUpMbps": up}, exact, processes, True, 5, {})

    def test_udp_does_not_inflate_lan(self):
        data = self.split(100, 50, {"LAN": [0, 0, 1, 2]})
        self.assertEqual((data["NetWanDownMbps"], data["NetLanDownMbps"]), (99, 1))
        self.assertEqual((data["NetWanUpMbps"], data["NetLanUpMbps"]), (48, 2))

    def test_counters_exceeding_totals(self):
        data = self.split(100, 0, {"mixed": [100, 0, 100, 0]})
        self.assertEqual((data["NetWanDownMbps"], data["NetLanDownMbps"]), (50, 50))

    def test_no_tcp_and_exact_helper(self):
        self.assertEqual(self.split(100, 0, {})["NetWanDownMbps"], 100)
        data = self.split(100, 50, {}, [70, 40, 30, 10])
        self.assertEqual((data["NetWanDownMbps"], data["NetLanDownMbps"]), (70, 30))
        self.assertEqual(data["NetSplitMode"], "exact")


class InstallationTests(unittest.TestCase):
    def test_custom_xdg_paths(self):
        # Stub session tools, never touch the real user's services or extensions.
        with tempfile.TemporaryDirectory() as tmp:
            base = Path(tmp)
            tools = base / "bin"
            tools.mkdir()
            for name in ("systemctl", "gnome-extensions"):
                script = tools / name
                script.write_text("#!/bin/sh\nexit 0\n")
                script.chmod(0o755)
            data = base / "data with spaces%$"
            conf = base / "config with spaces%"
            config_dir = conf / "codex-monitor"
            config_dir.mkdir(parents=True)
            config = json.loads((ROOT / "linux/config.example.json").read_text())
            config["widget"]["internetDownMbps"] = 100  # no terminal prompts
            (config_dir / "config.json").write_text(json.dumps(config))
            env = dict(os.environ, PATH=str(tools) + os.pathsep + os.environ["PATH"],
                       XDG_DATA_HOME=str(data), XDG_CONFIG_HOME=str(conf), CODEX_MONITOR_VERSION="test")
            subprocess.run(["bash", str(ROOT / "linux/install.sh")], env=env, check=True, capture_output=True)
            unit = (conf / "systemd/user/codex-monitor-bridge.service").read_text()
            self.assertIn(str(data).replace("%", "%%").replace("$", "$$") + "/codex-monitor/codex-bridge.py\"", unit)
            self.assertIn('Environment="XDG_CONFIG_HOME=' + str(conf).replace("%", "%%") + '"', unit)
            self.assertTrue((data / "codex-monitor/codex-bridge.py").exists())
            # A custom configuration must survive installation.
            self.assertEqual(json.loads((config_dir / "config.json").read_text()), config)


if __name__ == "__main__":
    unittest.main()
