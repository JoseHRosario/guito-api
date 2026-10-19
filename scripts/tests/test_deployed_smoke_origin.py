"""Execute the smoke runner's origin-resolution snippet offline; never call AWS."""
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
RUNNER = ROOT / "scripts" / "run-deployed-tests.sh"


class DeployedSmokeOriginTests(unittest.TestCase):
    def resolve_origin(self, root, environment):
        source = RUNNER.read_text()
        section = source.split("export GUITO_TARGET_UI_ORIGIN\n", 1)[1].split("\nexport PATH=", 1)[0]
        return subprocess.run(
            ["bash", "-euc", section + '\nprintf "%s\\n" "$GUITO_TARGET_UI_ORIGIN"'],
            env={**os.environ, "REPO_ROOT": str(root), "ENV_TARGET": environment,
                 "PYTHONDONTWRITEBYTECODE": "1",
                 "GUITO_TARGET_UI_ORIGIN": "https://override.invalid"},
            capture_output=True, text=True,
        )

    def test_production_should_resolve_exact_inherited_origin(self):
        result = self.resolve_origin(ROOT, "production")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual("https://guito.web.kerumirembora.com\n", result.stdout)

    def test_staging_should_resolve_exact_overlay_origin(self):
        result = self.resolve_origin(ROOT, "staging")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual("https://guito-staging.web.kerumirembora.com\n", result.stdout)

    def test_invalid_origins_should_fail_closed_even_with_environment_override(self):
        invalid_origins = ([], ["*"], ["http://ui.example"], ["https://*.example"],
                           ["https://ui.example/path"], ["https://user@ui.example"],
                           ["https://ui.example", "https://ui.example"])
        for environment in ("production", "staging"):
            for origins in invalid_origins:
                with self.subTest(environment=environment, origins=origins), tempfile.TemporaryDirectory(
                    dir=os.environ.get("TMPDIR", os.environ.get("RUNNER_TEMP"))
                ) as directory:
                    root = Path(directory)
                    source = root / "src" / "guito-api"
                    source.mkdir(parents=True)
                    (root / "deploy").mkdir()
                    shutil.copyfile(ROOT / "deploy" / "user-session-config.py", root / "deploy" / "user-session-config.py")
                    for name in ("appsettings.json", f"appsettings.{environment.capitalize()}.json"):
                        shutil.copyfile(ROOT / "src" / "guito-api" / name, source / name)
                    # Exercise inherited Production and overridden Staging validation.
                    target = source / ("appsettings.json" if environment == "production" else "appsettings.Staging.json")
                    config = json.loads(target.read_text())
                    config["AppConfiguration"]["Cors"] = {"AllowedOrigins": origins}
                    target.write_text(json.dumps(config))

                    result = self.resolve_origin(root, environment)

                    self.assertNotEqual(0, result.returncode)
                    self.assertEqual("", result.stdout)
                    self.assertIn("ValueError", result.stderr)


if __name__ == "__main__":
    unittest.main()
