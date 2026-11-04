"""Tests for the ``docs`` tool (search and read the Lightning Enable docs).

The parser, ranking, snippets and truncation are pinned by the shared golden file
``shared/docs/docs-tool-cases.json`` (repo root), which the .NET suite
(``Tools/DocsToolTests.cs``) asserts on too, so the two ports cannot drift. That
fixture lives above this package and is not shipped in the sdist, so the golden tests
skip when it is absent; the cache and failure-mode tests always run.
"""

import json
from pathlib import Path

import pytest
from mcp.types import CallToolRequest, CallToolRequestParams

from lightning_enable_mcp import docs_service as ds
from lightning_enable_mcp.docs_service import (
    CACHE_TTL_SECONDS,
    DEFAULT_DOCS_URL,
    DOCS_URL_ENV_VAR,
    RETRY_AFTER_FAILURE_SECONDS,
    DocsService,
)
from lightning_enable_mcp.server import LightningEnableServer
from lightning_enable_mcp.tools.annotations import TOOL_ANNOTATIONS
from lightning_enable_mcp.tools.docs import docs


def _find_shared() -> Path | None:
    for parent in Path(__file__).resolve().parents:
        candidate = parent / "shared" / "docs" / "docs-tool-cases.json"
        if candidate.is_file():
            return candidate.parent
    return None


SHARED = _find_shared()
needs_shared = pytest.mark.skipif(SHARED is None, reason="shared/docs fixture not present")

MINI = "\n".join(
    [
        "preamble",
        "=" * 78,
        "# Page One",
        "Source: https://docs.example.test/one",
        "=" * 78,
        "",
        "# Page One",
        "",
        "Alpha beta gamma.",
        "",
    ]
)


def _cases() -> dict:
    assert SHARED is not None
    return json.loads((SHARED / "docs-tool-cases.json").read_text(encoding="utf-8"))


def _pages():
    assert SHARED is not None
    return ds.parse((SHARED / "llms-full.sample.txt").read_text(encoding="utf-8"))


class Clock:
    def __init__(self) -> None:
        self.now = 1000.0

    def __call__(self) -> float:
        return self.now


class Fetcher:
    """Counts fetches; returns ``text`` or raises ``error``."""

    def __init__(self, text: str = MINI) -> None:
        self.text = text
        self.error: Exception | None = None
        self.calls = 0

    async def __call__(self, url: str) -> str:
        self.calls += 1
        if self.error is not None:
            raise self.error
        return self.text


# ══ Shared golden cases (identical expectations in the .NET suite) ═══════════


@needs_shared
class TestSharedGoldenCases:
    def test_parser_finds_every_page_and_heading(self):
        expected = _cases()["pages"]
        actual = [
            {
                "path": p.path,
                "title": p.title,
                "url": p.url,
                "headings": [s.heading for s in p.sections if s.heading],
            }
            for p in _pages()
        ]
        assert actual == expected

    def test_a_heading_inside_a_code_fence_is_not_a_section(self):
        auth = next(p for p in _pages() if p.path == "/api-reference/authentication")
        assert all("not a heading" not in (s.heading or "") for s in auth.sections)

    @pytest.mark.parametrize("case", _cases()["search"] if SHARED else [], ids=lambda c: c["query"])
    def test_search(self, case):
        result = ds.search(_pages(), case["query"], case.get("limit"))
        assert result["success"] is True
        assert result["results"] == case["expected"]
        assert result["count"] == len(case["expected"])

    @pytest.mark.parametrize(
        "case",
        _cases()["read"] if SHARED else [],
        ids=lambda c: f"{c['path']}|{c.get('section')}|{c.get('maxBytes')}",
    )
    def test_read(self, case):
        result = ds.read(
            _pages(), case["path"], case.get("section"), case.get("maxBytes", ds.MAX_READ_BYTES)
        )
        expected = dict(case["expected"])
        contains = expected.pop("errorContains", None)
        if contains is not None:
            assert result["success"] is False
            assert contains in result.pop("error")
        result.pop("hint", None)
        assert result == expected

    async def test_env_var_points_the_tool_at_a_local_file(self, monkeypatch):
        monkeypatch.setenv(DOCS_URL_ENV_VAR, (SHARED / "llms-full.sample.txt").as_uri())
        result = json.loads(await docs("search", query="NIP-04", docs_service=DocsService()))
        assert result["success"] is True
        assert result["results"][0]["path"] == "/nwc-setup/account-setup"
        assert result["source"].startswith("file:")


