"""
Speaker-attribution tests for the VTT parser in agent/spine.py.

These target the negative/edge cases the naive parser got wrong (or never handled): malformed
cue markup, mixed line endings, voice-tag variants, and metadata blocks that must not leak into
the transcript. `test_spine.py` keeps the original exact-equality tests untouched; this file adds
one test per documented edge case, named descriptively, with the negative case explicit.
"""
from agent.spine import parse_vtt, parse_transcript_segments, UNKNOWN_SPEAKER


# ---------- header / BOM ----------
def test_bom_prefix_before_webvtt_header_is_dropped():
    vtt = "﻿WEBVTT\n\n00:00:01.000 --> 00:00:02.000\n<v Alice>Hi\n"
    assert parse_vtt(vtt) == "Alice: Hi"


def test_webvtt_header_with_trailing_text_is_dropped():
    vtt = "WEBVTT - Meeting recording\n\n00:00:01.000 --> 00:00:02.000\n<v Alice>Hi\n"
    out = parse_vtt(vtt)
    assert "WEBVTT" not in out
    assert out == "Alice: Hi"


# ---------- NOTE / STYLE / REGION metadata blocks ----------
def test_single_line_note_block_is_dropped():
    vtt = ("WEBVTT\n\nNOTE this is a comment\n\n"
           "00:00:01.000 --> 00:00:02.000\n<v Alice>Hi\n")
    out = parse_vtt(vtt)
    assert "comment" not in out
    assert out == "Alice: Hi"


def test_multi_line_note_block_terminated_by_blank_line_is_dropped():
    vtt = ("WEBVTT\n\nNOTE\nThis spans\nseveral lines\n\n"
           "00:00:01.000 --> 00:00:02.000\n<v Alice>Hi\n")
    out = parse_vtt(vtt)
    assert "spans" not in out and "several" not in out
    assert out == "Alice: Hi"


def test_style_block_body_does_not_leak_as_text():
    vtt = ("WEBVTT\n\nSTYLE\n::cue { background: black; }\n\n"
           "00:00:01.000 --> 00:00:02.000\n<v Alice>Hi\n")
    out = parse_vtt(vtt)
    assert "cue" not in out and "background" not in out
    assert out == "Alice: Hi"


def test_region_block_body_does_not_leak_as_text():
    vtt = ("WEBVTT\n\nREGION\nid:fred\nwidth:40%\nlines:3\n\n"
           "00:00:01.000 --> 00:00:02.000\n<v Alice>Hi\n")
    out = parse_vtt(vtt)
    assert "id:fred" not in out and "width" not in out
    assert out == "Alice: Hi"


# ---------- cue identifiers ----------
def test_numeric_cue_identifier_is_dropped():
    vtt = "WEBVTT\n\n3\n00:00:01.000 --> 00:00:02.000\n<v Alice>Hi\n"
    assert parse_vtt(vtt) == "Alice: Hi"


def test_alphanumeric_cue_identifier_is_dropped():
    vtt = "WEBVTT\n\nintro-1\n00:00:01.000 --> 00:00:02.000\n<v Alice>Hi\n"
    out = parse_vtt(vtt)
    assert "intro-1" not in out
    assert out == "Alice: Hi"


def test_uuid_cue_identifier_is_dropped():
    vtt = ("WEBVTT\n\n550e8400-e29b-41d4-a716-446655440000\n"
           "00:00:01.000 --> 00:00:02.000\n<v Alice>Hi\n")
    out = parse_vtt(vtt)
    assert "550e8400" not in out
    assert out == "Alice: Hi"


# ---------- timestamp line with cue settings ----------
def test_timestamp_line_with_cue_settings_is_dropped():
    vtt = ("WEBVTT\n\n00:00:01.000 --> 00:00:03.000 align:start position:0% line:90%\n"
           "<v Alice>Hi\n")
    out = parse_vtt(vtt)
    assert "align" not in out and "position" not in out and "-->" not in out
    assert out == "Alice: Hi"


# ---------- voice tag variants ----------
def test_closing_voice_tag_is_stripped_not_leaked():
    # The original parser's naive "<v ".replace + partition(">") left a trailing "</v>" on the
    # utterance - prove that bug is fixed.
    vtt = "WEBVTT\n\n00:00:01.000 --> 00:00:02.000\n<v Alice>Hello</v>\n"
    assert parse_vtt(vtt) == "Alice: Hello"


def test_voice_tag_with_single_class_is_handled():
    vtt = "WEBVTT\n\n00:00:01.000 --> 00:00:02.000\n<v.loud Alice>Hi</v>\n"
    assert parse_vtt(vtt) == "Alice: Hi"


def test_voice_tag_with_multiple_classes_and_multiword_name_is_handled():
    vtt = "WEBVTT\n\n00:00:01.000 --> 00:00:02.000\n<v.first.loud Bob Smith>Hey</v>\n"
    assert parse_vtt(vtt) == "Bob Smith: Hey"


