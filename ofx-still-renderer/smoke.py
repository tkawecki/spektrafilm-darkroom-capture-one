"""Exercise the Vulkan binary frame bridge without opening Capture One."""

import argparse
from pathlib import Path
import struct
import subprocess
import time

import numpy as np


def source_frame(path):
    if path:
        from PIL import Image, ImageOps

        with Image.open(path) as image:
            image = ImageOps.exif_transpose(image).convert("RGB")
            image.thumbnail((720, 720))
            rgb = np.asarray(image, dtype=np.float32) / 255
    else:
        y, x = np.mgrid[0:1:256j, 0:1:384j].astype(np.float32)
        checker = (np.floor(x * 12) + np.floor(y * 8)) % 2
        rgb = np.stack((x, y, 0.2 + 0.6 * checker), axis=-1).astype(np.float32)
    return np.concatenate((rgb, np.ones((*rgb.shape[:2], 1), np.float32)), axis=-1)


def render(exe, frame, options):
    height, width = frame.shape[:2]
    start = time.monotonic()
    result = subprocess.run(
        [str(exe), str(width), str(height), *options.split()],
        input=frame.astype("<f4", copy=False).tobytes(),
        capture_output=True,
        timeout=20,
    )
    if result.returncode:
        raise RuntimeError(result.stderr.decode("utf-8", errors="replace"))
    if len(result.stdout) != frame.nbytes:
        raise AssertionError("Unexpected output byte count")
    pixels = np.frombuffer(result.stdout, dtype="<f4").reshape(frame.shape)
    if not np.isfinite(pixels).all():
        raise AssertionError("Non-finite output")
    np.testing.assert_array_equal(pixels[..., 3], frame[..., 3])
    return pixels, time.monotonic() - start


def reference_crop(frame):
    height, width = frame.shape[:2]
    scale, x_shift, y_shift = 1.2, 0.04, -0.03
    x = (width / 2) + (np.arange(width) + 0.5 - width / 2 + width * x_shift) / scale - 0.5
    y = (height / 2) + (np.arange(height) + 0.5 - height / 2 + height * y_shift) / scale - 0.5
    x0, y0 = np.floor(x).astype(int), np.floor(y).astype(int)
    tx = (x - x0)[None, :, None]
    ty = (y - y0)[:, None, None]
    top = (1 - tx) * frame[y0[:, None], x0] + tx * frame[y0[:, None], x0 + 1]
    bottom = (1 - tx) * frame[y0[:, None] + 1, x0] + tx * frame[y0[:, None] + 1, x0 + 1]
    return ((1 - ty) * top + ty * bottom).astype(np.float32)


def frame_request(frame, options):
    height, width = frame.shape[:2]
    return f"FRAME {width} {height} {options}\n".encode("ascii") + frame.astype("<f4", copy=False).tobytes()


def run_stream(exe, request, exit_code=0, mode="--stream"):
    result = subprocess.run([str(exe), mode], input=request, capture_output=True, timeout=20)
    if result.returncode != exit_code:
        raise AssertionError(f"Expected renderer exit {exit_code}, got {result.returncode}: {result.stderr!r}")
    offset = 0
    replies = []
    while offset < len(result.stdout):
        if len(result.stdout) - offset < 8:
            raise AssertionError("Truncated reply header")
        status, length = struct.unpack_from("<II", result.stdout, offset)
        offset += 8
        if status not in (0, 1) or length > len(result.stdout) - offset:
            raise AssertionError(f"Invalid reply header: status {status}, length {length}")
        if status == 1 and not 1 <= length <= 4096:
            raise AssertionError(f"Invalid error length: {length}")
        replies.append((status, result.stdout[offset:offset + length]))
        offset += length
    return replies


