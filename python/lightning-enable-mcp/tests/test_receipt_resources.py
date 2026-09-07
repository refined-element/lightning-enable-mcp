"""The durable receipt log as an MCP resource, exercised through the real handlers.

What matters here is the same thing that matters for the tool: a preimage is not a receipt
number, it IS the proof of payment, and it must never leave the process. The resource
inherits that guarantee from ``ReceiptService.read_recent`` — these tests prove it end to
end rather than trusting the seam.

Mirrors ``dotnet/tests/LightningEnable.Mcp.Tests/Resources/ReceiptResourcesTests.cs``.
"""

import json

import pytest
from mcp.types import (
    ListResourcesRequest,
    ListResourceTemplatesRequest,
    ReadResourceRequest,
)
from pydantic import AnyUrl

from lightning_enable_mcp.receipt_service import (
    REDACTED,
    ReceiptService,
    is_sensitive_field,
    redact_receipt,
)
from lightning_enable_mcp.server import (
    RECEIPT_BY_HASH_URI_TEMPLATE,
    RECEIPTS_RESOURCE_MAX_ROWS,
    RECEIPTS_RESOURCE_URI,
    LightningEnableServer,
)


def _receipt(payment_hash: str, amount_sats: int, **extra) -> str:
    row = {
        "type": "payment_receipt",
        "kind": "l402",
        "timestamp": "2026-09-07T00:00:00.000Z",
        "wallet": "NWC",
        "amountSats": amount_sats,
        "paymentHash": payment_hash,
        **extra,
    }
    return json.dumps(row)


@pytest.fixture
def server(tmp_path) -> LightningEnableServer:
    """A server whose receipt log is a temp file, so nothing touches the real home dir."""
    srv = LightningEnableServer()
    srv.receipt_service = ReceiptService(
        wallet_label="NWC", receipts_path=tmp_path / "receipts.jsonl"
    )
    return srv


def _write(server: LightningEnableServer, *lines: str) -> None:
    server.receipt_service.path.write_text("\n".join(lines) + "\n", encoding="utf-8")


async def _list_resources(server: LightningEnableServer):
    handler = server.server.request_handlers[ListResourcesRequest]
    result = await handler(
        ListResourcesRequest(method="resources/list", params=None)
    )
    return result.root.resources


async def _list_templates(server: LightningEnableServer):
    handler = server.server.request_handlers[ListResourceTemplatesRequest]
    result = await handler(
        ListResourceTemplatesRequest(method="resources/templates/list", params=None)
    )
    return result.root.resourceTemplates


async def _read_text(server: LightningEnableServer, uri: str) -> str:
    handler = server.server.request_handlers[ReadResourceRequest]
    result = await handler(
        ReadResourceRequest(
            method="resources/read", params={"uri": AnyUrl(uri)}
        )
    )
    return result.root.contents[0].text


def _parse_json_lines(text: str) -> list[dict]:
    return [json.loads(line) for line in text.split("\n") if line.strip()]


# ── Listing ───────────────────────────────────────────────────────────────────


class TestResourceListing:
    @pytest.mark.asyncio
    async def test_list_resources_contains_the_receipt_log_uri(self, server):
        uris = [str(r.uri) for r in await _list_resources(server)]
        assert RECEIPTS_RESOURCE_URI in uris

    @pytest.mark.asyncio
    async def test_list_resource_templates_contains_the_per_payment_hash_template(self, server):
        templates = [t.uriTemplate for t in await _list_templates(server)]
        assert RECEIPT_BY_HASH_URI_TEMPLATE in templates

    @pytest.mark.asyncio
    async def test_the_log_resource_declares_json_lines(self, server):
        resource = next(
            r for r in await _list_resources(server) if str(r.uri) == RECEIPTS_RESOURCE_URI
        )
        assert resource.mimeType == "application/x-ndjson"
        assert resource.name

    @pytest.mark.asyncio
    async def test_the_read_result_also_declares_json_lines(self, server):
        """The listing's media type is a promise; the read has to keep it."""
        _write(server, _receipt("hash-a", 100))

        handler = server.server.request_handlers[ReadResourceRequest]
        result = await handler(
            ReadResourceRequest(
                method="resources/read", params={"uri": AnyUrl(RECEIPTS_RESOURCE_URI)}
            )
        )

        assert result.root.contents[0].mimeType == "application/x-ndjson"


# ── Reading the log ───────────────────────────────────────────────────────────


