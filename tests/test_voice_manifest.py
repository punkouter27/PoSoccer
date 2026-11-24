"""voices.json (po-voice-manifest/1) lists every face character x 5 emotions x 5 levels, with real file names."""

import json
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
VOICES = ROOT / "Assets" / "_Project" / "Data" / "Resources" / "Voices"
EMOTIONS = ["angry", "happy", "hurt", "insult", "celebrate"]
VOICE_KEYS = {"pitchHz", "formantScale", "breath", "rasp", "vibratoHz", "vibratoDepth", "tempo", "timbre"}


def face_roster() -> list[str]:
    names = []
    for asset in sorted((ROOT / "Assets" / "_Project" / "RL_Models").glob("*/Reward_*.asset")):
        text = asset.read_text(encoding="utf-8")
        face = re.search(r"faceTexture: \{fileID: (\d+)", text)
        if face and face.group(1) != "0":
            names.append(re.search(r"playerName: (\S+)", text).group(1).upper())
    return names


def load_manifest() -> dict:
    return json.loads((VOICES / "voices.json").read_text(encoding="utf-8"))


def test_manifest_header():
    manifest = load_manifest()
    assert manifest["schema"] == "po-voice-manifest/1"
    assert manifest["game"] == "PoSoccer"
    assert manifest["root"] == "Assets/_Project/Data/Resources/Voices"
    assert manifest["emotions"] == EMOTIONS
    assert manifest["levels"] == [1, 2, 3, 4, 5]
    assert manifest["fallback"] == "procedural"


def test_manifest_covers_every_face_character():
    roster = face_roster()
    assert len(roster) == 16
    listed = [character["name"] for character in load_manifest()["characters"]]
    assert sorted(listed) == sorted(roster)
    assert "BYE" not in listed


def test_every_cell_names_its_files_and_exists_flags_match_disk():
    for character in load_manifest()["characters"]:
        name = character["name"]
        assert character["id"] == name.lower()
        assert set(character["voice"]) == VOICE_KEYS
        assert character["voice"]["pitchHz"] > 0
        assert list(character["clips"]) == EMOTIONS
        for emotion in EMOTIONS:
            clips = character["clips"][emotion]
            assert [clip["level"] for clip in clips] == [1, 2, 3, 4, 5]
            for clip in clips:
                level = clip["level"]
                assert clip["file"] == f"{name.lower()}/{emotion}-{level}.wav"
                assert clip["exists"] == (VOICES / clip["file"]).is_file()
                assert [take["line"] for take in clip["takes"]] == [1, 2, 3, 4, 5]
                for take in clip["takes"]:
                    assert take["file"] == f"{name}_{emotion.upper()}_{take['line']}_{level}.wav"
                    assert take["exists"] == (VOICES / take["file"]).is_file()


def test_every_character_has_a_distinct_voice():
    voices = [json.dumps(character["voice"], sort_keys=True) for character in load_manifest()["characters"]]
    assert len(set(voices)) == len(voices)
