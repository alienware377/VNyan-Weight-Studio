"""Builds the release zip for Weight Studio + Weight Studio Reader.

    python make_release_zip.py [--out DIR]

Layout of the zip (entry names always use forward slashes):

    WeightStudio/VNyanWeightStudio.dll
    WeightStudio/WeightStudio.vnobj
    WeightStudioReader/VNyanWeightStudioReader.dll
    WeightStudioReader/WeightStudioReader.vnobj
    install.bat
    install.ps1
    README.md
    LICENSE

The plugin files are taken from dist/ and Reader/dist/, and the version from the VERSION
constant in BoneWeightStudioPlugin.cs (the Reader's VERSION must match). The zip is written
to DIR (default: release/ next to this script) as VNyan-Weight-Studio-v<version>.zip.
"""
import argparse
import hashlib
import os
import re
import sys
import zipfile

REPO_NAME = "VNyan-Weight-Studio"
HERE = os.path.dirname(os.path.abspath(__file__))
# (source path relative to this script, entry name in the zip)
PAYLOAD = [
    ("dist/VNyanWeightStudio.dll", "WeightStudio/VNyanWeightStudio.dll"),
    ("dist/WeightStudio.vnobj", "WeightStudio/WeightStudio.vnobj"),
    ("Reader/dist/VNyanWeightStudioReader.dll", "WeightStudioReader/VNyanWeightStudioReader.dll"),
    ("Reader/dist/WeightStudioReader.vnobj", "WeightStudioReader/WeightStudioReader.vnobj"),
]
TOP_LEVEL = ["install.bat", "install.ps1", "README.md", "LICENSE"]
DASHES = (chr(0x2013), chr(0x2014))  # en dash, em dash


def read_version(rel):
    with open(os.path.join(HERE, rel), encoding="utf-8") as f:
        m = re.search(r'VERSION\s*=\s*"v?([0-9][0-9.]*)"', f.read())
    if not m:
        sys.exit("VERSION constant not found in " + rel)
    return m.group(1)


def md5(path):
    with open(path, "rb") as f:
        return hashlib.md5(f.read()).hexdigest()


def main():
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--out", default=os.path.join(HERE, "release"), help="output folder")
    args = ap.parse_args()

    version = read_version("BoneWeightStudioPlugin.cs")
    reader_version = read_version(os.path.join("Reader", "WeightStudioReaderPlugin.cs"))
    if reader_version != version:
        sys.exit("version mismatch: Weight Studio v%s, Reader v%s" % (version, reader_version))

    entries = [(os.path.join(HERE, src), name) for src, name in PAYLOAD]
    entries += [(os.path.join(HERE, name), name) for name in TOP_LEVEL]
    for src, _ in entries:
        if not os.path.isfile(src):
            sys.exit("missing: " + src)

    # text files that ship must not carry en or em dashes; install.ps1 must stay pure ASCII
    blobs = {}
    for src, name in entries:
        with open(src, "rb") as f:
            data = f.read()
        if name.endswith((".bat", ".ps1", ".md")) or name == "LICENSE":
            text = data.decode("utf-8")
            if any(d in text for d in DASHES):
                sys.exit("dash character found in " + name)
            if name.endswith(".ps1") and any(b > 127 for b in data):
                sys.exit("non-ASCII byte found in " + name)
            if name.endswith(".bat"):
                # cmd.exe is happiest with Windows line endings
                data = data.replace(b"\r\n", b"\n").replace(b"\n", b"\r\n")
        blobs[name] = data

    os.makedirs(args.out, exist_ok=True)
    zip_path = os.path.join(args.out, "%s-v%s.zip" % (REPO_NAME, version))
    if os.path.exists(zip_path):
        os.remove(zip_path)
    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as z:
        for _, name in entries:
            z.writestr(name.replace("\\", "/"), blobs[name])

    print("wrote " + zip_path)
    with zipfile.ZipFile(zip_path) as z:
        bad = [i.filename for i in z.infolist() if "\\" in i.filename]
        if bad:
            sys.exit("backslash entries: " + ", ".join(bad))
        for info in z.infolist():
            print("  %-50s %8d" % (info.filename, info.file_size))
    for src, name in entries[:len(PAYLOAD)]:
        print("  md5 %s  %s" % (md5(src), name))


if __name__ == "__main__":
    main()
