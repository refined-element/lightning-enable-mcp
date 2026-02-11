"""
Tests for Budget Manager
"""

import pytest
from datetime import datetime, timezone, timedelta
from lightning_enable_mcp.budget import (
    BudgetManager,
    BudgetExceededError,
    PaymentRecord,
)


class TestBudgetManager:
    """Tests for BudgetManager."""

    def test_default_limits(self):
        """Test default budget limits are set."""
        manager = BudgetManager()

        assert manager.max_per_request == 1000
        assert manager.max_per_session == 10000

    def test_custom_limits(self):
        """Test custom budget limits."""
        manager = BudgetManager(max_per_request=500, max_per_session=5000)

        assert manager.max_per_request == 500
        assert manager.max_per_session == 5000

    def test_check_payment_within_budget(self):
        """Test payment check passes within budget."""
        manager = BudgetManager(max_per_request=1000, max_per_session=10000)

        # Should not raise
        manager.check_payment(500)

    def test_check_payment_exceeds_per_request(self):
        """Test payment check fails when exceeding per-request limit."""
        manager = BudgetManager(max_per_request=1000, max_per_session=10000)

        with pytest.raises(BudgetExceededError, match="per-request limit"):
            manager.check_payment(1500)

    def test_check_payment_exceeds_session(self):
        """Test payment check fails when exceeding session limit."""
        manager = BudgetManager(max_per_request=1000, max_per_session=1000)
        manager.session_spent = 800

        with pytest.raises(BudgetExceededError, match="session limit"):
            manager.check_payment(500)

    def test_check_payment_with_override(self):
        """Test payment check with per-request override."""
        manager = BudgetManager(max_per_request=1000, max_per_session=10000)

        # Should not raise with lower override
        manager.check_payment(400, max_override=500)

        # Should raise when exceeding override
        with pytest.raises(BudgetExceededError):
            manager.check_payment(600, max_override=500)

    def test_record_payment(self):
        """Test recording a payment."""
        manager = BudgetManager()

        record = manager.record_payment(
            url="https://api.example.com/data",
            amount_sats=100,
            invoice="lnbc100n...",
            preimage="abc123",
        )

        assert record.url == "https://api.example.com/data"
        assert record.amount_sats == 100
        assert record.status == "success"
        assert manager.session_spent == 100
        assert len(manager.payments) == 1

    def test_record_multiple_payments(self):
        """Test recording multiple payments accumulates correctly."""
        manager = BudgetManager()

        manager.record_payment(
            url="https://api1.example.com",
            amount_sats=100,
            invoice="lnbc1...",
            preimage="abc1",
        )
        manager.record_payment(
            url="https://api2.example.com",
            amount_sats=200,
            invoice="lnbc2...",
            preimage="abc2",
        )

        assert manager.session_spent == 300
        assert len(manager.payments) == 2

    def test_record_failed_payment_no_accumulate(self):
        """Test failed payments don't accumulate in session total."""
        manager = BudgetManager()

        manager.record_payment(
            url="https://api.example.com",
            amount_sats=100,
            invoice="lnbc...",
            preimage="",
            status="failed",
        )

        assert manager.session_spent == 0
        assert len(manager.payments) == 1

    def test_get_history_limit(self):
        """Test payment history respects limit."""
        manager = BudgetManager()

        for i in range(5):
            manager.record_payment(
                url=f"https://api{i}.example.com",
                amount_sats=10,
                invoice=f"lnbc{i}...",
                preimage=f"pre{i}",
            )

        history = manager.get_history(limit=3)

        assert len(history) == 3

    def test_get_history_since_filter(self):
        """Test payment history filters by timestamp."""
        manager = BudgetManager()

        # Add old payment
        old_record = PaymentRecord(
            url="https://old.example.com",
            amount_sats=10,
            timestamp=datetime(2024, 1, 1, tzinfo=timezone.utc),
            invoice="lnbc...",
            preimage="old",
        )
        manager.payments.append(old_record)

        # Add new payment
        manager.record_payment(
            url="https://new.example.com",
            amount_sats=10,
            invoice="lnbc...",
            preimage="new",
        )

        # Filter since yesterday
        since = datetime.now(timezone.utc) - timedelta(hours=1)
        history = manager.get_history(since=since)

        assert len(history) == 1
        assert history[0].url == "https://new.example.com"

    def test_configure_updates_limits(self):
        """Test configuring new limits."""
        manager = BudgetManager()

        limits = manager.configure(per_request=500, per_session=5000)

        assert limits.per_request == 500
        assert limits.per_session == 5000
        assert manager.max_per_request == 500
        assert manager.max_per_session == 5000

    def test_get_status(self):
        """Test getting budget status."""
        manager = BudgetManager(max_per_request=1000, max_per_session=10000)
        manager.record_payment(
            url="https://api.example.com",
            amount_sats=300,
            invoice="lnbc...",
            preimage="abc",
        )

        status = manager.get_status()

        assert status["limits"]["per_request"] == 1000
        assert status["limits"]["per_session"] == 10000
        assert status["spent"] == 300
        assert status["remaining"] == 9700
        assert status["payment_count"] == 1


class TestPaymentRecord:
    """Tests for PaymentRecord."""

    def test_to_dict(self):
        """Test conversion to dictionary."""
        record = PaymentRecord(
            url="https://api.example.com",
            amount_sats=100,
            timestamp=datetime(2024, 6, 15, 12, 0, 0, tzinfo=timezone.utc),
            invoice="lnbc100n1...",
            preimage="abc123",
            status="success",
        )

        data = record.to_dict()

        assert data["url"] == "https://api.example.com"
        assert data["amount_sats"] == 100
        assert data["status"] == "success"
        assert "timestamp" in data

    def test_to_dict_truncates_long_invoice(self):
        """Test invoice is truncated in dict output."""
        long_invoice = "lnbc100n1p" + "x" * 100
        record = PaymentRecord(
            url="https://api.example.com",
            amount_sats=100,
            timestamp=datetime.now(timezone.utc),
            invoice=long_invoice,
            preimage="abc",
        )

        data = record.to_dict()

        assert len(data["invoice"]) == 23  # 20 chars + "..."
        assert data["invoice"].endswith("...")
