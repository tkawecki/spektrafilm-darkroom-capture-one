"""Compare C++ fitted morph/compatible exposure warp with pinned Python."""
import argparse
import io as string_io
import json
from pathlib import Path
import subprocess
import sys

import numpy as np
from scipy.optimize import brentq
from scipy.special import ndtr
from reference import ROOT, io, morph

sys.path.insert(0, str(ROOT / 'upstream-ofx/tools'))
from ofx_stock_lists import PAPERS
from generate_print_curve_models import generate, DEFAULT_OUTPUT


def reference_warp(x, legacy, model, gamma, amount, profile_type):
    """Invert the pinned fitted CDF, evaluating its small tail stably."""
    output = np.empty_like(legacy)
    for c in range(3):
        base_params = morph._morph_channel_params(model, morph.PrintCurvesMorphParams(gamma_factor=gamma), c, profile_type)
        new_params = morph._morph_channel_params(model, morph.PrintCurvesMorphParams(gamma_factor=gamma, developer_exhaustion=amount), c, profile_type)

        def evaluate(value, params, complement):
            centers, amplitudes, sigmas, mix = params
            z = (value - centers) / sigmas
            if profile_type == 'positive':
                z = -z
            cdf = ndtr(-z if complement else z)
            with np.errstate(over='ignore', under='ignore'):
                inner = np.exp(-(z / morph._GUMBEL_WIDTH + morph._GUMBEL_LOCATION))
                gumbel = -np.expm1(-inner) if complement else np.exp(-inner)
            return float(np.sum(amplitudes * ((1 - mix) * cdf + mix * gumbel)))

        for i, raw in enumerate(x):
            lower = evaluate(raw, new_params, False)
            upper = evaluate(raw, new_params, True)
            complement = lower > upper
            target = upper if complement else lower
            sign = (-1 if profile_type == 'positive' else 1) * (-1 if complement else 1)
            residual = lambda value: evaluate(value, base_params, complement) - target
            if sign * residual(x[0]) >= 0:
                warped = x[0]
            elif sign * residual(x[-1]) <= 0:
                warped = x[-1]
            else:
                warped = brentq(residual, x[0], x[-1], xtol=1e-12)
            output[i, c] = np.interp(warped, x, legacy[:, c])
    output[0] = legacy[0]
    output[-1] = legacy[-1]
    return output


def run(probe):
    assert DEFAULT_OUTPUT.read_text(encoding='utf-8') == generate(), 'Fitted model header is stale'
    max_error = 0.0
    rows = []
    for index, stock in enumerate(PAPERS):
        profile = json.loads((ROOT / f'upstream-spektrafilm/src/spektrafilm/data/profiles/{stock}.json').read_text())
        model = io.DensityCurvesModel(**profile['data']['density_curves_model'])
        worst_step = 0.0
        minimum = float('inf')
        max_midgray_lut_error = 0.0
        for gamma in (0.070710678, 0.5, 1.0, 2.0, 2.82842712):
            gamma = float(np.float32(gamma))
            for amount in (0, 0.000001, 0.01, 0.25, 0.5, 1):
                amount = float(np.float32(amount))
                result = subprocess.run([str(probe), str(index), str(gamma), str(amount)], capture_output=True, text=True, check=True)
                data = np.loadtxt(string_io.StringIO(result.stdout), delimiter=',').reshape(-1, 3, 6)
                x = data[:, 0, 0]
                with np.errstate(over='ignore', under='ignore'):
                    expected = morph.apply_print_curves_morph(x, model, morph.PrintCurvesMorphParams(gamma_factor=gamma, developer_exhaustion=amount), profile_type=profile['info']['type'])
                    baseline = morph.apply_print_curves_morph(x, model, morph.PrintCurvesMorphParams(gamma_factor=gamma), profile_type=profile['info']['type'])
                max_error = max(max_error, float(np.max(np.abs(expected - data[:, :, 3]))))
                np.testing.assert_allclose(data[:, :, 3], expected, atol=2e-8, rtol=0, err_msg=f'{stock} g={gamma} e={amount}')
                np.testing.assert_allclose(data[:, :, 2], baseline, atol=2e-8, rtol=0)
                np.testing.assert_allclose(data[-2, :, 3], data[-2, :, 2], atol=2e-11, rtol=0, err_msg='D(0) moved')
                np.testing.assert_allclose(data[-1, :, 3], model.amplitudes.sum(axis=1), atol=1e-12, rtol=0, err_msg='Dmax moved')
                np.testing.assert_allclose(data[-3, :, 3], 0, atol=1e-12, rtol=0, err_msg='fitted minimum moved')
                actual = data[:-3, :, 5]
                legacy = data[:-3, :, 4]
                np.testing.assert_array_equal(actual[[0, -1]], legacy[[0, -1]])
                np.testing.assert_allclose(data[-3:, :, 5], data[-3:, :, 4], atol=5e-7, rtol=0, err_msg='legacy endpoints or D(0) moved')
                if amount == 0:
                    np.testing.assert_array_equal(actual, legacy)
                else:
                    warped = reference_warp(x[:-3], legacy, model, gamma, amount, profile['info']['type'])
                    np.testing.assert_allclose(actual, warped, atol=8e-7, rtol=0, err_msg='native exposure warp differs')
                assert np.all(actual.min(axis=0) >= legacy.min(axis=0) - 1e-6)
                assert np.all(actual.max(axis=0) <= legacy.max(axis=0) + 1e-6)
                # A monotone exposure warp cannot add variation to the legacy
                # curve. Its existing small tail ripples are deliberately kept.
                variation = np.maximum(-np.diff(actual, axis=0), 0).sum(axis=0)
                legacy_variation = np.maximum(-np.diff(legacy, axis=0), 0).sum(axis=0)
                assert np.all(variation <= legacy_variation + 2e-6), (stock, gamma, amount, variation, legacy_variation)
                max_midgray_lut_error = max(max_midgray_lut_error, max(abs(np.interp(0, x[:-3], actual[:, c]) - np.interp(0, x[:-3], legacy[:, c])) for c in range(3)))
                assert np.isfinite(actual).all()
                worst_step = min(worst_step, float(np.diff(actual, axis=0).min()))
                minimum = min(minimum, float(actual.min()))
        rows.append({'stock': stock, 'minimum_density': minimum, 'smallest_adjacent_step': worst_step, 'max_midgray_lut_error': max_midgray_lut_error})
    print(json.dumps({'cases': len(PAPERS) * 5 * 6, 'maximum_reference_error': max_error, 'legacy_warp_bounds': rows}, indent=2))


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--probe', type=Path, required=True)
    run(parser.parse_args().probe)
