from pathlib import Path
repo = Path(SPECPATH).parent
a = Analysis([str(repo / 'native' / 'backend.py')], pathex=[str(repo)],
             binaries=[], datas=[(str(repo / 'profiles' / 'delta_force_harmonica.yaml'), 'profiles'), (str(repo / 'config.yaml'), '.')],
             hiddenimports=['pynput.keyboard._win32', 'pynput.mouse._win32'],
             hookspath=[], runtime_hooks=[], excludes=['PyQt6', 'PySide6', 'tkinter'], noarchive=False,
             module_collection_mode={'pynput': 'py'})
pyz = PYZ(a.pure)
exe = EXE(pyz, a.scripts, [], exclude_binaries=True, name='AmpEngine', debug=False,
          bootloader_ignore_signals=False, strip=False, upx=False, console=True,
          icon=str(repo / 'app.ico'))
coll = COLLECT(exe, a.binaries, a.datas, strip=False, upx=False, name='AmpEngine')
