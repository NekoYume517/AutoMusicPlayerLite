"""The eighth key reaches the double-high tonic without changing register."""
import os
os.environ['AMP_HEADLESS']='1'
import sys, tempfile, unittest
from dataclasses import replace
from pathlib import Path
sys.path.insert(0,str(Path(__file__).resolve().parents[2]))
from core.compiler import compile_score
from core.ir import from_storage
from core.profile import load_profiles
from core.parser import parse_jianpu
from core.score_io import export_json, import_json, note_id_to_midi
from core.midi_io import write_midi, read_midi
from core.preview_player import note_midi_number
from core.score_model import require_valid, Score
from core.transport import prepare_score

class TopNote(unittest.TestCase):
    def params(self):
        profile=next(p for p in load_profiles('profiles') if p.id=='delta_force_harmonica')
        return profile.build_compile_params(bpm=120,settle_ms=30,release_settle_ms=20,hold_ratio=1,max_hold_ms=None,gap_ms=0)
    def pitches(self, compiled):
        held=set(); output=[]
        keys={'Z':60,'X':62,'C':64,'V':65,'B':67,'N':69,'M':71,',':72}
        for event in compiled.events:
            if event.device=='mouse':
                if event.action=='down': held.add(event.key)
                else: held.discard(event.key)
            elif event.action=='down':
                output.append(keys[event.key]+12*('right' in held)-12*('left' in held)+('middle' in held))
        self.assertFalse(held)
        return output
    def test_top_tonic_uses_right_and_comma_without_lowering_following_note(self):
        notes=[{'notes':['top_1'],'dur':1},{'notes':['high_6'],'dur':1}]
        compiled=compile_score(from_storage(notes),self.params())
        self.assertEqual(self.pitches(compiled),[84,81])
        self.assertEqual(compiled.degradations,[])
    def test_high_tonic_has_both_equivalent_key_paths(self):
        notes=from_storage([{'notes':['high_1'],'dur':1}])
        params=self.params()
        self.assertEqual(self.pitches(compile_score(notes,params)),[72])
        params=replace(params,pitch_direct_overrides={})
        compiled=compile_score(notes,params)
        self.assertEqual(self.pitches(compiled),[72])
        self.assertIn(('mouse','right','down'),[(e.device,e.key,e.action) for e in compiled.events])
    def test_sharp_top_tonic_uses_both_modifiers_and_releases_them(self):
        compiled=compile_score(from_storage([{'notes':['top_1'],'dur':1,'semitone':1},{'notes':['high_1'],'dur':1}]),self.params())
        self.assertEqual(self.pitches(compiled),[85,72])
        self.assertEqual(compiled.degradations,[])
    def test_native_json_text_and_typed_score_keep_top_register(self):
        notes=[{'notes':['top_1'],'dur':.875},{'notes':['high_6'],'dur':.5}]
        parsed=parse_jianpu("1''{0.875} 6'_")
        self.assertEqual(parsed,notes)
        self.assertEqual(Score.from_storage('test',160,notes).to_storage(),notes)
        with tempfile.TemporaryDirectory() as folder:
            path=str(Path(folder)/'test.json'); export_json(path,{'name':'test','bpm_default':160,'notes':notes})
            self.assertEqual(import_json(path).notes,notes)
    def test_midi_export_reimport_and_preview_agree_on_c6(self):
        notes=[{'notes':['top_1'],'dur':1},{'notes':['high_6'],'dur':1}]
        with tempfile.TemporaryDirectory() as folder:
            path=str(Path(folder)/'test.mid'); write_midi(path,{'name':'test','bpm_default':120,'notes':notes})
            imported=read_midi(path)
            self.assertEqual(imported.notes,notes)
            self.assertFalse(any('octave' in getattr(d,'code','') for d in imported.degradations))
        self.assertEqual(note_midi_number('top_1'),84)
        self.assertEqual(note_id_to_midi('high_1'),72)
    def test_playback_preparation_keeps_top_without_extra_transposition(self):
        notes=[{'notes':['top_1'],'dur':1},{'notes':['high_6'],'dur':1}]
        self.assertEqual(prepare_score(notes).notes,notes)
        shifted=prepare_score(notes,transpose=-12)
        self.assertEqual([n['notes'] for n in shifted.notes],[['high_1'],['mid_6']])
    def test_unplayable_top_degree_is_rejected(self):
        with self.assertRaises(ValueError): require_valid([{'notes':['top_2'],'dur':1}])
        with self.assertRaises(ValueError): note_id_to_midi('top_2')

if __name__=='__main__': unittest.main()
