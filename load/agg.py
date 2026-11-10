import json, sys, statistics
from datetime import datetime, timezone

PATH = sys.argv[1] if len(sys.argv) > 1 else "/tmp/opencode/k6-out/k6-samples.json"
WIN = 30

# buckets: window_start_epoch -> dict
dur = {}
fail = {}
reqs = {}
codes = {}
total_reqs = total_fail = 0
all_dur = []
start = None
end = 0


def parse_t(s):
    # "2026-11-09T20:57:51.214583797Z"
    s = s.split(".")[0] + "Z" if "." in s else s
    return datetime.fromisoformat(s.replace("Z", "+00:00")).timestamp()


with open(PATH) as f:
    for line in f:
        if '"type":"Point"' not in line:
            continue
        try:
            o = json.loads(line)
        except Exception:
            continue
        m = o.get("metric")
        d = o.get("data", {})
        if m not in ("http_reqs", "http_req_failed", "http_req_duration"):
            continue
        t = parse_t(d["time"])
        if start is None:
            start = t
        end = max(end, t)
        w = int((t - start) // WIN) * WIN

        if m == "http_reqs":
            reqs[w] = reqs.get(w, 0) + 1
            total_reqs += 1
            st = (d.get("tags") or {}).get("status", "?")
            codes[st] = codes.get(st, 0) + 1
        elif m == "http_req_failed":
            v = 1 if d["value"] else 0
            fail[w] = fail.get(w, 0) + v
            total_fail += v
        else:
            dur.setdefault(w, []).append(d["value"])
            all_dur.append(d["value"])


def pct(xs, p):
    if not xs:
        return 0.0
    xs = sorted(xs)
    k = (len(xs) - 1) * p / 100
    lo, hi = int(k), min(int(k) + 1, len(xs) - 1)
    return xs[lo] + (xs[hi] - xs[lo]) * (k - lo)


last_t = start
span = max(end - start, 1)
print(f"START  {datetime.fromtimestamp(start, tz=timezone.utc):%Y-%m-%d %H:%M:%S} UTC")
print(f"SPAN   {span}s   TOTAL reqs={total_reqs}  failed={total_fail}  "
      f"rate={100*total_fail/total_reqs:.2f}%  rps={total_reqs/span:.1f}")
print(f"LATENCY avg={statistics.mean(all_dur)/1000:.2f}s "
      f"p50={pct(all_dur,50)/1000:.2f}s p90={pct(all_dur,90)/1000:.2f}s "
      f"p95={pct(all_dur,95)/1000:.2f}s max={max(all_dur)/1000:.2f}s")
print("\nWINDOW TABLE")
print("| Window | Req/s | p50 | p90 | p95 | Error % |")
print("|---|---|---|---|---|---|")
for w in sorted(reqs):
    n, fl = reqs[w], fail.get(w, 0)
    ds = dur.get(w, [])
    a = max(min(WIN, span - w), 0.001)
    note = "" if a >= WIN else " *(partial)"
    print(f"| {w}-{w+WIN}s | {n/a:.0f} | {pct(ds,50)/1000:.2f}s | "
          f"{pct(ds,90)/1000:.2f}s | {pct(ds,95)/1000:.2f}s | "
          f"{100*fl/n:.2f} %{note} |")

print("\nSTATUS CODES")
for k, v in sorted(codes.items(), key=lambda x: -x[1]):
    print(f"  {k}: {v}")
