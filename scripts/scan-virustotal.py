#!/usr/bin/env python3
"""Submit release artifacts to VirusTotal and fail the build on real detections (#111).

Runs on the release-candidate build after the downloadable artifacts are built. For each file it:
  1. Queries VirusTotal by SHA-256 first (no upload if VT has already seen the file — saves quota).
  2. Uploads only when unseen, using the large-file endpoint for files over 32 MB, then polls
     until the analysis completes.
  3. Counts `malicious` / `suspicious` engine verdicts, ignoring engines on a reviewed allowlist.
  4. Prints a per-engine summary + the VT report URL (to the log and the GitHub job summary).
  5. Exits non-zero if any file's malicious count (or suspicious, when enabled) exceeds the threshold.

Config (all optional except the key):
  VT_API_KEY               VirusTotal API key. Absent  -> the scan is SKIPPED with a notice
                           (mirrors how the Chrome Web Store deploy degrades), never a hard fail.
  VT_MALICIOUS_THRESHOLD   Max allowed malicious engines per file before failing. Default 0.
  VT_FAIL_ON_SUSPICIOUS    "true" to also count `suspicious` verdicts toward the threshold.
  scripts/virustotal-allowlist.txt   One engine name per line (# comments) whose verdicts are
                           ignored — so a single flaky heuristic engine can't block every release.

Usage: scan-virustotal.py <file> [<file> ...]

Deliberately dependency-free (urllib only) so it needs no pip install on the runner. Run
`scan-virustotal.py --self-test` to exercise the pure evaluation logic without a network/key.
"""
import json
import os
import sys
import time
import urllib.error
import urllib.request
import hashlib
import uuid

VT_API = "https://www.virustotal.com/api/v3"
LARGE_FILE_BYTES = 32 * 1024 * 1024  # VT's standard upload cap; above this use the upload_url endpoint.
POLL_INTERVAL_S = 20
POLL_TIMEOUT_S = 600


def load_allowlist(path):
    """Engine names whose verdicts we ignore (reviewed known false-positives)."""
    names = set()
    if path and os.path.isfile(path):
        with open(path, encoding="utf-8") as f:
            for line in f:
                line = line.split("#", 1)[0].strip()
                if line:
                    names.add(line)
    return names


def evaluate(results, allowlist, threshold, fail_on_suspicious):
    """Pure decision function over VT's last_analysis_results dict.

    Returns (malicious, suspicious, failed) where malicious/suspicious are the lists of engine
    names that flagged the file (excluding allowlisted engines), and failed is the gate decision.
    """
    malicious, suspicious = [], []
    for engine, verdict in (results or {}).items():
        if engine in allowlist:
            continue
        category = (verdict or {}).get("category")
        if category == "malicious":
            malicious.append(engine)
        elif category == "suspicious":
            suspicious.append(engine)
    counted = len(malicious) + (len(suspicious) if fail_on_suspicious else 0)
    return sorted(malicious), sorted(suspicious), counted > threshold


def _request(method, url, api_key, data=None, headers=None, timeout=120):
    req = urllib.request.Request(url, data=data, method=method)
    req.add_header("x-apikey", api_key)
    for k, v in (headers or {}).items():
        req.add_header(k, v)
    with urllib.request.urlopen(req, timeout=timeout) as resp:  # noqa: S310 (fixed https host)
        return json.loads(resp.read().decode("utf-8"))


def _multipart_upload(url, api_key, file_path):
    """POSTs the file as multipart/form-data (field name 'file'); returns the parsed JSON."""
    boundary = uuid.uuid4().hex
    with open(file_path, "rb") as f:
        payload = f.read()
    name = os.path.basename(file_path)
    body = b"".join([
        f"--{boundary}\r\n".encode(),
        f'Content-Disposition: form-data; name="file"; filename="{name}"\r\n'.encode(),
        b"Content-Type: application/octet-stream\r\n\r\n",
        payload,
        f"\r\n--{boundary}--\r\n".encode(),
    ])
    headers = {"Content-Type": f"multipart/form-data; boundary={boundary}"}
    return _request("POST", url, api_key, data=body, headers=headers, timeout=600)


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for block in iter(lambda: f.read(1 << 20), b""):
            h.update(block)
    return h.hexdigest()


