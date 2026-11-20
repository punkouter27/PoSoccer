"""scripts/unity-free-check/extract.py lifts C# members out of runtime classes; its parser is pure Python."""

from conftest import load_script

extract = load_script("scripts/unity-free-check/extract.py", "extract")


def test_top_level_members_splits_fields_and_methods():
    body = """
        public const int A = 1;
        // a comment with { brace
        public static int Twice(int x) { return x * 2; }
        static readonly int[] B = { 1, 2 };
    """
    chunks = extract.top_level_members(body)
    assert [extract.declared_name(c) for c in chunks] == ["A", "Twice", "B"]


def test_top_level_members_ignores_braces_in_strings():
    body = 'string S() { return $"{spot}}"; }\nint N;'
    chunks = extract.top_level_members(body)
    assert len(chunks) == 2
    assert extract.declared_name(chunks[1]) == "N"


def test_class_body_returns_inner_text():
    src = "namespace X { public sealed class Foo : Bar { int a; void M() { } } }"
    assert extract.class_body(src, "Foo").strip() == "int a; void M() { }"


def test_declared_name_skips_attributes():
    assert extract.declared_name("[SerializeField] private float _speed = 2f;") == "_speed"


def test_helpers_point_at_real_sources(root):
    for rel, (cls, _members) in extract.HELPERS.items():
        src = (root / rel).read_text(encoding="utf-8-sig")
        assert f"class {cls}" in src
