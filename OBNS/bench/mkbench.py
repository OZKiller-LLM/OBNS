# OBNS: packs the benchmark content for a test PC (bench.ps1), into the folder given first; then uchibo, eal, tml or all three.
# The paths are this PC's. The Tuen Ma Line pack is private: only to a machine the user has named, never committed or shared.
import os, zipfile, hashlib, sys
out = sys.argv[1]
os.makedirs(out, exist_ok=True)
Jp = r'D:\Documents\openBVE\Userdata\LegacyContent\Train\日本\JR東日本'


def add_tree(z, src, arc):
    for root, dirs, files in os.walk(src):
        for f in files:
            p = os.path.join(root, f)
            z.write(p, os.path.join(arc, os.path.relpath(p, src)).replace(os.sep, '/'))


def finish(path):
    h = hashlib.sha256()
    with open(path, 'rb') as f:
        for b in iter(lambda: f.read(1 << 20), b''):
            h.update(b)
    print(os.path.basename(path), os.path.getsize(path), h.hexdigest(), flush=True)


which = sys.argv[2:] or ['uchibo', 'eal', 'tml']
if 'uchibo' in which:
    p = os.path.join(out, 'bench-uchibo.zip')
    with zipfile.ZipFile(p, 'w', zipfile.ZIP_STORED, allowZip64=True) as z:
        z.write(r'D:\BVETS\Scenarios\Uchibo_3793F_ATS-P.txt', 'Uchibo/Scenarios/Uchibo_3793F_ATS-P.txt')
        add_tree(z, r'D:\BVETS\Scenarios\Uchibo20', 'Uchibo/Scenarios/Uchibo20')
        # The OpenBVE E217's exterior beside the BVE6 vehicle, as its extensions.cfg expects (..\橫須賀線 E217系\Extensions).
        z.write(os.path.join(Jp, '總武快速線 E217系', 'extensions.cfg'), 'Uchibo/Scenarios/Uchibo20/E217r/extensions.cfg')
        add_tree(z, os.path.join(Jp, '橫須賀線 E217系', 'Extensions'), 'Uchibo/Scenarios/Uchibo20/橫須賀線 E217系/Extensions')
    finish(p)
if 'eal' in which:
    p = os.path.join(out, 'bench-eal.zip')
    with zipfile.ZipFile(p, 'w', zipfile.ZIP_STORED, allowZip64=True) as z:
        add_tree(z, r'D:\TEMP\Railway', 'EAL/Railway')
        add_tree(z, r'D:\TEMP\mtr-full\MTR R-Train EMU 2023', 'EAL/Train/MTR R-Train EMU 2023')
    finish(p)
if 'tml' in which:
    p = os.path.join(out, 'bench-tml.zip')
    with zipfile.ZipFile(p, 'w', zipfile.ZIP_STORED, allowZip64=True) as z:
        add_tree(z, r'D:\Documents\TEMP\TML_LicensingFix', 'TML')
    finish(p)
print('done', flush=True)