def check_stream(exe, frame, base):
    small = frame[::8, ::8].copy()
    small[..., 3] = np.linspace(0, 1, small.shape[1], dtype=np.float32)
    different = small * np.float32(0.65)
    nonfinite = different.copy()
    nonfinite.flat[1] = np.nan
    baseline, _ = render(exe, small, base)
    alternate, _ = render(exe, different, base)
    reuse = f"REUSE {base}\n".encode("ascii")
    max_length_line = reuse[:-1].ljust(16384, b" ") + b"\n"

    replies = run_stream(exe, b"".join((
        reuse,
        frame_request(small, base),
        max_length_line,
        b"REUSE --output-color\n",
        frame_request(different, "--unknown-option 1"),
        frame_request(nonfinite, base),
        reuse,
        frame_request(different, base),
        reuse,
        b"QUIT invalid\n",
        b"QUIT\n",
    )))
    expected_errors = {
        0: b"Send a FRAME",
        3: b"Each setting needs a value",
        4: b"Unknown setting",
        5: b"Invalid input pixel",
        9: b"QUIT does not take arguments",
    }
    expected_frames = {1: baseline, 2: baseline, 6: baseline, 7: alternate, 8: alternate}
    assert len(replies) == 11, replies
    for index, error in expected_errors.items():
        status, payload = replies[index]
        assert status == 1 and error in payload, (index, status, payload)
    for index, expected in expected_frames.items():
        status, payload = replies[index]
        assert status == 0 and len(payload) == small.nbytes, (index, status, len(payload))
        pixels = np.frombuffer(payload, dtype="<f4").reshape(small.shape)
        np.testing.assert_array_equal(pixels, expected)
    assert replies[-1] == (0, b"")
    assert run_stream(exe, b"") == []
    assert run_stream(exe, frame_request(small, base)) == [(0, replies[1][1])]

    for request, error in (
        (b"FRAME 2048 2048\n", b"up to 1,048,576 pixels"),
        (b"FRAME 1 1\nshort", b"wrong byte length"),
        (b"FRAME 1 1 --bad 1\nshort", b"wrong byte length"),
        (b"REUSE\0\n", b"ASCII text"),
        (max_length_line[:-1] + b" \n", b"exceeds 16 KB"),
        (b"REUSE", b"before its line break"),
    ):
        failure = run_stream(exe, request, exit_code=1)
        assert len(failure) == 1 and failure[0][0] == 1 and error in failure[0][1], failure
    print("Stream validated persistent rendering, bounded input, error recovery, QUIT, and EOF")