def test_multiple_voice_spans_in_one_cue_produce_two_lines():
    vtt = "WEBVTT\n\n00:00:01.000 --> 00:00:02.000\n<v Alice>Hi</v> <v Bob>Yo</v>\n"
    assert parse_vtt(vtt) == "Alice: Hi\nBob: Yo"


def test_empty_voice_tag_no_name_attributes_to_unknown_constant():
    vtt = "WEBVTT\n\n00:00:01.000 --> 00:00:02.000\n<v>Hi</v>\n"
    assert parse_vtt(vtt) == f"{UNKNOWN_SPEAKER}: Hi"


def test_empty_voice_tag_with_space_no_name_attributes_to_unknown_constant():
    vtt = "WEBVTT\n\n00:00:01.000 --> 00:00:02.000\n<v >Hi</v>\n"
    assert parse_vtt(vtt) == f"{UNKNOWN_SPEAKER}: Hi"


# ---------- inline cue tags / entities ----------
def test_inline_styling_tags_are_stripped_but_words_kept():
    vtt = ("WEBVTT\n\n00:00:01.000 --> 00:00:02.000\n"
           "<v Alice>Hello <b>bold</b> <i>italic</i> <u>underline</u> "
           "<c.highlight>styled</c> <ruby>base<rt>ann</rt></ruby> <lang en>lang</lang> end</v>\n")
    out = parse_vtt(vtt)
    assert out == "Alice: Hello bold italic underline styled base ann lang end"
    for tag in ("<b>", "</b>", "<i>", "</i>", "<u>", "</u>", "<c.highlight>", "</c>",
                "<ruby>", "</ruby>", "<rt>", "</rt>", "<lang en>", "</lang>"):
        assert tag not in out


def test_karaoke_inline_timestamps_are_removed():
    vtt = ("WEBVTT\n\n00:00:01.000 --> 00:00:03.000\n"
           "<v Alice>Hello <00:00:01.500> there <00:00:02.000> friend</v>\n")
    out = parse_vtt(vtt)
    assert "00:00:01.500" not in out and "00:00:02.000" not in out
    assert out == "Alice: Hello there friend"


def test_html_entities_are_decoded():
    vtt = ("WEBVTT\n\n00:00:01.000 --> 00:00:02.000\n"
           "<v Alice>Tom &amp; Jerry &lt;3&gt; &lrm;&rlm;done</v>\n")
    out = parse_vtt(vtt)
    assert out == "Alice: Tom & Jerry <3> ‎‏done"
    assert "&amp;" not in out and "&lt;" not in out and "&gt;" not in out


def test_nbsp_entity_decodes_to_real_nbsp_character():
    vtt = "WEBVTT\n\n00:00:01.000 --> 00:00:02.000\n<v Alice>A&nbsp;B</v>\n"
    out = parse_vtt(vtt)
    assert "&nbsp;" not in out
    # &nbsp; decodes to a real U+00A0 - content the speaker "said" - so it must survive as
    # the actual character, not be collapsed to a plain space. Pin the exact string (asserting
    # merely that a plain space is present would pass vacuously via the "Alice: " prefix).
    assert out == "Alice: A\xa0B"


# ---------- multi-line payloads and cross-cue merging ----------
def test_multiline_cue_payload_joins_under_one_speaker():
    vtt = ("WEBVTT\n\n00:00:01.000 --> 00:00:04.000\n"
           "<v Alice>This spans\nmultiple physical lines\nof one cue</v>\n")
    assert parse_vtt(vtt) == "Alice: This spans multiple physical lines of one cue"


def test_consecutive_cues_from_same_speaker_are_merged():
    vtt = ("WEBVTT\n\n00:00:01.000 --> 00:00:02.000\n<v Alice>First part.\n\n"
           "00:00:02.000 --> 00:00:03.000\n<v Alice>Second part.\n")
    assert parse_vtt(vtt) == "Alice: First part. Second part."


def test_different_speakers_are_not_merged():
    vtt = ("WEBVTT\n\n00:00:01.000 --> 00:00:02.000\n<v Alice>Hi\n\n"
           "00:00:02.000 --> 00:00:03.000\n<v Bob>Yo\n")
    assert parse_vtt(vtt) == "Alice: Hi\nBob: Yo"


# ---------- pre-flattened / non-VTT input ----------
def test_preflattened_name_text_lines_are_preserved_not_double_prefixed():
    out = parse_vtt("Alice: just do it\nBob: sounds good")
    assert out == "Alice: just do it\nBob: sounds good"


def test_non_vtt_arbitrary_text_never_raises_and_is_returned_sanely():
    out = parse_vtt("just some random notes\nwith no structure at all")
    assert out == "just some random notes\nwith no structure at all"


def test_empty_string_input_returns_empty_string():
    assert parse_vtt("") == ""


def test_garbage_and_unbalanced_tags_never_raise():
    garbage = "WEBVTT\n\n00:00:01.000 --> 00:00:02.000\n<v Alice>unterminated <i>tag and <<<>>> junk\n"
    out = parse_vtt(garbage)
    assert isinstance(out, str)
    # <i> is a well-formed inline tag and is stripped; the unbalanced <<<>>> junk is not a
    # valid tag, so it is passed through verbatim rather than dropped or crashing.
    assert out == "Alice: unterminated tag and <<<>>> junk"


