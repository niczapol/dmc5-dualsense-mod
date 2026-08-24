import argparse
import hashlib
import json
from pathlib import Path


EXPECTED_EVENTS = {
    "Coyote_Shot_Shell",
    "Bluerose_Shot_Shell",
    "JR_Jigenzan_Shot_Shell",
    "Evony_Shot_Shell",
    "Ivory_Shot_Shell",
    "Jigenzan_Shot_Shell",
    "Beo_Sp_Impact",
    "Mirage_Sp_Loop",
    "Mirage_Sp_End",
    "Beo_Sp_Pre",
    "Yamato_Zetsu_Return",
    "Yamato_Zetsu_Noutou",
    "Stop_All",
}


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest().upper()


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("inventory", type=Path)
    parser.add_argument("--bank", type=Path)
    parser.add_argument("--wem-dir", type=Path)
    args = parser.parse_args()

    data = json.loads(args.inventory.read_text(encoding="utf-8"))
    events = data["events"]
    media = data["media"]

    assert data["schema"] == 1
    assert data["scanned_bank_files"] == 48
    assert len(events) == 13
    assert {item["name"] for item in events} == EXPECTED_EVENTS
    assert [item["index"] for item in events] == list(range(13))
    assert len({item["event_id"] for item in events}) == 13
    assert len(media) == 12
    assert len({item["id"] for item in media}) == 12
    assert sum(item["size"] for item in media) == 317440
    assert {item["media_id"] for item in events if item["media_id"] is not None} == {
        item["id"] for item in media
    }
    assert events[-1]["name"] == "Stop_All" and events[-1]["media_id"] is None

    if args.bank:
        assert args.bank.stat().st_size == data["bank"]["size"]
        assert sha256(args.bank) == data["bank"]["sha256"]

    if args.wem_dir:
        for item in media:
            path = args.wem_dir / f"{item['id']}.wem"
            assert path.stat().st_size == item["size"]
            assert sha256(path) == item["sha256"]

    print("PS5 haptic inventory verified: 13 events, 12 media files, 317440 bytes.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
