"""Contract tests with synthetic colored shapes; these are not character art."""
import importlib.util
import json
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

import numpy as np
from PIL import Image

APP = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("flat2d_pipeline", APP / "tools/build_flat2d_pack.py")
pipeline = importlib.util.module_from_spec(spec)
spec.loader.exec_module(pipeline)


class PipelineTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cache = pipeline.WORKSPACE / "build/.cache/contract-tests"
        cache.mkdir(parents=True, exist_ok=True)
        cls.root = Path(tempfile.mkdtemp(prefix="contract-", dir=str(cache)))
        cls.ffmpeg = shutil.which("ffmpeg")
        cls.ffprobe = shutil.which("ffprobe")
        cls.green = np.zeros((720, 1280, 4), dtype=np.uint8)
        cls.green[:] = (0, 255, 0, 255)
        cls.green[144:612, 520:760] = (20, 40, 230, 255)

    def test_green_key_preserves_blue_and_gray(self):
        frame = self.green.copy()
        frame[240:280, 580:600] = (160, 160, 160, 255)
        output = pipeline.key_green(frame)
        self.assertEqual(output[0, 0, 3], 0)
        self.assertEqual(output[200, 550, 3], 255)
        self.assertEqual(output[250, 590, 3], 255)
        # The antialiased contour extends one pixel beyond the solid shape.
        self.assertEqual(pipeline.bbox(output), (519, 143, 761, 613))

    def test_real_alpha_required(self):
        with self.assertRaisesRegex(ValueError, "transparent"):
            pipeline.bbox(self.green)
        with self.assertRaisesRegex(ValueError, "Empty"):
            pipeline.bbox(np.zeros((10, 10, 4), dtype=np.uint8))

    def test_muted_screen_and_dark_green_codec_fringe(self):
        frame = np.full((80, 120, 4), (133, 168, 117, 255), dtype=np.uint8)
        frame[20:60, 40:80] = (28, 28, 32, 255)
        frame[30:50, 50:70] = (160, 160, 160, 255)
        frame[19, 40:80] = (17, 30, 15, 255)
        frame[10, 30] = (30, 30, 30, 255)
        output = pipeline.key_green(frame)
        self.assertEqual(output[10, 10, 3], 0)
        self.assertEqual(output[10, 30, 3], 0)
        self.assertLess(output[19, 60, 3], 64)
        self.assertEqual(output[25, 60, 3], 255)
        self.assertEqual(output[40, 60].tolist(), [160, 160, 160, 255])
        self.assertFalse(np.any((output[..., 3] > 12)
            & (output[..., 1].astype(int) > np.maximum(output[..., 0], output[..., 2]).astype(int) + 5)))

    def test_no_green_is_rejected(self):
        with self.assertRaisesRegex(ValueError, "green background"):
            pipeline.key_green(np.full((10, 10, 4), 255, dtype=np.uint8))

    def test_pack_refuses_pending_reference(self):
        cfg = self.root / "pending.json"
        pipeline.write_json(cfg, {"referenceStatus": "pending"})
        output = self.root / "should-not-exist"
        with self.assertRaisesRegex(ValueError, "Reference is pending"):
            pipeline.build(cfg, output, True, self.ffmpeg, self.ffprobe)
        self.assertFalse(output.exists())

    def test_normalization_dedup_timing_and_crop(self):
        src = self.root / "normalized-source.png"
        Image.fromarray(pipeline.key_green(self.green)).save(src)
        output = self.root / "normalization"
        frames, union = pipeline.normalize([src] * 36,
            {"anchorX": 640, "anchorY": 612, "standingHeight": 468}, output)
        self.assertEqual(len(frames), 1)
        self.assertEqual(frames[0]["durationMs"], 3000)
        self.assertLessEqual(union[3], 638)
        with Image.open(output / "0000.png") as result:
            self.assertEqual(result.size, (616, 656))
            self.assertEqual(result.mode, "RGBA")
            self.assertEqual(result.getpixel((615, 655))[3], 0)
        with self.assertRaisesRegex(ValueError, "clipped"):
            pipeline.normalize([src], {"anchorX": 0, "anchorY": 612, "standingHeight": 468}, self.root / "clipped")

    def test_full_pack_requires_confirmed_sleep_poses(self):
        reference = self.root / "pose-gate-reference.png"
        Image.fromarray(self.green).save(reference)
        cfg = self.root / "pose-gate.json"
        pipeline.write_json(cfg, {"referenceStatus": "approved", "referenceImage": str(reference),
                                  "sourceRoot": str(self.root),
                                  "jobs": [{"id": action[0], "input": f"{action[0]}.mp4"}
                                          for action in pipeline.ACTIONS]})
        output = self.root / "unapproved-poses"
        with self.assertRaisesRegex(ValueError, "Sleep pose reference pending"):
            pipeline.build(cfg, output, False, self.ffmpeg, self.ffprobe)
        self.assertFalse(output.exists())

    def test_expanded_menu_actions_keep_sleep_chain_and_random_pool(self):
        frames = {a[0]: [{"path": a[0] + ".png", "durationMs": 100}] for a in pipeline.ACTIONS}
        actions = {a["id"]: a for a in pipeline.manifest_actions(frames)}
        self.assertEqual(actions["sleep_enter"]["nextActionId"], "sleep_idle")
        self.assertTrue(actions["sleep_idle"]["loop"])
        self.assertTrue(actions["sleep_idle"]["showSleepIndicator"])
        self.assertEqual(actions["wake"]["nextActionId"], "idle")
        self.assertEqual({a["id"] for a in actions.values() if "random" in a["triggers"]},
                         {"blink", "wave", "happy", "stretch"})
        for aid in ("nod", "shake_head", "shy", "look_around"):
            self.assertEqual(actions[aid]["nextActionId"], "idle")
            self.assertFalse(actions[aid]["loop"])
            self.assertEqual(actions[aid]["triggers"], [])

    def test_green_and_vp9_alpha_build_roundtrip(self):
        if not self.ffmpeg or not self.ffprobe:
            self.skipTest("ffmpeg/ffprobe unavailable")
        source = self.root / "videos"
        source.mkdir()
        green_png = source / "green.png"
        Image.fromarray(self.green).save(green_png)
        transparent = source / "alpha.png"
        Image.fromarray(pipeline.key_green(self.green)).save(transparent)
        for png, output, codec in [(green_png, source / "idle.mp4", ["-c:v", "libx264", "-pix_fmt", "yuv420p"]),
                                   (transparent, source / "wave.webm", ["-c:v", "libvpx-vp9", "-pix_fmt", "yuva420p", "-auto-alt-ref", "0"])]:
            subprocess.run([self.ffmpeg, "-v", "error", "-nostdin", "-loop", "1", "-i", str(png),
                            "-t", "3", "-r", "12", *codec, str(output)], check=True, capture_output=True)
        cfg = self.root / "jobs.json"
        config = {"referenceStatus": "approved", "referenceImage": str(transparent), "sourceRoot": str(source),
                  "jobs": [{"id": "idle", "input": "idle.mp4", "mode": "green", "start": 0, "end": 3},
                           {"id": "wave", "input": "wave.webm", "mode": "alpha", "start": 0, "end": 3}]}
        pipeline.write_json(cfg, config)
        output = self.root / "pilot" / "ellen-flat2d"
        pipeline.build(cfg, output, True, self.ffmpeg, self.ffprobe)
        manifest = json.loads((output / "character.json").read_text(encoding="utf-8"))
        self.assertEqual({a["id"] for a in manifest["actions"]}, {"idle", "wave"})
        self.assertEqual(manifest["sleepThresholdMs"], 0)
        for action in manifest["actions"]:
            self.assertEqual(sum(f["durationMs"] for f in action["frames"]), 3000)
            self.assertTrue(all((output / f["path"]).is_file() for f in action["frames"]))
        report = json.loads((output / "report.json").read_text(encoding="utf-8"))
        self.assertEqual(report["visualAcceptance"], "pending")
        self.assertEqual(set(report["pendingActions"]), {a[0] for a in pipeline.ACTIONS} - {"idle", "wave"})
        with self.assertRaises(FileExistsError):
            pipeline.build(cfg, output, True, self.ffmpeg, self.ffprobe)
        config["jobs"][1]["mode"] = "alpha"
        config["jobs"][1]["input"] = "idle.mp4"
        pipeline.write_json(cfg, config)
        failed = self.root / "opaque-failure"
        with self.assertRaisesRegex(ValueError, "transparent"):
            pipeline.build(cfg, failed, True, self.ffmpeg, self.ffprobe)
        self.assertFalse((failed / "character.json").exists())
        self.assertEqual(json.loads((failed / "report.json").read_text(encoding="utf-8"))["status"], "failed")
        config["jobs"][1]["input"] = "missing.mov"
        pipeline.write_json(cfg, config)
        with self.assertRaisesRegex(ValueError, "Missing input"):
            pipeline.build(cfg, self.root / "missing", True, self.ffmpeg, self.ffprobe)
        (self.root / "TEST_INPUTS_NOT_CHARACTER_ART.txt").write_text("Synthetic contract checks only; no actual pet animation or visual acceptance.\n")


if __name__ == "__main__":
    unittest.main(verbosity=2)
