"""ユーザーのゲーム原本から、ビルドに必要な管理DLLだけをローカルへ複製する。"""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parent.parent


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("game_dir", type=Path, help="Steamゲーム原本のdata_sts2_*ディレクトリ")
    parser.add_argument("--output", type=Path, default=ROOT / "lib", help="新規作成するローカルDLLディレクトリ")
    args = parser.parse_args()
    source = args.game_dir.resolve()
    destination = args.output.resolve()
    names = [entry.attrib["Include"].replace("\\", "/").split("/")[-1]
             for entry in ET.parse(ROOT / "Directory.Build.targets").iter("Reference")
             if "Include" in entry.attrib]
    missing = [name for name in names if not (source / name).is_file()]
    if missing:
        parser.error("ゲーム入力が不足: " + ", ".join(missing))
    if destination.exists():
        parser.error("出力先が既に存在します。別の--outputを指定してください")
    destination.mkdir(parents=True)
    hashes = {}
    for name in names:
        shutil.copy2(source / name, destination / name)
        hashes[name] = hashlib.sha256((destination / name).read_bytes()).hexdigest()
    (destination / "game-libs.json").write_text(json.dumps(hashes, indent=2) + "\n")
    print(f"{len(names)}個のDLLを{destination}へ複製しました。Gitには追加しないでください。")


if __name__ == "__main__":
    main()