class TestReadingTheLog:
    @pytest.mark.asyncio
    async def test_returns_one_json_object_per_line(self, server):
        _write(server, _receipt("hash-a", 100), _receipt("hash-b", 250))

        rows = _parse_json_lines(await _read_text(server, RECEIPTS_RESOURCE_URI))

        assert len(rows) == 2
        assert rows[0]["paymentHash"] == "hash-a"
        assert rows[1]["amountSats"] == 250

    @pytest.mark.asyncio
    async def test_redacts_a_preimage_that_somehow_reached_the_file(self, server):
        # Receipts never carry a preimage by construction. The log is a plain file on the
        # operator's disk, though, so a hand-edit or a future writer could put one there —
        # and by then it is one read away from a model's context.
        preimage = "d0bf1ee9a1b2c3d4e5f60718293a4b5c6d7e8f900112233445566778899aabbcc"
        _write(server, _receipt("hash-a", 100, preimage=preimage))

        text = await _read_text(server, RECEIPTS_RESOURCE_URI)

        assert preimage not in text
        assert _parse_json_lines(text)[0]["preimage"] == REDACTED, (
            "the field is shown as withheld rather than silently dropped"
        )

    @pytest.mark.asyncio
    async def test_keeps_the_ordinary_fields(self, server):
        _write(server, _receipt("hash-a", 100))

        row = _parse_json_lines(await _read_text(server, RECEIPTS_RESOURCE_URI))[0]

        assert row["amountSats"] == 100
        assert row["wallet"] == "NWC"
        assert row["paymentHash"] == "hash-a"

    @pytest.mark.asyncio
    async def test_skips_a_torn_line_rather_than_failing_the_whole_read(self, server):
        _write(server, _receipt("hash-a", 100), "{not json", _receipt("hash-b", 200))

        assert len(_parse_json_lines(await _read_text(server, RECEIPTS_RESOURCE_URI))) == 2

    @pytest.mark.asyncio
    async def test_with_no_file_yet_is_empty_not_an_error(self, server):
        assert await _read_text(server, RECEIPTS_RESOURCE_URI) == ""

    @pytest.mark.asyncio
    async def test_is_capped_at_200_rows(self, server):
        _write(server, *[_receipt(f"hash-{i}", i + 1) for i in range(250)])

        rows = _parse_json_lines(await _read_text(server, RECEIPTS_RESOURCE_URI))

        assert len(rows) == RECEIPTS_RESOURCE_MAX_ROWS
        assert rows[-1]["paymentHash"] == "hash-249", "the most recent are kept"

    @pytest.mark.asyncio
    async def test_a_trailing_slash_still_resolves(self, server):
        _write(server, _receipt("hash-a", 100))

        assert _parse_json_lines(await _read_text(server, RECEIPTS_RESOURCE_URI + "/"))


# ── Reading one payment hash ──────────────────────────────────────────────────


class TestReadingOnePaymentHash:
    @pytest.mark.asyncio
    async def test_returns_only_that_payments_receipts(self, server):
        _write(
            server,
            _receipt("hash-a", 100),
            _receipt("hash-b", 200),
            _receipt("hash-a", 300),
        )

        rows = _parse_json_lines(
            await _read_text(server, "lightning-enable://receipts/hash-a")
        )

        assert [r["amountSats"] for r in rows] == [100, 300]

    @pytest.mark.asyncio
    async def test_also_redacts(self, server):
        preimage = "aa11bb22cc33dd44ee55ff6677889900aabbccddeeff00112233445566778899"
        _write(server, _receipt("hash-a", 100, preImage=preimage))

        text = await _read_text(server, "lightning-enable://receipts/hash-a")

        assert preimage not in text, "the check is on the property NAME, case-insensitively"

    @pytest.mark.asyncio
    async def test_an_unknown_payment_hash_fails_with_an_actionable_message(self, server):
        _write(server, _receipt("hash-a", 100))

        with pytest.raises(Exception) as excinfo:
            await _read_text(server, "lightning-enable://receipts/hash-nope")

        message = str(excinfo.value)
        assert "hash-nope" in message
        assert RECEIPTS_RESOURCE_URI in message

    @pytest.mark.asyncio
    async def test_an_unknown_resource_names_what_is_served(self, server):
        with pytest.raises(Exception) as excinfo:
            await _read_text(server, "lightning-enable://something-else")

        assert RECEIPTS_RESOURCE_URI in str(excinfo.value)


# ── The redactor itself ───────────────────────────────────────────────────────


class TestReceiptRedaction:
    @pytest.mark.parametrize(
        "field",
        [
            "preimage",
            "preImage",
            "PREIMAGE",
            "paymentPreimage",
            "secret",
            "macaroon",
            "nwcConnectionString",
            "apiKey",
        ],
    )
    def test_sensitive_fields_are_replaced(self, field):
        assert redact_receipt({field: "fixture-value-do-not-leak"})[field] == REDACTED

    @pytest.mark.parametrize(
        "field", ["paymentHash", "amountSats", "wallet", "revokePath"]
    )
    def test_ordinary_fields_are_untouched(self, field):
        assert redact_receipt({field: "keep-me"})[field] == "keep-me"

    def test_payment_hash_is_never_confused_for_a_preimage(self):
        # The payment hash is the SAFE reference and the whole point of the receipt log.
        assert is_sensitive_field("paymentHash") is False
        assert is_sensitive_field("preimage") is True

    def test_nested_objects_and_arrays_are_walked(self):
        redacted = redact_receipt(
            {"outer": {"preimage": "x"}, "list": [{"secret": "y"}]}
        )

        assert redacted["outer"]["preimage"] == REDACTED
        assert redacted["list"][0]["secret"] == REDACTED

    def test_a_deeply_nested_document_terminates(self):
        # Bounded depth: a pathological receipt must not hang the read path.
        node: object = 1
        for _ in range(200):
            node = [node]

        redact_receipt({"nested": node})  # must simply return

    def test_non_dict_values_pass_through(self):
        assert redact_receipt(42) == 42
        assert redact_receipt(None) is None
        assert redact_receipt("plain") == "plain"
