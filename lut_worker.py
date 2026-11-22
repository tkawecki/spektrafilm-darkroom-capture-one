"""Export sRGB color LUTs over JSON lines using the spektrafilm reference runtime."""

from contextlib import redirect_stdout
from dataclasses import dataclass, fields
from functools import lru_cache
from io import StringIO
import json
import math
import os
from pathlib import Path
import sys
from time import perf_counter

ROOT = Path(__file__).resolve().parent
UPSTREAM = ROOT / "upstream-spektrafilm" / "src"
sys.path.insert(0, str(UPSTREAM))
CACHE = Path(os.getenv("LOCALAPPDATA", ROOT)) / "FilmToolsForCaptureOne" / "Python cache"
os.environ.setdefault("MPLCONFIGDIR", str(CACHE / "matplotlib"))
os.environ.setdefault("NUMBA_CACHE_DIR", str(CACHE / "numba"))

# Reserve stdout for the IPC response, including during upstream initialization.
with redirect_stdout(sys.stderr):
    import colour
    import numpy as np
    from scipy.special import expit
    from spektrafilm import digest_params, init_params
    from spektrafilm.config import STANDARD_OBSERVER_CMFS
    from spektrafilm.model.develop import compute_density_spectral
    from spektrafilm.model.illuminants import standard_illuminant
    from spektrafilm.runtime.pipeline import SimulationPipeline
    from spektrafilm.runtime.topology import Tap
    from spektrafilm.utils.conversions import density_to_light
    from spektrafilm.utils.gamut_compression import compress_rgb
    from spektrafilm.utils.morph_curves import PrintCurvesMorphParams



WORKING_SPACE = colour.RGB_COLOURSPACES["ProPhoto RGB"]
D50 = WORKING_SPACE.whitepoint
PROFILE_DIRECTORY = UPSTREAM / "spektrafilm" / "data" / "profiles"
DEFAULT_FILM = "kodak_portra_400"
DEFAULT_PAPER = "kodak_portra_endura"
MODES = {"print": "Print Simulation", "scan": "Scan Film"}
SPECTRAL_METHODS = {"hanatos2025": "Hanatos 2025", "mallett2019": "Mallett 2019"}
PUSH_PULL_MODES = {"standard": "Standard", "experimental": "Experimental"}


@dataclass(frozen=True)
class FilmControls:
    exposure: float = 0
    printer_exposure: float = 0
    film_gamma: float = 1
    print_gamma: float = 1
    film_push_pull: float = 0
    print_push_pull: float = 0
    negative_bleach_bypass: float = 0
    print_bleach_bypass: float = 0
    negative_leuco_cyan: float = 1
    cyan_filter: float = 0
    magenta_filter: float = 0
    yellow_filter: float = 0
    preflash_exposure: float = 0
    preflash_magenta: float = 0
    preflash_yellow: float = 0
    dir_amount: float = 1
    dir_same_layer: float = 1
    dir_interlayer: float = 1
    print_shadows: float = 0
    print_highlights: float = 0
    print_exhaustion: float = 0
    uv_cutoff: float = 380
    ir_cutoff: float = 780
    mode: str = "print"
    spectral_method: str = "hanatos2025"
    film_push_pull_mode: str = "standard"
    scan_invert: bool = False


LIMITS = {
    "exposure": (-8, 8), "printer_exposure": (-5, 5),
    "film_gamma": (0.1, 2), "print_gamma": (0.1, 2),
    "film_push_pull": (-2, 2), "print_push_pull": (-2, 2),
    "negative_bleach_bypass": (0, 1), "print_bleach_bypass": (0, 1),
    "negative_leuco_cyan": (0, 2), "cyan_filter": (0, 120),
    "magenta_filter": (-60, 60), "yellow_filter": (-60, 60),
    "preflash_exposure": (0, 1), "preflash_magenta": (-60, 60), "preflash_yellow": (-60, 60),
    "dir_amount": (0, 2), "dir_same_layer": (0, 2), "dir_interlayer": (0, 2),
    "print_shadows": (-1, 1), "print_highlights": (-1, 1), "print_exhaustion": (0, 1),
    "uv_cutoff": (380, 450), "ir_cutoff": (600, 780),
}


