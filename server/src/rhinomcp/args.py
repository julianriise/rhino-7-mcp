"""Argument checks the wall, stair and section tools share."""

from typing import Any

COMPASS = ("north", "south", "east", "west")


def is_pair(value: Any) -> bool:
    """[x, y]: two numbers, no bools."""
    return (
        isinstance(value, list)
        and len(value) == 2
        and all(isinstance(v, (int, float)) and not isinstance(v, bool) for v in value)
    )


def is_mm(value: Any) -> bool:
    """A length in mm: a number above 0, not a bool."""
    return isinstance(value, (int, float)) and not isinstance(value, bool) and value > 0
