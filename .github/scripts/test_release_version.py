"""Behavioral tests for calendar release versions."""

import unittest
from datetime import date

from release_version import ReleaseVersion, allocate_version, parse_tag


class ReleaseVersionFormattingTests(unittest.TestCase):
    def test_release_version_formats_canonical_tags(self):
        cases = (
            ("january_dev", date(2026, 1, 1), 1, True, "2026.101.1-dev", "v2026.101.1-dev"),
            ("leap_day", date(2024, 2, 29), 42, False, "2024.229.42", "v2024.229.42"),
            ("november_stable", date(2026, 11, 1), 2, False, "2026.1101.2", "v2026.1101.2"),
            ("december", date(2026, 12, 31), 9, True, "2026.1231.9-dev", "v2026.1231.9-dev"),
            ("next_year", date(2027, 1, 1), 1, False, "2027.101.1", "v2027.101.1"),
        )
        for label, day, counter, dev, expected_version, expected_tag in cases:
            with self.subTest(case=label):
                release = ReleaseVersion(day, counter, dev)
                self.assertEqual(expected_version, release.version)
                self.assertEqual(expected_tag, release.tag)

    def test_release_version_is_immutable(self):
        release = ReleaseVersion(date(2026, 11, 1), 2, True)
        changes = (
            ("day", date(2026, 11, 2)),
            ("counter", 3),
            ("dev", False),
        )
        for field, value in changes:
            with self.subTest(field=field):
                with self.assertRaises((AttributeError, TypeError)):
                    setattr(release, field, value)
        self.assertEqual("2026.1101.2-dev", release.version)
        self.assertEqual("v2026.1101.2-dev", release.tag)

    def test_release_version_rejects_out_of_range_counters(self):
        for label, counter in (
            ("negative", -1),
            ("zero", 0),
            ("above_maximum", 65535),
        ):
            with self.subTest(case=label):
                with self.assertRaises(ValueError):
                    ReleaseVersion(date(2026, 11, 1), counter, False)

        self.assertEqual("2026.1101.1", ReleaseVersion(date(2026, 11, 1), 1, False).version)
        self.assertEqual("2026.1101.65534", ReleaseVersion(date(2026, 11, 1), 65534, False).version)

    def test_generated_versions_sort_by_numeric_calendar_components(self):
        releases = (
            ReleaseVersion(date(2027, 1, 1), 1, False),
            ReleaseVersion(date(2026, 11, 1), 1, False),
            ReleaseVersion(date(2024, 3, 1), 1, False),
            ReleaseVersion(date(2026, 1, 1), 1, False),
            ReleaseVersion(date(2026, 12, 31), 1, False),
            ReleaseVersion(date(2025, 12, 31), 65534, False),
            ReleaseVersion(date(2026, 10, 31), 1, False),
            ReleaseVersion(date(2024, 2, 29), 5, False),
            ReleaseVersion(date(2026, 2, 1), 1, False),
            ReleaseVersion(date(2026, 3, 1), 1, False),
        )
        versions = [release.version for release in releases]
        ordered = sorted(versions, key=lambda version: tuple(int(part) for part in version.split(".")))

        self.assertEqual(
            [
                "2024.229.5",
                "2024.301.1",
                "2025.1231.65534",
                "2026.101.1",
                "2026.201.1",
                "2026.301.1",
                "2026.1031.1",
                "2026.1101.1",
                "2026.1231.1",
                "2027.101.1",
            ],
            ordered,
        )


class ReleaseTagParsingTests(unittest.TestCase):
    def test_parse_canonical_tags_returns_release_version(self):
        cases = (
            ("january_stable", "v2026.101.1", date(2026, 1, 1), 1, False, "2026.101.1"),
            ("leap_day_dev", "v2024.229.42-dev", date(2024, 2, 29), 42, True, "2024.229.42-dev"),
            ("november_stable", "v2026.1101.2", date(2026, 11, 1), 2, False, "2026.1101.2"),
            ("maximum_dev", "v2026.1231.65534-dev", date(2026, 12, 31), 65534, True, "2026.1231.65534-dev"),
            ("new_year", "v2027.101.1", date(2027, 1, 1), 1, False, "2027.101.1"),
        )
        for label, tag, day, counter, dev, expected_version in cases:
            with self.subTest(case=label):
                release = parse_tag(tag)
                self.assertIsNotNone(release)
                self.assertEqual(day, release.day)
                self.assertEqual(counter, release.counter)
                self.assertEqual(dev, release.dev)
                self.assertEqual(expected_version, release.version)
                self.assertEqual(tag, release.tag)

    def test_parse_ignores_unrelated_legacy_tags(self):
        cases = (
            ("old_zero_major", "v0.1.0"),
            ("old_stable", "v1.2.3"),
            ("old_prerelease", "v2.0.0-rc.1"),
            ("short_year", "v999.12.1"),
            ("other_namespace", "release-2026.101.1"),
            ("other_tag", "nightly"),
            ("empty", ""),
        )
        for label, tag in cases:
            with self.subTest(case=label):
                self.assertIsNone(parse_tag(tag))

    def test_parse_rejects_malformed_calendar_tags(self):
        cases = (
            ("non_leap_february_29", "v2026.229.1"),
            ("february_30", "v2024.230.1"),
            ("april_31", "v2026.431.1"),
            ("january_32", "v2026.132.1"),
            ("zero_day", "v2026.1100.1"),
            ("zero_month", "v2026.001.1"),
            ("month_13", "v2026.1301.1"),
            ("year_leading_zero", "v02026.101.1"),
            ("month_day_leading_zero", "v2026.0101.1"),
            ("counter_leading_zero", "v2026.101.01"),
            ("counter_zero", "v2026.101.0"),
            ("negative_counter", "v2026.101.-1"),
            ("counter_above_maximum", "v2026.101.65535"),
            ("missing_counter", "v2026.101"),
            ("missing_month_day", "v2026..1"),
            ("uppercase_suffix", "v2026.101.1-DEV"),
            ("unsupported_suffix", "v2026.101.1-rc.1"),
            ("commit_metadata", "v2026.101.1-dev+deadbeef"),
            ("trailing_newline", "v2026.101.1\n"),
            ("trailing_null", "v2026.101.1\x00"),
        )
        for label, tag in cases:
            with self.subTest(case=label):
                with self.assertRaises(ValueError):
                    parse_tag(tag)


