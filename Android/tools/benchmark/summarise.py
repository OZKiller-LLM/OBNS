"""Usage: python summarise.py [results/all_*.txt ...]
One line per phone and run from run_all.sh's files: the renderer asked for and the one used, the
load time, and the median cab and exterior frame rates (the 5-second samples, the first dropped as
it includes the switch). Then the plugin regression."""
import glob
import re
import statistics
import sys

files = sys.argv[1:] or sorted(f for f in glob.glob("results/all_*.txt") if not f.endswith("all_summary.txt"))


def median(samples):
    values = [float(v) for v in samples.split()]
    if len(values) > 2:
        values = values[1:]
    return statistics.median(values) if values else 0.0


for name in files:
    text = open(name, encoding="utf-8", errors="replace").read()
    head = text.splitlines()[0] if text else name
    model = re.match(r"=== (\S+)", head)
    print(f"## {model.group(1) if model else name}")
    for block in re.split(r"(?m)^(?==== .*\| (?:vulkannative|opengl|vulkan) \|)", text)[1:]:
        title = re.match(r"=== (.*?) \| (\S+) \|", block)
        load = re.search(r"^load: (.*)$", block, re.M)
        used = re.search(r"graphics backend: (\S+)", block)
        cab = re.search(r"^cab fps \(5 s samples\): (.*)$", block, re.M)
        ext = re.search(r"^exterior fps \(5 s samples\): (.*)$", block, re.M)
        thermal = re.search(r"^before: thermal status (\d)", block, re.M)
        print("  {:<38} {:<12} used {:<12} {:<22} cab {:>6.1f}  exterior {:>6.1f}{}".format(
            title.group(1)[:38], title.group(2), used.group(1) if used else "?", load.group(1)[:22] if load else "?",
            median(cab.group(1)) if cab else 0.0, median(ext.group(1)) if ext else 0.0,
            f"  (thermal {thermal.group(1)} at start)" if thermal and thermal.group(1) not in "01" else ""))
    for line in re.findall(r"^(.*(?:PASS|FAIL|passed \d).*)$", text, re.M):
        print("  " + line[:100])
    if "STOPPED" in text:
        print("  " + re.search(r"^.*STOPPED.*$", text, re.M).group(0)[:120])
    print()