def read_controls(request: dict) -> FilmControls:
    values = {
        field.name: read_slider(request, field.name, field.default, *LIMITS[field.name])
        for field in fields(FilmControls) if field.name in LIMITS
    }
    for key, choices, default in (
        ("mode", MODES, "print"),
        ("spectral_method", SPECTRAL_METHODS, "hanatos2025"),
        ("film_push_pull_mode", PUSH_PULL_MODES, "standard"),
    ):
        value = request.get(key, default)
        if not isinstance(value, str) or value not in choices:
            raise ValueError(f"Unsupported {key}.")
        values[key] = value
    scan_invert = request.get("scan_invert", False)
    if not isinstance(scan_invert, bool):
        raise ValueError("scan_invert must be true or false.")
    return FilmControls(**values, scan_invert=scan_invert)


def load_stocks() -> tuple[dict[str, dict], dict[str, dict]]:
    films, papers = {}, {}
    for path in sorted(PROFILE_DIRECTORY.glob("*.json")):
        info = json.loads(path.read_text(encoding="utf-8"))["info"]
        if info.get("channel_model") != "color":
            continue
        stock = {"id": path.stem, "label": info["name"], "kind": info["type"], "channel_model": "color"}
        if info["stage"] == "filming":
            films[path.stem] = stock
        elif info["stage"] == "printing":
            papers[path.stem] = stock
    return films, papers


@lru_cache(maxsize=3)
def get_pipeline(film: str, paper: str, mode: str, spectral_method: str, uv: float, ir: float):
    params = init_params(film_profile=film, print_profile=paper)
    params.debug.lut_mode = True
    params.io.input_color_space = WORKING_SPACE.name
    params.io.output_color_space = WORKING_SPACE.name
    params.io.input_cctf_decoding = False
    params.io.output_cctf_encoding = False
    params.io.scan_film = mode == "scan"
    params.settings.rgb_to_raw_method = spectral_method
    params.camera.filter_uv = (float(uv > 380), uv, 8.0)
    params.camera.filter_ir = (float(ir < 780), ir, 15.0)
    pipeline = SimulationPipeline(digest_params(params))
    return pipeline, pipeline.enlarger.c_filter_neutral


def film_development_gamma(stops: float) -> float:
    # SpektraFilm OFX standard push/pull interpolates ECN-2 processing ratios.
    if stops < 0:
        return (150 / 180) ** -stops
    if stops <= 1:
        return (220 / 180) ** stops
    return (220 / 180) * (280 / 220) ** (stops - 1)


def experimental_develop(pipeline, log_raw: np.ndarray, stops: float):
    # OFX experimental development remaps log exposure and density by layer.
    seconds = np.interp(stops, [-2, -1, 0, 1, 2], [150, 150, 180, 220, 280])
    activity = float(np.log(seconds / 180))
    signed_squared = activity * abs(activity)
    speed_gain = np.interp(stops, [-2, -1, 0, 1, 2], [-0.2, -0.2, 0, 0.33, 0.5])
    shifted = log_raw - (stops - speed_gain) * math.log10(2)
    couplings = np.array([[0, -0.015, 0.015], [0.015, 0, -0.015], [-0.015, 0.015, 0]])
    shifted += activity * ((shifted - shifted.mean(axis=-1, keepdims=True)) @ couplings)

    toe = 1 - expit((shifted + 2) / 0.5)
    shoulder = expit(shifted / 0.5)
    mid = np.maximum(1 - toe - shoulder, 0)
    mid_shift = np.array([0.25, 0.28, 0.31]) * activity + np.array([0.04, 0.06, 0.08]) * signed_squared
    shoulder_shift = np.array([0.32, 0.38, 0.45]) * activity + np.array([0.08, 0.12, 0.16]) * signed_squared
    warped = shifted + mid * mid_shift + shoulder * shoulder_shift
    density = pipeline.process(warped, inject=Tap.LOG_E_FILM, collect=Tap.CMY_FILM)

    toe = 1 - expit((warped + 2) / 0.5)
    shoulder = expit(warped / 0.5)
    mid = np.maximum(1 - toe - shoulder, 0)
    build = np.array([1.05, 0.95, 1.00]) * activity - np.array([0.90, 0.82, 0.86]) * signed_squared
    return density * np.clip(1 + build * (0.12 * toe + mid + shoulder), 0.35, 2)