# ══ Ranking and truncation rules ═════════════════════════════════════════════


class TestRules:
    def test_title_matches_outrank_body_matches(self):
        text = "\n".join(
            [
                "=" * 78,
                "# Body page",
                "Source: https://d.test/body",
                "=" * 78,
                "",
                "relay relay relay relay",
                "=" * 78,
                "# Relay",
                "Source: https://d.test/relay",
                "=" * 78,
                "",
                "nothing to see",
            ]
        )
        result = ds.search(ds.parse(text), "relay")
        # 5 (title) beats 4 (body).
        assert [r["path"] for r in result["results"]] == ["/relay", "/body"]

    def test_matching_more_query_terms_beats_a_higher_score(self):
        text = "\n".join(
            [
                "=" * 78,
                "# A",
                "Source: https://d.test/a",
                "=" * 78,
                "",
                "wallet wallet wallet wallet wallet wallet",
                "=" * 78,
                "# B",
                "Source: https://d.test/b",
                "=" * 78,
                "",
                "wallet relay",
            ]
        )
        result = ds.search(ds.parse(text), "wallet relay")
        assert result["results"][0]["path"] == "/b"

    def test_limit_is_clamped_to_20(self):
        body = "\n\n".join(f"## Topic {n}\n\nkeyword" for n in range(30))
        text = "\n".join(["=" * 78, "# P", "Source: https://d.test/p", "=" * 78, "", body])
        assert ds.search(ds.parse(text), "keyword", 500)["count"] == 20

    def test_query_without_letters_or_digits_is_an_error(self):
        result = ds.search(ds.parse(MINI), "?!")
        assert result["success"] is False
        assert "letters or digits" in result["error"]

    def test_read_caps_at_12000_bytes_on_a_line_boundary(self):
        lines = [f"line {n:05d} " + "x" * 90 for n in range(300)]
        text = "\n".join(["=" * 78, "# Big", "Source: https://d.test/big", "=" * 78, ""] + lines)
        result = ds.read(ds.parse(text), "/big")
        assert result["truncated"] is True
        assert len(result["text"].encode("utf-8")) <= ds.MAX_READ_BYTES
        assert result["text"].endswith("x")  # whole lines only
        assert "hint" in result

    def test_truncation_never_splits_a_multibyte_character(self):
        text, cut = ds.truncate_utf8("ab€c", 4)  # euro sign is 3 bytes
        assert (text, cut) == ("ab", True)

    def test_crlf_corpus_parses_like_lf(self):
        assert [p.path for p in ds.parse(MINI.replace("\n", "\r\n"))] == ["/one"]

    def test_a_divider_inside_a_page_is_not_a_page_boundary(self):
        text = MINI + "\n" + "=" * 78 + "\nnot a header\n"
        pages = ds.parse(text)
        assert len(pages) == 1
        assert "not a header" in pages[0].body


# ══ Cache and failure modes ══════════════════════════════════════════════════


