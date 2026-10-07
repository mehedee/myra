#!/usr/bin/env python3
"""Create original vector Myra Icon Composer documents and Dock renditions."""
import json
import pathlib
import subprocess

ROOT = pathlib.Path(__file__).resolve().parents[1]
ICON_TOOL = pathlib.Path('/Applications/Xcode.app/Contents/Applications/Icon Composer.app/Contents/Executables/ictool')
FAMILIES = {
    'signature': '<path d="M260 706V318h106l146 199 146-199h106v388H649V496L512 682 375 496v210z"/>',
    'cinema': '<rect x="220" y="280" width="584" height="464" rx="64" fill="none" stroke="white" stroke-width="56"/><path d="M456 384l210 128-210 128z"/><path d="M300 306v44m424-44v44M300 674v44m424-44v44" fill="none" stroke="white" stroke-width="36"/>',
    'orbit': '<ellipse cx="512" cy="512" rx="295" ry="180" transform="rotate(-35 512 512)" fill="none" stroke="white" stroke-width="40"/><path d="M458 384l200 128-200 128z"/><circle cx="746" cy="354" r="50"/>',
    'minimal': '<path d="M282 706V318l230 238 230-238v388h-92V543L512 686 374 543v163z"/>',
}
BASE = {'fill': {'linear-gradient': ['srgb:0.145,0.165,0.38,1', 'srgb:0.28,0.14,0.5,1']},
        'fill-specializations': [{'appearance': 'dark', 'value': {'linear-gradient': ['srgb:0.055,0.065,0.13,1','srgb:0.12,0.08,0.23,1']}}],
        'supported-platforms': {'squares': 'shared'}}
for family, mark in FAMILIES.items():
    document = ROOT / 'Resources' / 'MyraIcons' / (family + '.icon')
    (document / 'Assets').mkdir(parents=True, exist_ok=True)
    # Two actual vector layers let the system render depth, lighting, and glass.
    (document / 'Assets' / 'halo.svg').write_text('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1024 1024"><circle cx="512" cy="512" r="355" fill="white" opacity="0.10"/></svg>')
    (document / 'Assets' / 'mark.svg').write_text('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1024 1024" fill="white">' + mark + '</svg>')
    data = dict(BASE)
    for fill in [data['fill'], data['fill-specializations'][0]['value']]:
        fill['orientation'] = {'start':{'x':0.5,'y':0},'stop':{'x':0.5,'y':0.7}}
    data['groups'] = [{'layers': [
        {'image-name':'halo.svg','name':'Ambient halo','glass':True,'opacity':0.3},
        {'image-name':'mark.svg','name':'Myra '+family.title(),'glass':True,'fill':'automatic'}],
        'lighting':'individual','shadow':{'kind':'neutral','opacity':0.5},'specular':True,
        'translucency':{'enabled':True,'value':0.3}}]
    (document / 'icon.json').write_text(json.dumps(data, indent=2)+'\n')
    # These are explicitly rendered Dock images, not dynamically layered Finder icons.
    output_dir = ROOT / 'Sources' / 'Myra' / 'Resources' / 'MyraIcons'
    output_dir.mkdir(parents=True, exist_ok=True)
    for mode, rendition in [('light','Default'),('dark','Dark'),('glass','ClearLight')]:
        subprocess.run([str(ICON_TOOL),str(document),'--export-image','--output-file',str(output_dir/(family+'-'+mode+'.png')),
                        '--platform','macOS','--rendition',rendition,'--width','512','--height','512','--scale','1'],check=True)