def develop_film(pipeline, image: np.ndarray, controls: FilmControls):
    if controls.film_push_pull_mode == "experimental" and controls.film_push_pull != 0:
        log_raw = pipeline.process(image, collect=Tap.LOG_E_FILM)
        return experimental_develop(pipeline, log_raw, controls.film_push_pull)
    return pipeline.process(image, collect=Tap.CMY_FILM)


def silver_and_dye(density: np.ndarray, profile, amount: float, *, print_stage: bool, leuco: float = 0):
    # The OFX models retained silver as wavelength-independent optical density.
    if amount == 0:
        return density, 0
    maximum = np.maximum(np.nanmax(profile.data.density_curves, axis=0), 1e-6)
    positive_film = not print_stage and profile.is_positive
    developed = np.maximum(maximum - density if positive_film else density, 0)
    shoulder = 0.65 if print_stage else 0.85
    weight = amount * np.mean(developed / (developed + shoulder), axis=-1, keepdims=True)
    dye = density + weight * (np.max(density, axis=-1, keepdims=True) - density)
    if not print_stage and not positive_film and leuco > 0:
        cyan_loss = 0.30 * leuco * amount * np.clip(density[..., 0] / maximum[0], 0, 1)
        dye[..., 0] = np.maximum(dye[..., 0] - cyan_loss, 0)
    return dye, (0.36 if print_stage else 0.22) * weight


def expose_print_with_bypass(pipeline, density: np.ndarray, controls: FilmControls):
    stage = pipeline._printing_stage
    service = pipeline._enlarger_service
    lamp = standard_illuminant(pipeline.enlarger.illuminant)
    light = service.enlarger_filtered_illuminant(lamp)
    sensitivity = np.nan_to_num(10 ** pipeline.print.data.log_sensitivity)
    film = pipeline.film

    def paper_exposure(cmy):
        dye, silver = silver_and_dye(cmy, film, controls.negative_bleach_bypass,
                                     print_stage=False, leuco=controls.negative_leuco_cyan)
        spectrum = compute_density_spectral(film.data.channel_density, dye, film.data.base_density) + silver
        return density_to_light(spectrum, light) @ sensitivity

    raw = paper_exposure(density)
    middle_gray = np.full((1, 1, 3), 0.184)
    gray_density = develop_film(pipeline, middle_gray, controls)
    gray_exposure = np.maximum(paper_exposure(gray_density), 1e-10)
    raw /= np.exp(np.mean(np.log(gray_exposure), axis=-1, keepdims=True))
    raw += stage._compute_raw_preflash(lamp, sensitivity)
    return np.log10(np.maximum(raw * 2**controls.printer_exposure, 0) + 1e-10)


def scan_medium(pipeline, density: np.ndarray, controls: FilmControls):
    print_stage = controls.mode == "print"
    profile = pipeline.print if print_stage else pipeline.film
    amount = controls.print_bleach_bypass if print_stage else controls.negative_bleach_bypass
    invert = not print_stage and controls.scan_invert
    if amount == 0 and not invert:
        return pipeline.process(density, inject=Tap.CMY_PRINT if print_stage else Tap.CMY_FILM)

    illuminant = standard_illuminant(profile.info.viewing_illuminant)
    cmf = STANDARD_OBSERVER_CMFS[:]
    normalized_cmf = cmf * illuminant[:, None] / np.dot(illuminant, cmf[:, 1])
    illuminant_xy = colour.XYZ_to_xy(normalized_cmf.sum(axis=0))

    def scan(cmy, silver=0):
        spectrum = compute_density_spectral(profile.data.channel_density, cmy, profile.data.base_density) + silver
        xyz = density_to_light(spectrum, 1) @ normalized_cmf
        return colour.XYZ_to_RGB(xyz, WORKING_SPACE, illuminant=illuminant_xy)

    dye, silver = silver_and_dye(density, profile, amount, print_stage=print_stage,
                                 leuco=controls.negative_leuco_cyan)
    rgb = scan(dye, silver)
    if invert:
        dmin = scan(np.zeros((1, 1, 3)))
        dmax = scan(np.nanmax(profile.data.density_curves, axis=0).reshape(1, 1, 3))
        span = dmin - dmax
        span = np.copysign(np.maximum(np.abs(span), 1e-6), span)
        rgb = (rgb - dmax if profile.is_positive else dmin - rgb) / span
    return compress_rgb(rgb, pipeline.io.output_gamut_compress, output_color_space=WORKING_SPACE.name)


