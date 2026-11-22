"""Create the matrix/TRC ICC profiles used for Capture One preview input."""

from pathlib import Path
from struct import pack, pack_into


D50 = (0.9642, 1.0, 0.8249)

# ROMM RGB primaries normalized to the ICC D50 PCS white point.
PROPHOTO_COLORANTS = (
    (0.797666111, 0.288037048, 0.0),
    (0.135192313, 0.711877319, 0.0),
    (0.031341576, 0.000085633, 0.8249),
)

# BT.2020 primaries and D65 white adapted to the D50 PCS with Bradford.
REC2020_COLORANTS = (
    (0.673480188, 0.279042585, -0.001933483),
    (0.165671207, 0.675344496, 0.029982850),
    (0.125048605, 0.045612919, 0.796850633),
)
D65_TO_D50 = (
    1.047886003, 0.022918765, -0.050216095,
    0.029581782, 0.990483518, -0.017078708,
    -0.009251881, 0.015072607, 0.751678134,
)


def fixed16(numbers):
    return b"".join(pack(">i", round(number * 65536)) for number in numbers)


def xyz_tag(values):
    return b"XYZ \0\0\0\0" + fixed16(values)


def text_tag(text):
    # The two characters for language and country are encoded as ASCII.
    encoded = text.encode("utf-16-be")
    return b"mluc\0\0\0\0" + pack(">II4sII", 1, 12, b"enUS", len(encoded), 28) + encoded


def build_profile(description, colorants, curve, adaptation=None):
    header = bytearray(128)
    header[8:24] = b"\x04\x30\x00\x00mntrRGB XYZ "
    pack_into(">6H", header, 24, 2026, 1, 1, 0, 0, 0)
    header[36:40] = b"acsp"
    header[68:80] = fixed16(D50)

    tags = {
        b"desc": text_tag(description),
        b"cprt": text_tag("Generated from standard RGB primaries and transfer functions."),
        b"wtpt": xyz_tag(D50),
        b"rXYZ": xyz_tag(colorants[0]),
        b"gXYZ": xyz_tag(colorants[1]),
        b"bXYZ": xyz_tag(colorants[2]),
        b"rTRC": curve,
        b"gTRC": curve,
        b"bTRC": curve,
    }
    if adaptation is not None:
        tags[b"chad"] = b"sf32\0\0\0\0" + fixed16(adaptation)

    profile = header + pack(">I", len(tags)) + bytes(12 * len(tags))
    for index, (tag, data) in enumerate(tags.items()):
        pack_into(">4sII", profile, 132 + index * 12, tag, len(profile), len(data))
        profile.extend(data)
        profile.extend(bytes(-len(data) % 4))
    pack_into(">I", profile, 0, len(profile))
    return profile


def main():
    directory = Path(__file__).resolve().parent / "profiles" / "preview"
    directory.mkdir(parents=True, exist_ok=True)

    # ICC parametric function 3. Linear toe c*x below d, (a*x+b)**g above.
    prophoto_curve = b"para\0\0\0\0" + pack(">HH", 3, 0) + fixed16((1.8, 1.0, 0.0, 1 / 16, 1 / 32))
    prophoto = build_profile("SpektraFilm ProPhoto RGB Preview", PROPHOTO_COLORANTS, prophoto_curve)
    (directory / "ProPhoto RGB.icc").write_bytes(prophoto)

    linear_curve = b"curv\0\0\0\0" + pack(">I", 0)
    rec2020 = build_profile("SpektraFilm Linear Rec.2020 Preview", REC2020_COLORANTS, linear_curve, D65_TO_D50)
    (directory / "Linear Rec.2020.icc").write_bytes(rec2020)


if __name__ == "__main__":
    main()