def analysis_results_for(path, api_key):
    """Returns VT's last_analysis_results for a file, uploading + polling if VT hasn't seen it."""
    digest = sha256(path)
    try:
        report = _request("GET", f"{VT_API}/files/{digest}", api_key)
        return digest, report["data"]["attributes"].get("last_analysis_results", {})
    except urllib.error.HTTPError as e:
        if e.code != 404:
            raise  # a real error (auth/quota) — surface it

    # Unseen by VT: upload (large files need a one-time upload URL), then poll the analysis.
    size = os.path.getsize(path)
    if size > LARGE_FILE_BYTES:
        upload_url = _request("GET", f"{VT_API}/files/upload_url", api_key)["data"]
    else:
        upload_url = f"{VT_API}/files"
    analysis_id = _multipart_upload(upload_url, api_key, path)["data"]["id"]

    deadline = time.time() + POLL_TIMEOUT_S
    while time.time() < deadline:
        analysis = _request("GET", f"{VT_API}/analyses/{analysis_id}", api_key)
        if analysis["data"]["attributes"]["status"] == "completed":
            break
        time.sleep(POLL_INTERVAL_S)
    else:
        raise TimeoutError(f"VirusTotal analysis for {os.path.basename(path)} did not complete in time.")

    report = _request("GET", f"{VT_API}/files/{digest}", api_key)
    return digest, report["data"]["attributes"].get("last_analysis_results", {})


def emit(line, summary_lines):
    print(line)
    summary_lines.append(line)


def main(argv):
    if argv == ["--self-test"]:
        return self_test()

    files = argv
    if not files:
        print("usage: scan-virustotal.py <file> [<file> ...]", file=sys.stderr)
        return 2

    api_key = os.environ.get("VT_API_KEY", "").strip()
    if not api_key:
        print("::notice::VT_API_KEY not set — skipping the VirusTotal scan. "
              "Set the secret to enable release binary scanning (#111).")
        return 0

    threshold = int(os.environ.get("VT_MALICIOUS_THRESHOLD", "0"))
    fail_on_suspicious = os.environ.get("VT_FAIL_ON_SUSPICIOUS", "").lower() == "true"
    allowlist = load_allowlist(os.path.join(os.path.dirname(__file__), "virustotal-allowlist.txt"))

    summary = ["# VirusTotal scan", ""]
    failures = []
    for path in files:
        name = os.path.basename(path)
        try:
            digest, results = analysis_results_for(path, api_key)
        except Exception as e:  # network/quota/timeout — report which file and fail loudly.
            emit(f"- ❌ `{name}`: scan error: {e}", summary)
            failures.append(name)
            continue
        malicious, suspicious, failed = evaluate(results, allowlist, threshold, fail_on_suspicious)
        url = f"https://www.virustotal.com/gui/file/{digest}"
        verdict = "❌ FAIL" if failed else "✅ clean"
        emit(f"- {verdict} [`{name}`]({url}) — "
             f"{len(malicious)} malicious, {len(suspicious)} suspicious "
             f"(threshold {threshold}{', +suspicious' if fail_on_suspicious else ''})", summary)
        if malicious:
            emit(f"    - malicious engines: {', '.join(malicious)}", summary)
        if suspicious:
            emit(f"    - suspicious engines: {', '.join(suspicious)}", summary)
        if failed:
            failures.append(name)

    step_summary = os.environ.get("GITHUB_STEP_SUMMARY")
    if step_summary:
        with open(step_summary, "a", encoding="utf-8") as f:
            f.write("\n".join(summary) + "\n")

    if failures:
        print(f"::error::VirusTotal flagged {len(failures)} artifact(s): {', '.join(failures)}",
              file=sys.stderr)
        return 1
    print("All artifacts passed the VirusTotal scan.")
    return 0


def self_test():
    """Exercises the pure evaluate() gate — no network, no key needed."""
    clean = {"EngineA": {"category": "undetected"}, "EngineB": {"category": "harmless"}}
    m, s, failed = evaluate(clean, set(), 0, False)
    assert (m, s, failed) == ([], [], False), (m, s, failed)

    flagged = {"BadHeuristic": {"category": "malicious"}, "EngineB": {"category": "harmless"}}
    m, s, failed = evaluate(flagged, set(), 0, False)
    assert m == ["BadHeuristic"] and failed is True, (m, s, failed)

    # Allowlisted engine's malicious verdict is ignored.
    m, s, failed = evaluate(flagged, {"BadHeuristic"}, 0, False)
    assert m == [] and failed is False, (m, s, failed)

    # Threshold tolerates up to N malicious engines.
    two = {"E1": {"category": "malicious"}, "E2": {"category": "malicious"}}
    assert evaluate(two, set(), 2, False)[2] is False
    assert evaluate(two, set(), 1, False)[2] is True

    # Suspicious only counts when explicitly enabled.
    susp = {"E1": {"category": "suspicious"}}
    assert evaluate(susp, set(), 0, False)[2] is False
    assert evaluate(susp, set(), 0, True)[2] is True

    # Empty / missing results never fail.
    assert evaluate({}, set(), 0, True)[2] is False
    assert evaluate(None, set(), 0, True)[2] is False
    print("scan-virustotal self-test: OK")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
