"""CPU-only Monte Carlo of the Grain Synthesis pixel estimator (SpektraGrain.comp grainSynthesisLayerFromDensity):
Boolean model of disks (radius r um) with intensity lambda = D ln10 / (pi r^2), N sample points per pixel drawn
from a Gaussian observation window (sigma um), coverage c = covered/N clamped to 1-eps, density -log10(1-c).
Reports E[density] - D (density bias) and E[1-c] / 10^-D (transmission ratio) for a single layer."""
import sys
import numpy as np
rng = np.random.default_rng(1)
def simulate(D, r, sigma, N, eps, windows=1500):
    lam = D * np.log(10) / (np.pi * r * r)
    half = 4 * sigma + 2 * r                      # square around the window centre
    area = (2 * half) ** 2
    dens, trans = [], []
    for _ in range(windows):
        n = rng.poisson(lam * area)
        if n > 200000: n = 200000
        centres = rng.uniform(-half, half, size=(n, 2))
        pts = rng.normal(0, sigma, size=(N, 2))
        # covered if any grain centre within r
        if n == 0:
            cov = 0
        else:
            d2 = ((pts[:, None, :] - centres[None, :, :]) ** 2).sum(-1) if n * N <= 4_000_000 else None
            if d2 is None:
                covered = np.zeros(N, bool)
                for s in range(0, n, 20000):
                    covered |= (((pts[:, None, :] - centres[None, s:s+20000, :]) ** 2).sum(-1) <= r * r).any(1)
                cov = covered.mean()
            else:
                cov = (d2 <= r * r).any(1).mean()
        c = min(cov, 1 - eps)
        dens.append(-np.log10(max(1 - c, eps))); trans.append(1 - c)
    return np.mean(dens) - D, np.mean(trans) / 10 ** -D
r_scale = {'R': 1.2, 'G': 1.0, 'B': 2.5}
print("channel  D     density bias   transmission ratio   (N=128, sigma=1um, eps=1e-4, r=0.25um*scale)")
for ch, s in r_scale.items():
    for D in (0.25, 0.5, 1.0, 1.5, 2.0):
        b, t = simulate(D, 0.25 * s, 1.0, 128, 1e-4)
        print(f"  {ch}     {D:4.2f}   {b:+.4f}        {t:.4f}")
