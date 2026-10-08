"""音符 -> 键盘按键映射。"""

OCTAVES = ("high", "mid", "low")
NOTES = tuple(range(1, 8))


class KeyMap:
    def __init__(self, mapping: dict):
        """mapping: {"high": [Q..U], "mid": [A..J], "low": [Z..M]}"""
        self._mapping = {octave: list(keys) for octave, keys in mapping.items()}
        for octave in OCTAVES:
            if len(self._mapping.get(octave, [])) != 7:
                raise ValueError(f"keymap.{octave} 必须包含 7 个按键")

    def key_for(self, note_id: str) -> str | None:
        """note_id 形如 "high_1" / "mid_3" / "low_5",返回键盘按键。"""
        if "_" not in note_id:
            return None
        octave, num = note_id.split("_", 1)
        keys = self._mapping.get(octave)
        if not keys:
            return None
        try:
            idx = int(num) - 1
        except ValueError:
            return None
        if not 0 <= idx < 7:
            return None
        return keys[idx]