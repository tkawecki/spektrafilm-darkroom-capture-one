"""Independent LittleCMS consumer check, via Pillow RGB8 -> Lab8 transforms.

The double-precision PCS oracle and RGB16 container tests remain the precision
checks. This deliberately exercises a separate CMM's interpretation of each
new ICC; no monitor assignment or application settings are changed.
"""
import io,itertools,json,sys
from pathlib import Path
import numpy as np
from PIL import Image,ImageCms
sys.path.insert(0,str(Path(__file__).resolve().parents[2]))
from make_export_profiles import SPACES,colour,decode,encode,matrix,D50
root=Path(__file__).resolve().parents[2]
manifest=json.loads((root/'profiles/export/spaces.json').read_text())
patches=np.asarray(list(itertools.product(np.linspace(.08,.9,7),repeat=3)))
report=[]
for entry in manifest['spaces']:
    if entry['icc'] is None:continue
    name=entry['colorSpace'];encoded=entry['encoded'];cs=SPACES[name]
    linear=decode(patches,'sRGB',True)@matrix('sRGB',name).T
    codes=np.rint(np.clip(encode(linear,name,encoded),0,1)*255).astype(np.uint8)
    image=Image.fromarray(codes.reshape(1,-1,3),'RGB')
    profile=ImageCms.ImageCmsProfile(str(root/'profiles/export'/entry['icc']))
    destination=ImageCms.createProfile('LAB',5000)
    transform=ImageCms.buildTransformFromOpenProfiles(profile,destination,'RGB','LAB',renderingIntent=ImageCms.Intent.RELATIVE_COLORIMETRIC,flags=ImageCms.Flags.NOOPTIMIZE)
    actual=np.asarray(ImageCms.applyTransform(image,transform)).reshape(-1,3).astype(int)
    # Pillow's ndarray interface exposes signed Lab a/b bytes modulo 256;
    # getpixel() uses the usual unsigned offset-by-128 representation.
    actual[:,1:]=(actual[:,1:]+128)%256
    adapt=colour.adaptation.matrix_chromatic_adaptation_VonKries(colour.xy_to_XYZ(cs.whitepoint),D50,transform='Bradford')
    xyz=decode(codes.astype(float)/255,name,encoded)@(adapt@cs.matrix_RGB_to_XYZ).T
    lab=colour.XYZ_to_Lab(xyz,illuminant=colour.XYZ_to_xy(D50))
    expected=np.rint(np.clip(lab*np.array([2.55,1,1])+np.array([0,128,128]),0,255)).astype(int)
    error=np.abs(actual-expected);maximum=int(error.max())
    assert maximum<=2,(entry['id'],maximum,codes[np.unravel_index(error.argmax(),error.shape)[0]],actual[np.unravel_index(error.argmax(),error.shape)[0]],expected[np.unravel_index(error.argmax(),error.shape)[0]])
    report.append(dict(id=entry['id'],patches=len(codes),maxLab8CodeError=maximum))
result=dict(pillow=Image.__version__,littleCms=ImageCms.core.littlecms_version,colour=colour.__version__,results=report)
output=root/'artifacts/full-function-20261116/export-color-pairs-lcms.json';output.write_text(json.dumps(result,indent=2)+'\n')
print(f'PASS {len(report)} ICC profiles, {len(report)*len(patches)} RGB patches through LittleCMS; max Lab8 code error {max(e["maxLab8CodeError"] for e in report)}; {output}')
