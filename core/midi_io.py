"""MIDI file interchange; parsing and exporting never send device input."""
from collections import defaultdict, deque
from pathlib import Path
import mido
from core.source_score import SourceNote, SourceSong, AdaptOptions, adapt_source_song
from core.score_model import require_valid
from core.source_score import MAX_SOURCE_NOTES, MAX_SOURCE_TRACKS

def read_midi(path):
    if Path(path).stat().st_size > 10 * 1024 * 1024:
        raise ValueError("MIDI 文件不能超过 10 MB")
    try:
        midi = mido.MidiFile(path, charset="utf-8")
    except UnicodeDecodeError:
        midi = mido.MidiFile(path, charset="latin1")
    if midi.type == 2:
        raise ValueError("暂不支持 MIDI Type 2 的独立时间轴，请先另存为 Type 0/1")
    if midi.ticks_per_beat <= 0:
        raise ValueError("不支持 SMPTE 时间格式，请先转换为按拍计时的 MIDI")
    if len(midi.tracks) > MAX_SOURCE_TRACKS:
        raise ValueError("MIDI 音轨过多")
    events, names = [], {}
    for index, track in enumerate(midi.tracks):
        tick = 0
        names[index] = track.name or f"音轨 {index + 1}"
        for order, message in enumerate(track):
            tick += message.time
            events.append((tick, index, order, message))
    if len(events) > 300_000:
        raise ValueError("MIDI 事件超过 300000 个")
    events.sort(key=lambda e: e[:3])
    active, sustained = defaultdict(deque), defaultdict(list)
    pedal = defaultdict(bool)
    notes, warnings = [], []
    wall = 0.0; previous_tick = 0; tempo = 500000; initial_tempo = 500000; tempo_sections = 1
    drums = unmatched = dangling = 0
    def append(start, end, pitch, track):
        if end > start:
            notes.append(SourceNote(start, end, pitch, track))
            if len(notes) > MAX_SOURCE_NOTES:
                raise ValueError("MIDI 音符超过 100000 个")
    def flush_sustain(track, channel):
        for start, pitch in sustained.pop((track, channel), []): append(start, wall, pitch, track)
    for tick, track, _, msg in events:
        wall += mido.tick2second(tick - previous_tick, midi.ticks_per_beat, tempo)
        previous_tick = tick
        if msg.type == "set_tempo":
            if tick > 0 and tempo != msg.tempo: tempo_sections += 1
            tempo = msg.tempo
            if tick == 0: initial_tempo = tempo
        if msg.is_meta or not hasattr(msg, "channel"):
            continue
        channel = msg.channel
        if channel == 9:
            if msg.type == "note_on" and msg.velocity: drums += 1
            continue
        if msg.type == "control_change":
            if msg.control == 64:
                pedal[track, channel] = msg.value >= 64
                if not pedal[track, channel]: flush_sustain(track, channel)
            elif msg.control in (120, 123):
                for key in list(active):
                    if key[:2] == (track, channel):
                        for start in active.pop(key): append(start, wall, key[2], track)
                flush_sustain(track, channel)
            continue
        if msg.type not in ("note_on", "note_off"):
            continue
        key = (track, channel, msg.note)
        if msg.type == "note_on" and msg.velocity > 0:
            active[key].append(wall)
        elif active[key]:
            start = active[key].popleft()
            if pedal[track, channel]: sustained[track, channel].append((start, msg.note))
            else: append(start, wall, msg.note, track)
        else:
            unmatched += 1
    for (track, channel, pitch), starts in active.items():
        for start in starts: append(start, wall, pitch, track); dangling += 1
    for track, channel in list(sustained): flush_sustain(track, channel)
    if not notes:
        raise ValueError("MIDI 没有可导入的旋律音符（鼓轨不会作为旋律）")
    bpm = min(300, max(30, round(mido.tempo2bpm(initial_tempo))))
    if drums: warnings.append(f"已忽略鼓轨的 {drums} 个打击乐音符")
    if unmatched: warnings.append(f"已忽略 {unmatched} 个未配对的松键事件")
    if dangling: warnings.append(f"有 {dangling} 个缺少松键的音符按文件末尾结束")
    if round(mido.tempo2bpm(initial_tempo)) != bpm: warnings.append(f"来源速度已换算到支持的 {bpm} BPM")
    song = SourceSong(Path(path).stem, notes, names, wall, bpm, tempo_sections)
    adapted = adapt_source_song(song, AdaptOptions(style="preserve", track="auto"))
    if len({n.track for n in notes}) > 1:
        warnings.append(f"已自动选择旋律音轨：{names[adapted.selected_track]}")
    from core.score_io import _from_adapted
    return _from_adapted(adapted, kind="midi", source_format="midi", extra_warnings=warnings)

def write_midi(path, score):
    from core.score_io import note_id_to_midi
    notes = score["notes"]
    bpm = score.get("bpm_default", 100)
    require_valid(notes, bpm=bpm)
    midi = mido.MidiFile(type=0, ticks_per_beat=960, charset="utf-8")
    track = mido.MidiTrack(); midi.tracks.append(track)
    track.append(mido.MetaMessage("track_name", name=score.get("name", "乐谱"), time=0))
    track.append(mido.MetaMessage("set_tempo", tempo=mido.bpm2tempo(bpm), time=0))
    track.append(mido.Message("program_change", channel=0, program=0, time=0))
    events = []
    cursor = 0.0
    for note in notes:
        start = round(cursor * midi.ticks_per_beat)
        cursor += note["dur"]
        end = max(start + 1, round(cursor * midi.ticks_per_beat))
        for pitch in dict.fromkeys(note["notes"]):
            number = note_id_to_midi(pitch, note.get("semitone", 0))
            events.append((start, 1, number, mido.Message("note_on", channel=0, note=number, velocity=90)))
            events.append((end, 0, number, mido.Message("note_off", channel=0, note=number, velocity=0)))
    events.sort(key=lambda e: e[:3])
    previous = 0
    for tick, _, _, message in events:
        track.append(message.copy(time=tick - previous)); previous = tick
    total = max(previous, round(cursor * midi.ticks_per_beat))
    track.append(mido.MetaMessage("end_of_track", time=total - previous))
    midi.save(path)
