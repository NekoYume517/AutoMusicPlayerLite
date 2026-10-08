"""Stage a self-contained desktop MSIX; requires the already prepared release folder."""
import argparse
import copy
import json
from pathlib import Path
import shutil
import xml.etree.ElementTree as ET

p=argparse.ArgumentParser()
p.add_argument('--publish',type=Path,required=True)
p.add_argument('--stage',type=Path,required=True)
p.add_argument('--nuget',type=Path,required=True)
p.add_argument('--identity',required=True)
p.add_argument('--display-name',required=True)
p.add_argument('--version',required=True)
a=p.parse_args()
assert not a.stage.exists(), 'Use a fresh staging directory'
shutil.copytree(a.publish,a.stage)
foundation='http://schemas.microsoft.com/appx/manifest/foundation/windows10'
uap='http://schemas.microsoft.com/appx/manifest/uap/windows10'
rescap='http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities'
desktop6='http://schemas.microsoft.com/appx/manifest/desktop/windows10/6'
ET.register_namespace('',foundation); ET.register_namespace('uap',uap)
ET.register_namespace('rescap',rescap); ET.register_namespace('desktop6',desktop6)
root=ET.fromstring(f'''<Package xmlns="{foundation}" xmlns:uap="{uap}" xmlns:rescap="{rescap}" xmlns:desktop6="{desktop6}" IgnorableNamespaces="uap rescap desktop6">
  <Identity Name="{a.identity}" Publisher="CN=NekoYume517" Version="{a.version}" ProcessorArchitecture="x64" />
  <Properties><DisplayName>{a.display_name}</DisplayName><PublisherDisplayName>NekoYume517</PublisherDisplayName><Logo>Assets\\StoreLogo.png</Logo><desktop6:FileSystemWriteVirtualization>disabled</desktop6:FileSystemWriteVirtualization></Properties>
  <Dependencies><TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.19041.0" MaxVersionTested="10.0.26100.0" /></Dependencies>
  <Resources><Resource Language="zh-cn" /><Resource Language="en-us" /></Resources>
  <Applications><Application Id="App" Executable="AutoMusicPlayerLite.exe" EntryPoint="Windows.FullTrustApplication"><uap:VisualElements DisplayName="{a.display_name}" Description="Native harmonica player" BackgroundColor="transparent" Square150x150Logo="Assets\\Square150x150Logo.png" Square44x44Logo="Assets\\Square44x44Logo.png" /></Application></Applications>
  <Extensions />
  <Capabilities><rescap:Capability Name="runFullTrust" /><rescap:Capability Name="allowElevation" /><rescap:Capability Name="unvirtualizedResources" /></Capabilities>
</Package>''')
extensions=root.find('{'+foundation+'}Extensions')
assets=Path(__file__).parent/'WinUI/obj/project.assets.json'
libraries=json.loads(assets.read_text(encoding='utf-8'))['libraries']
registered=set()
for identity,metadata in libraries.items():
    if not identity.casefold().startswith('microsoft.windowsappsdk') or 'path' not in metadata: continue
    for resource in (a.nuget/metadata['path']/'build/native').glob('*WinRTClassRegistrations.xml'):
        for extension in ET.parse(resource).getroot():
            server=extension.find('{'+foundation+'}InProcessServer')
            if server is None: continue
            dll=server.find('{'+foundation+'}Path')
            if dll is None or not (a.stage/dll.text).is_file(): continue
            node=copy.deepcopy(extension)
            container=node.find('{'+foundation+'}InProcessServer')
            for entry in list(container):
                key=entry.get('ActivatableClassId')
                if key is not None:
                    if key in registered: container.remove(entry)
                    else: registered.add(key)
            if any(e.get('ActivatableClassId') for e in container): extensions.append(node)
ET.indent(root)
ET.ElementTree(root).write(a.stage/'AppxManifest.xml',encoding='utf-8',xml_declaration=True)
print(json.dumps({'identity':a.identity,'version':a.version,'registered_winrt_classes':len(registered),'files':sum(f.is_file() for f in a.stage.rglob('*'))}))