def render_grid(pipeline, image: np.ndarray, controls: FilmControls):
    film = develop_film(pipeline, image, controls)
    if controls.mode == "scan":
        return scan_medium(pipeline, film, controls)

    if controls.negative_bleach_bypass:
        log_print = expose_print_with_bypass(pipeline, film, controls)
    else:
        log_print = pipeline.process(film, inject=Tap.CMY_FILM, collect=Tap.LOG_E_PRINT)
    paper = pipeline.process(log_print, inject=Tap.LOG_E_PRINT, collect=Tap.CMY_PRINT)
    if controls.print_shadows or controls.print_highlights:
        maximum = np.maximum(np.nanmax(pipeline.print.data.density_curves, axis=0), 1e-6)
        norm = np.clip(paper / maximum, 0, 1)
        # OFX applies these polynomials to normalized print dye density.
        rest = 1 - norm
        delta = 0.5 * norm * rest * (controls.print_shadows * norm + controls.print_highlights * rest)
        paper = np.clip(norm - delta, 0, 1) * maximum
    return scan_medium(pipeline, paper, controls)


@lru_cache(maxsize=2)
def sample_srgb_grid(size: int):
    axis = np.linspace(0.0, 1.0, size)
    rgb = np.stack(np.meshgrid(axis, axis, axis, indexing="ij"), axis=-1).reshape(-1, 3)
    xyz = colour.RGB_to_XYZ(rgb, "sRGB", illuminant=D50, apply_cctf_decoding=True)
    return xyz, np.maximum(colour.XYZ_to_RGB(xyz, WORKING_SPACE), 0)


