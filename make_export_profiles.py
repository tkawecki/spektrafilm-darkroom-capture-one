"""Reproducible matrix/TRC ICC profiles for additional reference image-export pairs.

The four historical choices and their profiles are never rewritten. Log pairs
are intentionally metadata-only: no falsely normalized display ICC is emitted.
"""
import os
import hashlib,json,sys
from pathlib import Path
from struct import pack
import numpy as np
if os.environ.get('COLOUR_SCIENCE_PATH'): sys.path.append(os.environ['COLOUR_SCIENCE_PATH'])  # optional: another colour-science installation
import colour
from make_preview_profiles import build_profile, fixed16, D50

REGISTRY=[
    ('Srgb','srgb','sRGB',True),('AdobeRgb','adobe_rgb','Adobe RGB (1998)',True),
    ('ProPhotoRgb','prophoto','ProPhoto RGB',True),('LinearRec2020','linear_rec2020','ITU-R BT.2020',False),
    ('LinearSrgb','linear_srgb','sRGB',False),('LinearAdobeRgb','linear_adobe_rgb','Adobe RGB (1998)',False),
    ('LinearProPhotoRgb','linear_prophoto','ProPhoto RGB',False),
    ('DisplayP3','display_p3','Display P3',True),('LinearDisplayP3','linear_display_p3','Display P3',False),
    ('DciP3','dci_p3','DCI-P3',True),('LinearDciP3','linear_dci_p3','DCI-P3',False),
    ('Rec2020','rec2020','ITU-R BT.2020',True),('Aces2065','aces2065_1','ACES2065-1',False),
    ('DavinciWideGamut','davinci_wide_gamut','DaVinci Wide Gamut',True),
    ('LinearDavinciWideGamut','linear_davinci_wide_gamut','DaVinci Wide Gamut',False),
    ('VGamut','v_gamut','V-Gamut',True),('LinearVGamut','linear_v_gamut','V-Gamut',False)]
SPACES={name:colour.RGB_COLOURSPACES[name].copy() for _,_,name,_ in REGISTRY}
SPACES['ProPhoto RGB'].whitepoint=colour.XYZ_to_xy(D50)
for space in SPACES.values():space.use_derived_transformation_matrices(True)
def decode(values,name,encoded):
    if not encoded:return np.asarray(values)
    cs=SPACES[name]
    return np.sign(values)*cs.cctf_decoding(np.abs(values)) if name in ('Adobe RGB (1998)','DCI-P3') else cs.cctf_decoding(values)
def encode(values,name,encoded):
    if not encoded:return np.asarray(values)
    cs=SPACES[name]
    return np.sign(values)*cs.cctf_encoding(np.abs(values)) if name in ('Adobe RGB (1998)','DCI-P3') else cs.cctf_encoding(values)
def matrix(source,target):return colour.matrix_RGB_to_RGB(SPACES[source],SPACES[target],chromatic_adaptation_transform='Bradford')
def curve(name,encoded):
    if not encoded:return b'curv\0\0\0\0'+pack('>I',0)
    if name=='Display P3':p=(2.4,1/1.055,.055/1.055,1/12.92,.04045)
    elif name=='DCI-P3':return b'para\0\0\0\0'+pack('>HH',0,0)+fixed16((2.6,))
    elif name=='ITU-R BT.2020':
        # The nominal rounded BT.2020 inverse has a tiny downward jump at
        # OETF(.018). An ICC TRC must be monotone. Preserve its upper envelope
        # in a 4096-entry table; maximum deviation is ~5.5e-5 linear near the
        # discontinuity. Pixel conversion itself uses the exact reference.
        values=decode(np.linspace(0,1,4096),name,True)
        codes=np.rint(np.maximum.accumulate(values)*65535).astype(np.uint16)
        return b'curv\0\0\0\0'+pack('>I',len(codes))+codes.astype('>u2').tobytes()
    else:raise ValueError(name)
    return b'para\0\0\0\0'+pack('>HH',3,0)+fixed16(p)
def main():
    directory=Path(__file__).resolve().parent/'profiles'/'export';directory.mkdir(parents=True,exist_ok=True)
    entries=[]
    for index,(enum,id,name,encoded) in enumerate(REGISTRY):
        entry=dict(enum=enum,id=id,colorSpace=name,encoded=encoded,
                   fromLinearSrgb=matrix('sRGB',name).tolist(),toLinearSrgb=matrix(name,'sRGB').tolist(),
                   icc=None,sha256=None)
        if index>=4 and not (encoded and name in ('DaVinci Wide Gamut','V-Gamut')):
            cs=SPACES[name];white=colour.xy_to_XYZ(cs.whitepoint)
            adaptation=colour.adaptation.matrix_chromatic_adaptation_VonKries(white,D50,transform='Bradford')
            colors=adaptation@cs.matrix_RGB_to_XYZ
            data=build_profile('Spektrafilm '+name+('' if encoded else ' Linear'),colors.T,curve(name,encoded),adaptation.ravel())
            filename=id+'.icc';(directory/filename).write_bytes(data)
            entry.update(icc=filename,sha256=hashlib.sha256(data).hexdigest().upper())
        entries.append(entry)
    manifest=dict(colourVersion=colour.__version__,description='New ICC matrix/TRC profiles; old four profiles preserved elsewhere; log pairs metadata-only.',spaces=entries)
    (directory/'spaces.json').write_text(json.dumps(manifest,indent=2)+'\n',encoding='utf-8')
    print(f'{len(entries)} export pairs, {sum(e["icc"] is not None for e in entries)} new ICC profiles; historical four untouched')
if __name__=='__main__':main()