def check_export_stream(exe, frame, base):
    small_request = frame_request(frame[::8, ::8].copy(), base)
    assert run_stream(exe, small_request, mode="--export-stream") == run_stream(exe, small_request)

    large = np.tile(frame, (1 + 1025 // frame.shape[0], 1 + 1025 // frame.shape[1], 1))[:1025, :1025].copy()
    request = frame_request(large, base)
    failure = run_stream(exe, b"FRAME 1025 1025\n", exit_code=1)
    assert len(failure) == 1 and failure[0][0] == 1 and b"up to 1,048,576 pixels" in failure[0][1]
    replies = run_stream(exe, request, mode="--export-stream")
    assert len(replies) == 1 and replies[0][0] == 0 and len(replies[0][1]) == large.nbytes
    exported = np.frombuffer(replies[0][1], dtype="<f4").reshape(large.shape)
    assert np.isfinite(exported).all()
    assert np.mean(np.abs(exported[..., :3] - large[..., :3])) > 0.0001
    np.testing.assert_array_equal(exported[..., 3], large[..., 3])

    for request, error in (
        (b"FRAME 16385 1\n", b"Invalid integer"),
        (b"FRAME 10001 10000\n", b"up to 100,000,000 pixels"),
    ):
        failure = run_stream(exe, request, exit_code=1, mode="--export-stream")
        assert len(failure) == 1 and failure[0][0] == 1 and error in failure[0][1]
    print("Export stream accepted a frame above the preview limit and preserved preview color parity")


def check_output_transfers(exe, frame, base):
    small = frame[::8, ::8].copy()
    small[..., 3] = np.linspace(0, 1, small.shape[1], dtype=np.float32)
    transfers = ("srgb", "rec709_gamma24", "linear_rec709")
    commands = "".join(f"REUSE {base} --output-color {name}\n" for name in transfers)
    commands += f"REUSE {base} --output-color adobe_rgb\nREUSE {base}\n"
    request = frame_request(small, base) + commands.encode("ascii")
    replies = run_stream(exe, request)
    assert len(replies) == 6 and all(status == 0 for status, _ in replies[:4]), replies
    assert replies[4][0] == 1 and b"Unknown option value" in replies[4][1]
    assert replies[5] == replies[0]
    default, srgb, gamma24, linear = (
        np.frombuffer(payload, dtype="<f4").reshape(small.shape) for _, payload in replies[:4]
    )
    np.testing.assert_array_equal(default, srgb)
    for name, output in zip(transfers, (srgb, gamma24, linear)):
        standalone, _ = render(exe, small, f"{base} --output-color {name}")
        np.testing.assert_array_equal(output, standalone)
        np.testing.assert_array_equal(output[..., 3], small[..., 3])

    srgb_rgb, gamma24_rgb, linear_rgb = (output[..., :3] for output in (srgb, gamma24, linear))
    magnitude = np.abs(srgb_rgb)
    srgb_decoded = np.copysign(
        np.where(magnitude <= 0.04045, magnitude / 12.92, ((magnitude + 0.055) / 1.055) ** 2.4), srgb_rgb
    )
    gamma24_decoded = np.copysign(np.abs(gamma24_rgb) ** 2.4, gamma24_rgb)
    assert np.max(np.abs(srgb_rgb - gamma24_rgb)) > 0.005
    np.testing.assert_allclose(gamma24_decoded, linear_rgb, rtol=0, atol=3e-6)
    # Upstream generates separate, rounded sRGB and BT.709 matrices for nominally the same primaries.
    np.testing.assert_allclose(srgb_decoded, linear_rgb, rtol=0, atol=2e-4)
    print("Verified sRGB, Rec.709 gamma 2.4, and linear Rec.709 output against their decoded color tolerances")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--image", type=Path, help="Optional photograph. Defaults to a synthetic image.")
    parser.add_argument("--exe", type=Path, default=Path(__file__).resolve().parents[1]
                        / "build-ofx-still" / "Release" / "SpektraStillRenderer.exe")
    args = parser.parse_args()
    frame = source_frame(args.image)
    base = "--stock kodak_portra_400 --paper kodak_portra_endura --input-color srgb"
    check_stream(args.exe, frame, base)
    check_export_stream(args.exe, frame, base)
    check_output_transfers(args.exe, frame, base)
    baseline, seconds = render(args.exe, frame, base)
    print(f"Baseline {frame.shape[1]}×{frame.shape[0]} in {seconds * 1000:.0f} ms")

    cases = {
        "halation": "--halation-amount 3 --halation-scale 3 --halation-boost 5",
        "camera diffusion": "--camera-diffusion-family pro_mist --camera-diffusion-strength 1.5",
        "print diffusion": "--print-diffusion-family black_pro_mist --print-diffusion-strength 1.5",
        "scanner glare and MTF": "--scanner-enabled true --scanner-glare 0.1 --scanner-mtf 15",
        "scanner sharpening": "--scanner-enabled true --scanner-unsharp-radius 60 --scanner-unsharp-amount 3",
        "production grain": "--grain-model production --grain-amount 1.6",
        "synthesis grain": "--grain-model synthesis --grain-synthesis-samples 12 --grain-synthesis-cell-cap 8",
        "spatial DIR": "--dir-amount 1.5 --dir-diffusion 80 --dir-tail-weight 0.3",
        "enlarger crop": "--enlarger-scale 1.2 --enlarger-x 4 --enlarger-y 3",
        "standard film push": "--film-push-pull 1.7",
        "experimental film push": "--film-push-pull 1.7 --film-push-pull-mode experimental",
        "negative bleach bypass": "--negative-bleach-bypass 0.9",
        "scan negative": "--mode scan",
        "invert negative scan": "--mode scan_inverted",
    }
    for label, settings in cases.items():
        actual, seconds = render(args.exe, frame, f"{base} {settings}")
        delta = float(np.mean(np.abs(actual[..., :3] - baseline[..., :3]))) * 255
        if delta <= 0.01:
            raise AssertionError(f"{label} did not change the output ({delta:.3f}/255)")
        print(f"{label:24s} mean RGB change {delta:7.3f}/255 in {seconds * 1000:.0f} ms")
        if label == "enlarger crop":
            expected, _ = render(args.exe, reference_crop(frame), base)
            np.testing.assert_allclose(actual, expected, rtol=2e-4, atol=2e-4)
    for stock in ("kodak_portra_400", "fujifilm_provia_100f"):
        options = f"--stock {stock} --input-color srgb"
        forward, _ = render(args.exe, frame, f"{options} --mode scan")
        inverted, _ = render(args.exe, frame, f"{options} --mode scan_inverted")
        delta = np.mean(np.abs(forward[..., :3] - inverted[..., :3])) * 255
        if delta <= 0.01:
            raise AssertionError(f"Scan inversion did not change the scan output for {stock}")
        print(f"{stock:24s} mean RGB scan inversion change {delta:7.3f}/255")


if __name__ == "__main__":
    main()
