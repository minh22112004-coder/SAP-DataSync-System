from __future__ import annotations

import json
import logging
import threading
from datetime import datetime, timezone
from http import HTTPStatus
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from typing import Any

from .service import EtlWorker

LOGGER = logging.getLogger(__name__)


class RunCoordinator:
    def __init__(self, worker: EtlWorker) -> None:
        self._worker = worker
        self._run_lock = threading.Lock()
        self._state_lock = threading.Lock()
        self._state: dict[str, Any] = {
            "running": False,
            "trigger": None,
            "startedAt": None,
            "completedAt": None,
            "exitCode": None,
            "message": "Chưa có lần chạy thủ công trong phiên ETL hiện tại.",
        }

    def status(self) -> dict[str, Any]:
        with self._state_lock:
            return dict(self._state)

    def start_manual(self) -> tuple[bool, dict[str, Any]]:
        if not self._run_lock.acquire(blocking=False):
            with self._state_lock:
                state = dict(self._state)
            return False, state

        self._mark_started("manual")
        thread = threading.Thread(
            target=self._execute_acquired,
            name="manual-etl-run",
            daemon=True,
        )
        thread.start()
        return True, self.status()

    def run_scheduled(self) -> int:
        if not self._run_lock.acquire(blocking=False):
            LOGGER.warning("ETL run skipped because another run is already active")
            return 0
        self._mark_started("schedule")
        return self._execute_acquired()

    def run_blocking(self, trigger: str) -> int:
        self._run_lock.acquire()
        self._mark_started(trigger)
        return self._execute_acquired()

    def _mark_started(self, trigger: str) -> None:
        with self._state_lock:
            self._state = {
                "running": True,
                "trigger": trigger,
                "startedAt": _utc_now(),
                "completedAt": None,
                "exitCode": None,
                "message": "Đang kiểm tra và import file nguồn.",
            }

    def _execute_acquired(self) -> int:
        exit_code = 1
        try:
            exit_code = self._worker.run_cycle()
            message = (
                "Đã hoàn tất kiểm tra file nguồn; file đã import sẽ được bỏ qua."
                if exit_code == 0
                else "Import hoàn tất nhưng có file xử lý thất bại."
            )
            return exit_code
        except Exception:
            LOGGER.exception("Unhandled error during coordinated ETL run")
            message = "Import thất bại do lỗi không mong đợi."
            return 1
        finally:
            with self._state_lock:
                self._state.update(
                    running=False,
                    completedAt=_utc_now(),
                    exitCode=exit_code,
                    message=message,
                )
            self._run_lock.release()


class ControlServer:
    def __init__(self, coordinator: RunCoordinator, host: str, port: int) -> None:
        handler = _create_handler(coordinator)
        self._server = ThreadingHTTPServer((host, port), handler)
        self._thread = threading.Thread(
            target=self._server.serve_forever,
            name="etl-control-server",
            daemon=True,
        )

    def start(self) -> None:
        self._thread.start()
        host, port = self._server.server_address
        LOGGER.info("ETL control endpoint listening on %s:%s", host, port)

    def stop(self) -> None:
        self._server.shutdown()
        self._server.server_close()
        self._thread.join(timeout=5)


def _create_handler(coordinator: RunCoordinator) -> type[BaseHTTPRequestHandler]:
    class Handler(BaseHTTPRequestHandler):
        def do_GET(self) -> None:  # noqa: N802
            if self.path == "/health":
                self._write_json(HTTPStatus.OK, {"status": "Healthy"})
                return
            if self.path == "/status":
                self._write_json(HTTPStatus.OK, coordinator.status())
                return
            self._write_json(HTTPStatus.NOT_FOUND, {"message": "Endpoint not found."})

        def do_POST(self) -> None:  # noqa: N802
            if self.path != "/run":
                self._write_json(HTTPStatus.NOT_FOUND, {"message": "Endpoint not found."})
                return
            accepted, state = coordinator.start_manual()
            status = HTTPStatus.ACCEPTED if accepted else HTTPStatus.CONFLICT
            self._write_json(status, state)

        def _write_json(self, status: HTTPStatus, body: dict[str, Any]) -> None:
            payload = json.dumps(body, ensure_ascii=False).encode("utf-8")
            self.send_response(status.value)
            self.send_header("Content-Type", "application/json; charset=utf-8")
            self.send_header("Content-Length", str(len(payload)))
            self.send_header("Cache-Control", "no-store")
            self.end_headers()
            self.wfile.write(payload)

        def log_message(self, format: str, *args: object) -> None:
            LOGGER.debug("ETL control: " + format, *args)

    return Handler


def _utc_now() -> str:
    return datetime.now(timezone.utc).isoformat()