class ReleaseVersionAllocationTests(unittest.TestCase):
    def test_allocate_starts_at_one_without_same_day_tags(self):
        cases = (
            ("empty_stable", (), False, "2026.1101.1"),
            ("legacy_dev", ("v0.1.0", "nightly"), True, "2026.1101.1-dev"),
            ("different_day", ("v2026.1031.91", "v2026.1102.8-dev"), False, "2026.1101.1"),
        )
        for label, tags, dev, expected_version in cases:
            with self.subTest(case=label):
                release = allocate_version(date(2026, 11, 1), tags, dev)
                self.assertEqual(expected_version, release.version)

    def test_allocate_shares_counter_across_stable_and_dev(self):
        cases = (
            (
                "stable_after_dev",
                ("v2026.1101.1-dev", "v2026.1101.2", "v2026.1101.3-dev"),
                False,
                "2026.1101.4",
            ),
            (
                "dev_after_stable",
                ("v2026.1101.8", "v2026.1101.6-dev"),
                True,
                "2026.1101.9-dev",
            ),
        )
        for label, tags, dev, expected_version in cases:
            with self.subTest(case=label):
                release = allocate_version(date(2026, 11, 1), tags, dev)
                self.assertEqual(expected_version, release.version)

    def test_allocate_uses_numeric_max_not_lexical_order(self):
        tags = ("v2026.1101.9", "v2026.1101.10-dev", "v2026.1101.2", "v2026.1031.60000")
        release = allocate_version(date(2026, 11, 1), tags, False)
        self.assertEqual("2026.1101.11", release.version)
        self.assertEqual("v2026.1101.11", release.tag)

    def test_allocate_increments_adjacent_counter_boundaries(self):
        cases = (
            ("one_to_two", 1, "2026.1101.2-dev"),
            ("nine_to_ten", 9, "2026.1101.10-dev"),
            ("ten_to_eleven", 10, "2026.1101.11-dev"),
            ("maximum_minus_two", 65532, "2026.1101.65533-dev"),
            ("maximum_minus_one", 65533, "2026.1101.65534-dev"),
        )
        for label, previous, expected_version in cases:
            with self.subTest(case=label):
                release = allocate_version(date(2026, 11, 1), (f"v2026.1101.{previous}",), True)
                self.assertEqual(expected_version, release.version)

        with self.assertRaises(ValueError):
            allocate_version(date(2026, 11, 1), ("v2026.1101.65534-dev",), False)

    def test_allocate_resets_across_day_and_year(self):
        cases = (
            ("next_day", date(2026, 11, 2), "v2026.1101.65534-dev", "2026.1102.1"),
            ("leap_day", date(2024, 2, 29), "v2024.228.65534", "2024.229.1"),
            ("next_year", date(2027, 1, 1), "v2026.1231.65534-dev", "2027.101.1"),
        )
        for label, day, previous_tag, expected_version in cases:
            with self.subTest(case=label):
                release = allocate_version(day, (previous_tag,), False)
                self.assertEqual(expected_version, release.version)

    def test_allocate_rejects_reused_counter_for_stable_and_dev(self):
        stable = "v2026.1101.7"
        dev = "v2026.1101.7-dev"
        for label, tags in (("stable_first", (stable, dev)), ("dev_first", (dev, stable))):
            with self.subTest(case=label):
                with self.assertRaises(ValueError):
                    allocate_version(date(2026, 11, 1), tags, True)

    def test_allocate_rejects_invalid_calendar_tags(self):
        cases = (
            ("invalid_other_day", "v2026.229.4"),
            ("leading_zero_year", "v02026.1101.8"),
            ("leading_zero_counter", "v2026.1101.01"),
            ("overflow_counter", "v2026.1101.65535"),
        )
        for label, invalid_tag in cases:
            with self.subTest(case=label):
                with self.assertRaises(ValueError):
                    allocate_version(date(2026, 11, 1), ("v2026.1101.1", invalid_tag), True)

    def test_allocate_accepts_a_single_pass_iterable(self):
        tags = ["v2026.1101.2-dev", "v2026.1101.4"]
        release = allocate_version(date(2026, 11, 1), (tag for tag in tags), True)
        self.assertEqual("2026.1101.5-dev", release.version)
        self.assertEqual(["v2026.1101.2-dev", "v2026.1101.4"], tags)


if __name__ == "__main__":
    unittest.main()