class LutRenderer:
    def __init__(self):
        self.films, self.papers = load_stocks()

    @lru_cache(maxsize=8)
    def simulate_xyz(self, film: str, paper: str, controls: FilmControls, size: int):
        pipeline, stock_cyan = get_pipeline(film, paper, controls.mode, controls.spectral_method,
                                           controls.uv_cutoff, controls.ir_cutoff)
        standard_development = controls.film_push_pull_mode == "standard"
        film_gamma = film_development_gamma(controls.film_push_pull) if standard_development else 1
        pipeline.film_render.density_curve_gamma = controls.film_gamma * film_gamma
        couplers = pipeline.film_render.dir_couplers
        couplers.amount = controls.dir_amount
        couplers.inhibition_samelayer = controls.dir_same_layer
        couplers.inhibition_interlayer = controls.dir_interlayer
        # Refresh the film density reference once after changing gamma.
        pipeline.soft_update(exposure_compensation_ev=0)

        enlarger = pipeline.enlarger
        enlarger.print_exposure = 2**controls.printer_exposure
        enlarger.c_filter_neutral = stock_cyan + controls.cyan_filter
        enlarger.m_filter_shift = controls.magenta_filter
        enlarger.y_filter_shift = controls.yellow_filter
        enlarger.preflash_exposure = controls.preflash_exposure
        enlarger.preflash_m_filter_shift = controls.preflash_magenta
        enlarger.preflash_y_filter_shift = controls.preflash_yellow
        gamma = controls.print_gamma * 2 ** (controls.print_push_pull * 0.25)
        pipeline.print_render.density_curves_morph = PrintCurvesMorphParams(
            active=gamma != 1 or controls.print_exhaustion != 0,
            gamma_factor=gamma, developer_exhaustion=controls.print_exhaustion,
        )

        _, source = sample_srgb_grid(size)
        # Bounded chunks limit the spectral wavelength buffers for a 65³ export.
        output = np.empty_like(source)
        for start in range(0, len(source), 65536):
            batch = slice(start, start + 65536)
            image = source[batch].reshape(1, -1, 3) * 2**controls.exposure
            rendered = render_grid(pipeline, image, controls).reshape(-1, 3)
            output[batch] = colour.RGB_to_XYZ(rendered, WORKING_SPACE)
        if not np.isfinite(output).all():
            raise ValueError("SpektraFilm produced invalid color values for these settings.")
        return output


    def mixed_xyz(self, film, paper, controls, strength, size):
        source, _ = sample_srgb_grid(size)
        if strength == 0:
            return source
        rendered = self.simulate_xyz(film, paper, controls, size)
        # Interpolate in linear-light D50 XYZ before encoding sRGB.
        return source + strength * (rendered - source)


    def export_cube(self, film, paper, controls, strength, size):
        if strength == 0:
            axis = np.linspace(0, 1, size)
            rgb = np.stack(np.meshgrid(axis, axis, axis, indexing="ij"), axis=-1).reshape(-1, 3)
        else:
            xyz = self.mixed_xyz(film, paper, controls, strength, size)
            rgb = np.clip(colour.XYZ_to_RGB(xyz, "sRGB", illuminant=D50, apply_cctf_encoding=True), 0, 1)
        red_fastest = rgb.reshape(size, size, size, 3).transpose(2, 1, 0, 3).reshape(-1, 3)
        header = (f'TITLE "Spektrafilm Darkroom for Capture One {film} {paper} sRGB"\n'
                  '# sRGB encoded input and output\n'
                  '# Derived from Andrea Volpato / https://github.com/andreavolpato/spektrafilm\n'
                  '# Profiles and derivatives licensed CC BY-SA 4.0 / https://creativecommons.org/licenses/by-sa/4.0/\n'
                  '# Modified for Spektrafilm for Capture One by Luka Kauzlaric\n'
                  '# Further modified for Spektrafilm Darkroom for Capture One\n'
                  f'LUT_3D_SIZE {size}\nDOMAIN_MIN 0.0 0.0 0.0\nDOMAIN_MAX 1.0 1.0 1.0\n')
        with StringIO() as stream:
            stream.write(header)
            np.savetxt(stream, red_fastest, fmt="%.7f")
            return stream.getvalue()


    def handle(self, request: dict) -> dict:
        if request.get("command") != "export_lut":
            raise ValueError("Unsupported LUT command.")
        controls = read_controls(request)
        film = request.get("film", DEFAULT_FILM)
        paper = request.get("paper", DEFAULT_PAPER) if controls.mode == "print" else DEFAULT_PAPER
        if not isinstance(film, str) or film not in self.films:
            raise ValueError("Choose a film from the available stocks.")
        if controls.mode == "print" and self.films[film]["kind"] == "positive":
            raise ValueError("Use Scan Film with a positive film stock.")
        if not isinstance(paper, str) or paper not in self.papers:
            raise ValueError("Choose a print stock from the available stocks.")
        strength = read_slider(request, "strength", 1, 0, 1)
        size = request.get("size", 33)
        if isinstance(size, bool) or not isinstance(size, int) or size not in (33, 65):
            raise ValueError("Choose a 33- or 65-point LUT.")
        return {"cube": self.export_cube(film, paper, controls, strength, size)}


def read_slider(request: dict, key: str, default: float, low: float, high: float) -> float:
    value = request.get(key, default)
    if (
        isinstance(value, bool)
        or not isinstance(value, (float, int))
        or not low <= value <= high
    ):
        raise ValueError(f"{key} must be between {low} and {high}.")
    return float(value)


def main() -> None:
    renderer = LutRenderer()

    for line in sys.stdin:
        request_id = None
        start = perf_counter()
        try:
            request = json.loads(line)
            if not isinstance(request, dict):
                raise ValueError("Expected a JSON request object.")
            request_id = request.get("id")
            with redirect_stdout(sys.stderr):
                payload = renderer.handle(request)
            response = {"id": request_id, "ok": True, **payload}
        except (ValueError, KeyError, RuntimeError, OSError) as error:
            response = {"id": request_id, "ok": False, "error": str(error)}
        response["elapsed_ms"] = round((perf_counter() - start) * 1000, 1)
        print(json.dumps(response, ensure_ascii=True, separators=(",", ":")), flush=True)


if __name__ == "__main__":
    main()