class TestCache:
    async def test_second_call_within_an_hour_uses_the_cache(self):
        fetch, clock = Fetcher(), Clock()
        service = DocsService("https://docs.example.test/llms-full.txt", fetch, clock)
        await service.search("alpha")
        clock.now += CACHE_TTL_SECONDS - 1
        await service.read("/one")
        assert fetch.calls == 1

    async def test_expired_cache_is_refreshed(self):
        fetch, clock = Fetcher(), Clock()
        service = DocsService("https://docs.example.test/llms-full.txt", fetch, clock)
        await service.search("alpha")
        clock.now += CACHE_TTL_SECONDS
        fetch.text = MINI.replace("Alpha", "Delta")
        result = await service.search("delta")
        assert fetch.calls == 2
        assert result["count"] == 1

    async def test_failed_refresh_serves_the_stale_copy_and_backs_off(self):
        fetch, clock = Fetcher(), Clock()
        service = DocsService("https://docs.example.test/llms-full.txt", fetch, clock)
        await service.search("alpha")
        clock.now += CACHE_TTL_SECONDS + 1
        fetch.error = RuntimeError("connection refused")

        result = await service.search("alpha")
        assert result["success"] is True
        assert result["stale"] is True
        assert "connection refused" in result["warning"]
        assert fetch.calls == 2

        # Within the back-off window the stale copy is served without refetching.
        clock.now += RETRY_AFTER_FAILURE_SECONDS - 1
        assert (await service.read("/one"))["stale"] is True
        assert fetch.calls == 2

        # After it, the next call tries again, and a success clears the stale flag.
        clock.now += 2
        fetch.error = None
        result = await service.read("/one")
        assert fetch.calls == 3
        assert "stale" not in result

    async def test_failure_with_no_cache_names_the_url(self):
        fetch = Fetcher()
        fetch.error = RuntimeError("HTTP 503")
        service = DocsService("https://docs.example.test/llms-full.txt", fetch, Clock())
        result = await service.search("alpha")
        assert result["success"] is False
        assert "https://docs.example.test/llms-full.txt" in result["error"]
        assert "HTTP 503" in result["error"]
        assert DOCS_URL_ENV_VAR in result["error"]

    async def test_a_file_with_no_pages_is_a_failure_not_an_empty_corpus(self):
        service = DocsService("https://docs.example.test/x", Fetcher("<html>nope</html>"), Clock())
        result = await service.search("alpha")
        assert result["success"] is False
        assert "no pages found" in result["error"]

    @pytest.mark.parametrize(
        "url",
        ["http://docs.example.test/llms-full.txt", "ftp://docs.example.test/x", "not a url"],
    )
    async def test_non_https_sources_are_refused(self, url):
        fetch = Fetcher()
        result = await DocsService(url, fetch, Clock()).search("alpha")
        assert result["success"] is False
        assert DOCS_URL_ENV_VAR in result["error"]
        assert fetch.calls == 0

    @pytest.mark.parametrize("url", ["http://localhost:8080/x", "http://127.0.0.1/x"])
    async def test_loopback_http_is_allowed(self, url):
        result = await DocsService(url, Fetcher(), Clock()).search("alpha")
        assert result["success"] is True

    def test_default_source_is_the_published_corpus(self, monkeypatch):
        monkeypatch.delenv(DOCS_URL_ENV_VAR, raising=False)
        assert (
            DocsService().source_url
            == DEFAULT_DOCS_URL
            == ("https://docs.lightningenable.com/llms-full.txt")
        )

    async def test_missing_file_url_is_a_clear_error(self, tmp_path):
        url = (tmp_path / "missing.txt").as_uri()
        result = await DocsService(url).search("alpha")
        assert result["success"] is False
        assert url in result["error"]


# ══ Tool surface ═════════════════════════════════════════════════════════════


class TestTool:
    async def test_unknown_action_lists_the_valid_ones(self):
        result = json.loads(
            await docs("browse", docs_service=DocsService("https://x.test/a", Fetcher(), Clock()))
        )
        assert result["success"] is False
        assert result["valid_actions"] == ["search", "read"]

    async def test_search_without_query_is_an_error_without_fetching(self):
        fetch = Fetcher()
        result = json.loads(
            await docs("search", docs_service=DocsService("https://x.test/a", fetch, Clock()))
        )
        assert result["success"] is False
        assert "query" in result["error"]
        assert fetch.calls == 0

    async def test_read_without_path_is_an_error_without_fetching(self):
        fetch = Fetcher()
        result = json.loads(
            await docs("read", docs_service=DocsService("https://x.test/a", fetch, Clock()))
        )
        assert result["success"] is False
        assert "path" in result["error"]
        assert fetch.calls == 0

    @pytest.mark.parametrize("limit", ["many", True, 2.5])
    async def test_bad_limit_is_an_error(self, limit):
        service = DocsService("https://x.test/a", Fetcher(), Clock())
        result = json.loads(await docs("search", query="alpha", limit=limit, docs_service=service))
        assert result["success"] is False
        assert "limit" in result["error"]

    def test_annotations_are_read_only_idempotent_open_world(self):
        annotations = TOOL_ANNOTATIONS["docs"]
        assert annotations.readOnlyHint is True
        assert annotations.idempotentHint is True
        assert annotations.openWorldHint is True

    async def test_server_dispatches_docs_without_a_wallet(self, monkeypatch):
        server = LightningEnableServer()
        server.docs_service = DocsService("https://x.test/a", Fetcher(), Clock())
        assert server.wallet is None
        handler = server.server.request_handlers[CallToolRequest]
        req = CallToolRequest(
            method="tools/call",
            params=CallToolRequestParams(name="docs", arguments={"action": "read", "path": "/one"}),
        )
        result = json.loads((await handler(req)).root.content[0].text)
        assert result["success"] is True
        assert result["title"] == "Page One"
