"""Finds the first boss hit that differs between host and friend ([Dmg] lines) and
shows what differed in the deep trace during the frames right before it."""
import re
import sys
import os

d = sys.argv[1]
window = int(sys.argv[2]) if len(sys.argv) > 2 else 15
COSMETIC = ("Player_Hit", "Icon", "Fader")


def hits(fn):
    out = []
    for line in open(os.path.join(d, fn), encoding="utf-8", errors="replace"):
        m = re.search(r"\[Dmg\] (\d+) u\d+ \+([\d.]+) = ([\d.]+) (\S+)", line)
        if m:
            out.append((int(m.group(1)), m.group(2), m.group(4)))
    return out


h, a = hits("host.log"), hits("amigo.log")
div = None
for x, y in zip(h, a):
    if x != y:
        div = min(x[0], y[0])
        print("hit diferente: host", x, "| amigo", y)
        break
if div is None:
    print("hits identicos (", len(h), "x", len(a), ")")
    sys.exit(0)


def frames(fn):
    out = {}
    cur = None
    for line in open(os.path.join(d, fn), encoding="utf-8", errors="replace"):
        line = line.rstrip("\n")
        if line.startswith("F "):
            f = int(line.split()[1])
            cur = f if div - window <= f <= div + 1 else None
            if cur is not None:
                out[cur] = (line, [])
        elif cur is not None:
            out[cur][1].append(line)
    return out


fh, fa = frames("host.log.deep"), frames("amigo.log.deep")


def keep(x):
    return not any(c in x for c in COSMETIC) and not (x.startswith("A ") and x.endswith("norend"))


for f in sorted(set(fh) & set(fa)):
    sh = set(filter(keep, fh[f][1]))
    sa = set(filter(keep, fa[f][1]))
    diff_h, diff_a = sorted(sh - sa), sorted(sa - sh)
    head = "" if fh[f][0].split()[2:] == fa[f][0].split()[2:] else ("  [" + fh[f][0] + " | " + fa[f][0] + "]")
    print(f, "igual" if not diff_h and not diff_a and not head else "DIFERENTE" + head)
    for x in diff_h[:8]:
        print("   so host :", x)
    for x in diff_a[:8]:
        print("   so amigo:", x)
