"""ローカルの固定参照記録とDLLホスト・公開記録器を比較する。実ゲームは起動しない。"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[2]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--corpus", type=Path, required=True)
    parser.add_argument("--input-root", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    manifest = json.loads(args.corpus.read_text())
    inputs = [case["capture"] for case in manifest["fresh"]]
    for case in manifest["saved"]:
        inputs.extend([case["capture"], case["save"]])
    digest = lambda path: hashlib.sha256(path.read_bytes()).hexdigest()
    for item in inputs:
        path = args.input_root / item["path"]
        if not path.is_file() or digest(path) != item["sha256"]:
            parser.error("参照入力のハッシュ不一致: " + str(path))
    if digest(ROOT / "lib/sts2.dll") != manifest["gameSha256"]:
        parser.error("ゲームDLLのハッシュ不一致")
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    env = os.environ | {"LEARN_REFERENCE_ROOT": str(args.input_root.resolve())}
    checks = []

    def run(name, file, flags, report, predicate):
        with (output / (name + ".stdout.log")).open("w") as stdout, (output / (name + ".stderr.log")).open("w") as stderr:
            process = subprocess.run([sys.executable, str(Path(__file__).parent / file), *map(str, flags)],
                                     env=env, cwd=ROOT, stdout=stdout, stderr=stderr, timeout=180)
        passed = process.returncode == 0 and report.is_file() and predicate(json.loads(report.read_text()))
        checks.append(dict(id=name, passed=passed, report=str(report)))
        print(json.dumps(checks[-1]), flush=True)
        (output / "summary.json").write_text(json.dumps(dict(passed=all(row["passed"] for row in checks), checks=checks), indent=2) + "\n")
        return passed

    corpus = args.corpus.resolve()
    fresh = output / "fresh"
    okay = run("fresh_native", "verify_native_paths.py", ["--output", fresh, "--corpus", corpus, "--repeats", 2],
               fresh / "summary.json", lambda values: len(values) == 2 * len(manifest["fresh"])
               and all(value["outcome"] == "matched" and value["exitCode"] == 0 for value in values))
    if okay:
        public = output / "fresh-public"
        run("fresh_public", "verify_recorder_paths.py", ["--input", fresh, "--output", public],
            public / "summary.json", lambda values: len(values) == len(manifest["fresh"]) and all(v["passed"] for v in values))
    for case in manifest["saved"]:
        raw = output / (case["id"] + "-raw")
        save = (args.input_root / case["save"]["path"]).resolve()
        flags = ["--capture", (args.input_root / case["capture"]["path"]).resolve(), "--save", save, "--output", raw]
        if case["fixtureHost"]:
            flags.append("--fixture-host")
        okay = run(case["id"] + "_native", "verify_native_capture.py", flags, raw / "report.json",
                   lambda value, count=case["nativeActions"]: value["outcome"] == "matched" and value["exitCode"] == 0
                   and value["actions"] == count and all(row["equal"] for row in value["checks"]))
        if okay and case["publicRecorder"]:
            public = output / (case["id"] + "-public")
            run(case["id"] + "_public", "verify_recorder_paths.py", ["--input", raw, "--save", save, "--output", public],
                public / "summary.json", lambda values: len(values) == 1 and values[0]["passed"])
    return 0 if checks and all(row["passed"] for row in checks) else 1


if __name__ == "__main__":
    raise SystemExit(main())
