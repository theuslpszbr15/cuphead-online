"""Compares the .deep files from host and friend and shows the first frame/object that diverged."""
import sys
import os

# Hit sparks pick their look on their own; they never touch gameplay.
COSMETIC = ("Player_Hit",)


def load(path):
    frames = {}
    cur = None
    with open(path, encoding="utf-8", errors="replace") as f:
        for line in f:
            line = line.rstrip("\n")
            if line.startswith("F "):
                parts = line.split()
                cur = int(parts[1])
                frames[cur] = (line, [])
            elif cur is not None:
                frames[cur][1].append(line)
    return frames


def barrier_frames(log):
    """Frames where 'barreira 1' released (start of the synchronized part of each scene)."""
    import re
    out = []
    with open(log, encoding="utf-8", errors="replace") as f:
        for line in f:
            m = re.search(r"barreira 1:(\S+) liberada no frame (\d+)", line)
            if m:
                out.append((int(m.group(2)), m.group(1)))
    return out


def main():
    d = sys.argv[1]
    h = load(os.path.join(d, "host.log.deep"))
    a = load(os.path.join(d, "amigo.log.deep"))
    bars = barrier_frames(os.path.join(d, "host.log")) + barrier_frames(os.path.join(d, "amigo.log"))
    loads = []
    with open(os.path.join(d, "amigo.log"), encoding="utf-8", errors="replace") as f:
        import re
        for line in f:
            m = re.search(r"carregando (\S+) no frame (\d+)", line)
            if m:
                loads.append(int(m.group(2)))

    def synced(fr):
        # after the latest barrier 1 (from either side) and before the next load
        start = max([b for b, _ in bars if b <= fr], default=None)
        if start is None:
            return False
        nxt = min([l for l in loads if l > start], default=10**9)
        return fr > start + 1 and fr < nxt
    common = sorted(f for f in set(h) & set(a) if synced(f))
    # The last frame each side wrote can be cut in half when the test closes the game.
    if common:
        common = common[:-1]
    print("frames comparados:", len(common), (common[0], common[-1]) if common else "")

    def name_of(line):
        parts = line.split()
        return parts[1] if parts and parts[0] in ("A", "R") and len(parts) > 1 else (parts[0] if parts else "")

    # Objects that already differ in the first synced frames are UI/visual noise
    # (they run on real time); ignore them so the first gameplay divergence stands out.
    noise = set()
    for f in common[:30]:
        for x in set(h[f][1]) ^ set(a[f][1]):
            if x.startswith("A "):
                noise.add(name_of(x))
    if noise:
        print("ignorando (ruido visual):", ", ".join(sorted(noise)))

    def clean(lines):
        return set(x for x in lines if name_of(x) not in noise and not (x.startswith("A ") and (x.endswith("norend") or name_of(x) in COSMETIC)))
    bad = 0
    for f in common:
        hh, ho = h[f]
        ah, ao = a[f]
        sh, sa = clean(ho), clean(ao)
        if hh.split()[2:] != ah.split()[2:] or sh != sa:
            bad += 1
            if bad > 1:
                continue
            print("PRIMEIRA DIVERGENCIA no frame", f)
            print("  host :", hh)
            print("  amigo:", ah)
            only_h = sorted(sh - sa)
            only_a = sorted(sa - sh)
            for x in only_h[:25]:
                print("  so host :", x)
            for x in only_a[:25]:
                print("  so amigo:", x)
    print("frames divergentes:", bad)


main()
