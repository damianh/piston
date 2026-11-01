"""Calendar release versions shared by the publishing workflow and its tests."""

from dataclasses import dataclass
from datetime import date
import re
from typing import Iterable


MAX_COUNTER = 65534
_CALENDAR_PREFIX = re.compile(r"^v[0-9]{4,}\.")
_CALENDAR_TAG = re.compile(
    r"^v([1-9][0-9]{3})\.([1-9][0-9]{2,3})\.([1-9][0-9]*)(-dev)?$"
)


@dataclass(frozen=True)
class ReleaseVersion:
    day: date
    counter: int
    dev: bool

    def __post_init__(self):
        if not 1000 <= self.day.year <= 9999:
            raise ValueError("The release year must have four digits.")
        if not 1 <= self.counter <= MAX_COUNTER:
            raise ValueError(f"The daily release counter must be between 1 and {MAX_COUNTER}.")

    @property
    def version(self) -> str:
        month_day = self.day.month * 100 + self.day.day
        suffix = "-dev" if self.dev else ""
        return f"{self.day.year}.{month_day}.{self.counter}{suffix}"

    @property
    def tag(self) -> str:
        return f"v{self.version}"


def parse_tag(tag: str) -> ReleaseVersion | None:
    match = _CALENDAR_TAG.fullmatch(tag)
    if match is None:
        if _CALENDAR_PREFIX.match(tag):
            raise ValueError(f"Invalid calendar release tag: {tag}")
        return None

    year, month_day, counter = (int(value) for value in match.group(1, 2, 3))
    month, day = divmod(month_day, 100)
    try:
        version = ReleaseVersion(date(year, month, day), counter, match.group(4) is not None)
    except ValueError as error:
        raise ValueError(f"Invalid calendar release tag: {tag}: {error}") from error
    if version.tag != tag:
        raise ValueError(f"Noncanonical calendar release tag: {tag}")
    return version


def allocate_version(day: date, tags: Iterable[str], dev: bool) -> ReleaseVersion:
    counters: dict[tuple[date, int], str] = {}
    for tag in tags:
        version = parse_tag(tag)
        if version is None:
            continue
        key = (version.day, version.counter)
        if key in counters and counters[key] != tag:
            raise ValueError(f"Calendar release counter is reused by {counters[key]} and {tag}.")
        counters[key] = tag

    last_counter = max((counter for release_day, counter in counters if release_day == day), default=0)
    return ReleaseVersion(day, last_counter + 1, dev)
