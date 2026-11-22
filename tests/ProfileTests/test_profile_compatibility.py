"""The pinned reference accepts added Gaussian profiles without rewriting data."""
import copy
import importlib.util
import json
from pathlib import Path
import sys
import unittest

ROOT = Path(__file__).resolve().parents[2]
SOURCE = ROOT / "upstream-spektrafilm/src/spektrafilm"
spec = importlib.util.spec_from_file_location("pinned_profile_io", SOURCE / "profiles/io.py")
profile_io = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = profile_io
spec.loader.exec_module(profile_io)


class ProfileCompatibilityTests(unittest.TestCase):
    def test_added_profiles_keep_data_and_authorship(self):
        for name in ("fujifilm_eterna_250d", "fujifilm_velvia_100f", "kodak_ultramax_800",
                     "kodak_vision2_100t", "kodak_vision2_500t", "kodak_vision2_50d"):
            with self.subTest(name=name):
                payload = json.loads((SOURCE / "data/profiles" / (name + ".json")).read_text())
                original = copy.deepcopy(payload)
                profile = profile_io.profile_from_dict(payload)
                profile_io._validate_profile(profile, name)
                self.assertEqual(profile.info.custom_look, payload["info"]["custom_look"])
                self.assertEqual(profile.metadata.license, payload["metadata"]["license"])
                self.assertEqual(profile.metadata.datasource, payload["metadata"]["datasource"])
                self.assertEqual(profile.data.density_curves.tolist(), payload["data"]["density_curves"])
                self.assertEqual(payload, original)

    def test_existing_profile_round_trip_keeps_values(self):
        payload = json.loads((SOURCE / "data/profiles/kodak_portra_400.json").read_text())
        profile = profile_io.profile_from_dict(payload)
        result = profile_io._json_safe(profile_io.profile_to_dict(profile))
        for section in ("metadata", "info", "data"):
            for key, value in payload[section].items():
                self.assertEqual(result[section][key], value, (section, key))

    def test_unsupported_development_family_is_not_silently_dropped(self):
        payload = json.loads((SOURCE / "data/profiles/kodak_portra_400.json").read_text())
        payload["data"]["development_time"] = [4, 5, 6.5]
        with self.assertRaises((TypeError, ValueError)):
            profile_io.profile_from_dict(payload)


if __name__ == "__main__":
    unittest.main()
