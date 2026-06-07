"""
Tests for PaymentHistoryService.

The legacy BudgetManager (which combined limits + history + session tracking) was
removed in favor of the .NET-style split: BudgetService owns spending limits and
approval, PaymentHistoryService owns the session audit trail. The still-relevant
history cases from the old BudgetManager tests live here now.
"""

from datetime import datetime, timezone, timedelta

from lightning_enable_mcp.payment_history_service import (
    PaymentHistoryService,
    PaymentRecord,
    MAX_PAYMENT_RECORDS,
)


class TestPaymentHistoryService:
    """Tests for PaymentHistoryService."""

    def test_record_payment(self):
        svc = PaymentHistoryService()

        record = svc.record_payment(
            url="https://api.example.com/data",
            amount_sats=100,
        )

        assert record.url == "https://api.example.com/data"
        assert record.amount_sats == 100
        assert record.status == "success"
        assert svc.total_payments == 1
        assert svc.total_sats_spent == 100

    def test_record_multiple_payments(self):
        svc = PaymentHistoryService()

        svc.record_payment(url="https://api1.example.com", amount_sats=100)
        svc.record_payment(url="https://api2.example.com", amount_sats=200)

        assert svc.total_payments == 2
        assert svc.total_sats_spent == 300

    def test_failed_payment_not_counted_in_spent(self):
        svc = PaymentHistoryService()

        svc.record_payment(url="https://api.example.com", amount_sats=100, status="failed")

        # The record is kept (for the audit trail), but failed payments don't
        # count toward total spent.
        assert svc.total_payments == 1
        assert svc.total_sats_spent == 0

    def test_get_history_limit(self):
        svc = PaymentHistoryService()

        for i in range(5):
            svc.record_payment(url=f"https://api{i}.example.com", amount_sats=10)

        history = svc.get_history(limit=3)
        assert len(history) == 3

    def test_get_history_most_recent_first(self):
        svc = PaymentHistoryService()

        svc.record_payment(url="https://first.example.com", amount_sats=10)
        svc.record_payment(url="https://second.example.com", amount_sats=10)

        history = svc.get_history()
        assert history[0].url == "https://second.example.com"

    def test_get_history_since_filter(self):
        svc = PaymentHistoryService()

        # Inject an old payment directly.
        svc._payments.append(
            PaymentRecord(
                url="https://old.example.com",
                amount_sats=10,
                timestamp=datetime(2024, 1, 1, tzinfo=timezone.utc),
            )
        )
        svc.record_payment(url="https://new.example.com", amount_sats=10)

        since = datetime.now(timezone.utc) - timedelta(hours=1)
        history = svc.get_history(since=since)

        assert len(history) == 1
        assert history[0].url == "https://new.example.com"

    def test_clear(self):
        svc = PaymentHistoryService()
        svc.record_payment(url="https://api.example.com", amount_sats=10)
        svc.clear()
        assert svc.total_payments == 0

    def test_history_is_bounded(self):
        """The in-memory list is capped so a long session can't grow unbounded."""
        svc = PaymentHistoryService()

        for i in range(MAX_PAYMENT_RECORDS + 50):
            svc.record_payment(url=f"https://api{i}.example.com", amount_sats=1)

        assert svc.total_payments == MAX_PAYMENT_RECORDS
        # Oldest were dropped; the most recent are retained.
        urls = {r.url for r in svc.get_history(limit=MAX_PAYMENT_RECORDS)}
        assert f"https://api{MAX_PAYMENT_RECORDS + 49}.example.com" in urls
        assert "https://api0.example.com" not in urls


class TestPaymentRecord:
    """Tests for PaymentRecord — including the funds-safety property that the
    preimage is never stored."""

    def test_no_preimage_field(self):
        """FUNDS-SAFETY: PaymentRecord must not carry a preimage at all."""
        record = PaymentRecord(
            url="https://api.example.com",
            amount_sats=100,
            timestamp=datetime.now(timezone.utc),
        )
        assert not hasattr(record, "preimage")
        # record_payment also has no preimage parameter.
        import inspect

        sig = inspect.signature(PaymentHistoryService.record_payment)
        assert "preimage" not in sig.parameters

    def test_to_dict_has_no_preimage(self):
        record = PaymentRecord(
            url="https://api.example.com",
            amount_sats=100,
            timestamp=datetime(2024, 6, 15, 12, 0, 0, tzinfo=timezone.utc),
            invoice="lnbc100n1...",
        )
        data = record.to_dict()

        assert data["url"] == "https://api.example.com"
        assert data["amount_sats"] == 100
        assert data["status"] == "success"
        assert "timestamp" in data
        assert "preimage" not in data

    def test_to_dict_truncates_long_invoice(self):
        long_invoice = "lnbc100n1p" + "x" * 100
        record = PaymentRecord(
            url="https://api.example.com",
            amount_sats=100,
            timestamp=datetime.now(timezone.utc),
            invoice=long_invoice,
        )
        data = record.to_dict()

        assert len(data["invoice"]) == 23  # 20 chars + "..."
        assert data["invoice"].endswith("...")

    def test_to_dict_omits_invoice_when_absent(self):
        record = PaymentRecord(
            url="https://api.example.com",
            amount_sats=100,
            timestamp=datetime.now(timezone.utc),
        )
        data = record.to_dict()
        assert "invoice" not in data
