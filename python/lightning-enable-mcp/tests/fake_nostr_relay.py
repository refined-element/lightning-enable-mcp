"""An in-process Nostr relay for attestation tests. Binds 127.0.0.1 only; no real network.

Modes:
  accept       OK true for every EVENT; stores it; serves REQ by kinds / #p / #u / #v.
  reject       OK false "blocked: ..." for every EVENT.
  auth         sends ["AUTH", challenge] on connect, then OK false "auth-required: ..."
               for EVENT and CLOSED "auth-required: ..." for REQ.
  silent       never answers (to exercise timeouts).
"""

from __future__ import annotations

import json
from typing import Any

import websockets


class FakeRelay:
    def __init__(self, mode: str = "accept") -> None:
        self.mode = mode
        self.stored: list[dict[str, Any]] = []
        self.received: list[list[Any]] = []
        self.extra_events: list[dict[str, Any]] = []  # served on REQ even if never published
        self.raw_frames: list[str] = []  # sent verbatim before the events of every REQ
        self._server: Any = None
        self.url = ""

    async def __aenter__(self) -> FakeRelay:
        self._server = await websockets.serve(self._handle, "127.0.0.1", 0)
        port = next(iter(self._server.sockets)).getsockname()[1]
        self.url = f"ws://127.0.0.1:{port}"
        return self

    async def __aexit__(self, *exc: Any) -> None:
        self._server.close()
        await self._server.wait_closed()

    async def _handle(self, ws: Any, *_: Any) -> None:
        if self.mode == "auth":
            await ws.send(json.dumps(["AUTH", "challenge-123"]))
        try:
            async for raw in ws:
                frame = json.loads(raw)
                self.received.append(frame)
                if self.mode == "silent":
                    continue
                if frame[0] == "EVENT":
                    await self._on_event(ws, frame[1])
                elif frame[0] == "REQ":
                    await self._on_req(ws, frame[1], frame[2] if len(frame) > 2 else {})
        except websockets.ConnectionClosed:
            pass

    async def _on_event(self, ws: Any, event: dict[str, Any]) -> None:
        if self.mode == "reject":
            await ws.send(json.dumps(["OK", event["id"], False, "blocked: not on the allowlist"]))
        elif self.mode == "auth":
            await ws.send(json.dumps(["OK", event["id"], False, "auth-required: sign in first"]))
        else:
            self.stored.append(event)
            await ws.send(json.dumps(["OK", event["id"], True, ""]))

    async def _on_req(self, ws: Any, sub: str, flt: dict[str, Any]) -> None:
        if self.mode == "auth":
            await ws.send(json.dumps(["CLOSED", sub, "auth-required: sign in first"]))
            return
        for raw in self.raw_frames:
            await ws.send(raw)
        for ev in [*self.extra_events, *self.stored]:
            if _matches(ev, flt):
                await ws.send(json.dumps(["EVENT", sub, ev], ensure_ascii=False))
        await ws.send(json.dumps(["EOSE", sub]))


def _matches(ev: dict[str, Any], flt: dict[str, Any]) -> bool:
    if "kinds" in flt and ev.get("kind") not in flt["kinds"]:
        return False
    for key, wanted in flt.items():
        if key.startswith("#") and len(key) == 2:
            values = {t[1] for t in ev.get("tags", []) if len(t) >= 2 and t[0] == key[1]}
            if not values & set(wanted):
                return False
    return True
