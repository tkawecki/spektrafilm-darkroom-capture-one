"""Independent SciPy oracle for reference micro_contrast: sigma1.5/truncate3/reflect."""
import json
from pathlib import Path
import numpy as np
from scipy.ndimage import gaussian_filter
rng=np.random.default_rng(701)
cases=[]
for height,width in [(1,1),(1,17),(13,1),(9,13),(19,11)]:
    rgba=rng.uniform(-.2,1.5,(height,width,4)).astype('f4')
    rgba[...,3]=rng.uniform(0,1,(height,width)).astype('f4')
    luma=np.einsum('...c,c->...',rgba[...,:3].astype('f8'),[.2126,.7152,.0722])
    detail=luma-gaussian_filter(luma,1.5,truncate=3,mode='reflect')
    for amount in [-1,-.3,0,.4,1]:
        expected=rgba.copy()
        expected[...,:3]=(rgba[...,:3].astype('f8')+amount*detail[...,None]).astype('f4')
        cases.append(dict(width=width,height=height,amount=amount,source=rgba.ravel().tolist(),expected=expected.ravel().tolist()))
Path(__file__).with_name('fixtures.json').write_text(json.dumps(cases,separators=(',',':')))
print(f'{len(cases)} SciPy reference cases')
