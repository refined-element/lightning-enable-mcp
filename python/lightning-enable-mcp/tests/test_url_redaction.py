"""
Tests for _redact_url_for_display — the access_l402_resource URL redaction that
keeps query strings / fragments / userinfo (where tokens live) out of stderr and
logs (engineering standard #5: never log credentials).
"""

from lightning_enable_mcp.tools.access_resource import _redact_url_for_display


def test_query_string_is_redacted():
    out = _redact_url_for_display("https://api.example.com/v1/data?token=SECRET&q=1")
    assert "SECRET" not in out
    assert "token" not in out
    assert "api.example.com" in out
    assert "redacted" in out


def test_fragment_is_redacted():
    out = _redact_url_for_display("https://example.com/p#access_token=SECRET")
    assert "SECRET" not in out
    assert "access_token" not in out


def test_userinfo_is_stripped():
    out = _redact_url_for_display("https://user:p4ssw0rd@example.com/path")
    assert "p4ssw0rd" not in out
    assert "user" not in out
    assert "example.com/path" in out


def test_clean_url_passes_through_without_marker():
    out = _redact_url_for_display("https://example.com/clean/path")
    assert out == "https://example.com/clean/path"
    assert "redacted" not in out


def test_port_is_preserved_but_credentials_are_not():
    out = _redact_url_for_display("https://user:pw@example.com:8443/x?k=v")
    assert "example.com:8443" in out
    assert "pw" not in out and "v" not in out


def test_long_url_is_truncated_after_redaction():
    long_host = "https://" + ("a" * 80) + ".com/path?token=SECRET"
    out = _redact_url_for_display(long_host)
    assert "SECRET" not in out
    assert out.endswith("...")
