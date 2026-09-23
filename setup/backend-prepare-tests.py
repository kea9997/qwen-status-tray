"""Offline preparation regression tests. No Docker, network, model or GPU required."""
import hashlib
import io
import json
from pathlib import Path
import runpy
import tempfile
import unittest
from unittest.mock import patch

module = runpy.run_path(str(Path(__file__).with_name("backend-prepare.py")))


class Response(io.BytesIO):
    def __init__(self, payload, status=200, headers=None):
        super().__init__(payload)
        self.status = status
        self.headers = headers or {}


class PreparationTests(unittest.TestCase):
    def config(self, payload):
        return {"model": {"repository": "test/public", "revision": "a" * 40,
                          "filename": "model.ninfer", "sha256": hashlib.sha256(payload).hexdigest()}}

    def test_pinned_url_and_checksum(self):
        payload = b"offline-artifact"
        with tempfile.TemporaryDirectory(prefix="backend-test-") as temp:
            with patch("urllib.request.urlopen", return_value=Response(payload)) as request:
                module["prepare_ninfer"](self.config(payload), temp)
                self.assertIn("/resolve/" + "a" * 40 + "/", request.call_args[0][0].full_url)
            self.assertEqual((Path(temp) / "model.ninfer").read_bytes(), payload)
            with patch("urllib.request.urlopen", side_effect=AssertionError("must not redownload")):
                module["prepare_ninfer"](self.config(payload), temp)

    def test_resumed_download_and_ignored_range(self):
        payload = b"offline-artifact"
        for status in (200, 206):
            with self.subTest(status=status), tempfile.TemporaryDirectory(prefix="backend-test-") as temp:
                (Path(temp) / "model.ninfer.partial").write_bytes(payload[:4])
                response = Response(payload[4:] if status == 206 else payload, status,
                                    {"Content-Range": f"bytes 4-{len(payload)-1}/{len(payload)}"})
                with patch("urllib.request.urlopen", return_value=response):
                    module["prepare_ninfer"](self.config(payload), temp)
                self.assertEqual((Path(temp) / "model.ninfer").read_bytes(), payload)

    def test_corrupt_download_is_never_published(self):
        with tempfile.TemporaryDirectory(prefix="backend-test-") as temp:
            with patch("urllib.request.urlopen", return_value=Response(b"corrupt")):
                with self.assertRaisesRegex(RuntimeError, "SHA256"):
                    module["prepare_ninfer"](self.config(b"expected"), temp)
            self.assertFalse((Path(temp) / "model.ninfer").exists())

    def test_complete_partial_is_promoted_without_range_request(self):
        payload = b"offline-artifact"
        with tempfile.TemporaryDirectory(prefix="backend-test-") as temp:
            (Path(temp) / "model.ninfer.partial").write_bytes(payload)
            with patch("urllib.request.urlopen", side_effect=AssertionError("must not request an unsatisfiable range")):
                module["prepare_ninfer"](self.config(payload), temp)
            self.assertEqual((Path(temp) / "model.ninfer").read_bytes(), payload)

    def test_existing_foreign_model_is_not_overwritten(self):
        with tempfile.TemporaryDirectory(prefix="backend-test-") as temp:
            target = Path(temp) / "model.ninfer"
            target.write_bytes(b"user-file")
            with self.assertRaisesRegex(RuntimeError, "refusing to replace"):
                module["prepare_ninfer"](self.config(b"expected"), temp)
            self.assertEqual(target.read_bytes(), b"user-file")

    def test_shard_paths_cannot_escape_model_directory(self):
        with tempfile.TemporaryDirectory(prefix="backend-test-") as temp:
            directory = Path(temp) / "model"
            directory.mkdir()
            for name in ("config.json", "tokenizer.json", "tokenizer_config.json"):
                (directory / name).write_text("{}")
            (Path(temp) / "foreign").write_text("not a shard")
            (directory / "model.safetensors.index.json").write_text(json.dumps({"weight_map": {"weight": "../foreign"}}))
            with self.assertRaisesRegex(RuntimeError, "Invalid or absent"):
                module["validate_safetensors_model"](directory)


if __name__ == "__main__":
    unittest.main()
