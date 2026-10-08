"""Import beat quantization, with cumulative boundaries to avoid timing drift."""
from copy import deepcopy
from core.score_model import MAX_DUR_BEATS

QUANTUM = .125
def normalize_durations(notes, *, quantize=False):
    result = []
    cursor = boundary = 0.0
    for source in notes:
        item = deepcopy(source)
        value = float(item["dur"])
        if quantize:
            cursor += value
            end = round(cursor / QUANTUM) * QUANTUM
            value = end - boundary
            boundary = end
            if value <= 0:
                continue
        else:
            nearest = round(value / QUANTUM) * QUANTUM
            if nearest > 0 and abs(nearest - value) <= .002:
                value = nearest
            else:
                value = round(value, 9)
        while value > MAX_DUR_BEATS:
            fragment = deepcopy(item); fragment["dur"] = MAX_DUR_BEATS; result.append(fragment); value -= MAX_DUR_BEATS
        item["dur"] = value
        result.append(item)
    return result