# ---------- line endings ----------
def test_windows_crlf_line_endings_are_handled_with_no_cr_in_output():
    vtt = "WEBVTT\r\n\r\n00:00:01.000 --> 00:00:02.000\r\n<v Alice>Hello\r\n"
    out = parse_vtt(vtt)
    assert "\r" not in out
    assert out == "Alice: Hello"


def test_stray_lone_cr_is_handled_with_no_cr_in_output():
    vtt = "WEBVTT\r\n\r00:00:01.000 --> 00:00:02.000\r<v Alice>Hi\r"
    out = parse_vtt(vtt)
    assert "\r" not in out
    assert out == "Alice: Hi"


# ---------- fixture / regression parity with test_spine.py ----------
def test_original_exact_equality_case_still_matches():
    vtt = ("WEBVTT\n\n1\n00:00:01.000 --> 00:00:03.000\n<v Alice>Hello there\n\n"
           "00:00:04.000 --> 00:00:05.000\n<v Bob>Hi Alice\n")
    assert parse_vtt(vtt) == "Alice: Hello there\nBob: Hi Alice"


def test_untagged_plain_line_case_still_matches():
    assert parse_vtt("WEBVTT\n\n2\n00:00:01.000 --> 00:00:02.000\nplain line\n") == "plain line"


# ---------- parse_transcript_segments helper, directly ----------
def test_parse_transcript_segments_returns_speaker_none_for_unattributed_text():
    segs = parse_transcript_segments("WEBVTT\n\n00:00:01.000 --> 00:00:02.000\nplain line\n")
    assert segs == [(None, "plain line")]


def test_parse_transcript_segments_returns_one_tuple_per_voice_span():
    segs = parse_transcript_segments(
        "WEBVTT\n\n00:00:01.000 --> 00:00:02.000\n<v Alice>Hi</v> <v Bob>Yo</v>\n")
    assert segs == [("Alice", "Hi"), ("Bob", "Yo")]


def test_parse_transcript_segments_never_raises_on_none_like_or_weird_input():
    # Defensive: parse_vtt's contract is "never raises" even outside its typed str contract.
    for bad in ("", "﻿", "<<<>>>", "NOTE\n\n", "STYLE\n\n"):
        out = parse_vtt(bad)
        assert isinstance(out, str)


# ---------- adversarial-review regressions (production-hardening) ----------
def test_unterminated_voice_tag_does_not_redos_hang():
    # An unterminated "<v" followed by a long run of dots previously made the voice-tag regex
    # backtrack exponentially and hang the whole pipeline forever (worse than raising, since the
    # module's try/except cannot catch a runaway regex). Must now be linear and near-instant.
    import time
    start = time.perf_counter()
    out = parse_vtt("<v" + "." * 500)
    elapsed = time.perf_counter() - start
    assert isinstance(out, str)
    assert elapsed < 1.0  # fixed regex parses in microseconds; 1s is a huge, non-flaky margin


def test_literal_arrow_inside_dialogue_is_not_mistaken_for_timing_and_dropped():
    # "-->" can legitimately appear in what someone says. A bare substring test used to treat the
    # whole utterance as a timing line and drop it; a real timestamp regex must keep it.
    vtt = ("WEBVTT\n\n00:00:01.000 --> 00:00:02.000\n<v Alice>compare a --> b now</v>\n\n"
           "00:00:05.000 --> 00:00:06.000\n<v Bob>after\n")
    assert parse_vtt(vtt) == "Alice: compare a --> b now\nBob: after"


def test_bare_style_keyword_as_cue_id_before_timing_line_keeps_dialogue():
    # A bare "STYLE" sitting where a cue identifier goes (immediately before a real timing line)
    # must be treated as a cue id, not a STYLE metadata block - otherwise the block-skip ate the
    # timing line and the dialogue with it.
    vtt = "WEBVTT\n\nSTYLE\n00:00:01.000 --> 00:00:02.000\n<v Alice>Hi\n"
    assert parse_vtt(vtt) == "Alice: Hi"


def test_genuine_style_block_body_is_still_dropped():
    # ...but a real STYLE block (keyword followed by its body, not a timing line) is still dropped.
    vtt = ("WEBVTT\n\nSTYLE\n::cue { color: red }\n\n"
           "00:00:01.000 --> 00:00:02.000\n<v Alice>Hi\n")
    assert parse_vtt(vtt) == "Alice: Hi"


def test_two_different_anonymous_speakers_are_not_merged():
    # Two distinct unnamed <v> cues both attribute to "Unknown", but they are different people -
    # the same-speaker merge must NOT fuse them into one continuous turn.
    vtt = ("WEBVTT\n\n00:00:01.000 --> 00:00:02.000\n<v>first anon</v>\n\n"
           "00:00:03.000 --> 00:00:04.000\n<v>second anon</v>\n")
    assert parse_vtt(vtt) == "Unknown: first anon\nUnknown: second anon"
