from __future__ import annotations

import datetime as dt

EVENTS = (
    {
        "id": "newyear",
        "title": "Новый год",
        "start": (12, 20),
        "end": (1, 14),
        "accent": "#5aa9ff",
        "shape": "flake",
        "count": 26,
    },
    {
        "id": "halloween",
        "title": "Хэллоуин",
        "start": (10, 25),
        "end": (10, 31),
        "accent": "#ff8a2b",
        "shape": "pumpkin",
        "count": 14,
    },
    {
        "id": "feb23",
        "title": "23 февраля",
        "start": (2, 17),
        "end": (2, 24),
        "accent": "#7fa86b",
        "shape": "star",
        "count": 16,
    },
    {
        "id": "march8",
        "title": "8 марта",
        "start": (3, 1),
        "end": (3, 9),
        "accent": "#ff7ab6",
        "shape": "petal",
        "count": 20,
    },
    {
        "id": "september1",
        "title": "1 сентября",
        "start": (8, 25),
        "end": (9, 3),
        "accent": "#c9a227",
        "shape": "leaf",
        "count": 16,
    },
)


def _in_window(day: dt.date, start: tuple[int, int], end: tuple[int, int]) -> bool:
    today = (day.month, day.day)
    if start <= end:
        return start <= today <= end
    return today >= start or today <= end


def active(now: dt.date | None = None) -> dict | None:
    """Праздник, окно которого попадает на указанную дату (по умолчанию — сегодня)."""
    day = now or dt.date.today()
    for event in EVENTS:
        if _in_window(day, event["start"], event["end"]):
            return {key: value for key, value in event.items() if key not in ("start", "end")}
    return None
