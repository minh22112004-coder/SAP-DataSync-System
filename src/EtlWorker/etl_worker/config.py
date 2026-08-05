from __future__ import annotations

import os
from dataclasses import dataclass
from datetime import time
from zoneinfo import ZoneInfo, ZoneInfoNotFoundError


@dataclass(frozen=True)
class Settings:
    sql_host: str
    sql_port: int
    sql_database: str
    sql_user: str
    sql_password: str
    sql_driver: str
    sql_encrypt: bool
    sql_trust_server_certificate: bool
    sql_connect_timeout_seconds: int
    source_path: str
    upload_path: str | None
    archive_path: str
    file_pattern: str
    worksheet_name: str | None
    product: str
    sales_organization: str
    batch_size: int
    minimum_file_age_seconds: int
    soft_delete_enabled: bool
    daily_time: time
    timezone: ZoneInfo
    run_once: bool
    run_on_startup: bool
    control_port: int

    @classmethod
    def from_environment(cls) -> "Settings":
        daily_time = _read_time("ETL_DAILY_TIME", "01:00")
        timezone_name = os.getenv("ETL_TIMEZONE", "Asia/Ho_Chi_Minh")
        try:
            timezone = ZoneInfo(timezone_name)
        except ZoneInfoNotFoundError as exception:
            raise ValueError(f"ETL_TIMEZONE is invalid: {timezone_name}") from exception

        return cls(
            sql_host=_required("SQL_HOST"),
            sql_port=_read_int("SQL_PORT", 1433, 1, 65_535),
            sql_database=os.getenv("SQL_DATABASE", "SapDataSync"),
            sql_user=os.getenv("SQL_USER", "sa"),
            sql_password=_required("SQL_PASSWORD"),
            sql_driver=os.getenv("SQL_DRIVER", "ODBC Driver 18 for SQL Server"),
            sql_encrypt=_read_bool("SQL_ENCRYPT", True),
            sql_trust_server_certificate=_read_bool("SQL_TRUST_SERVER_CERTIFICATE", True),
            sql_connect_timeout_seconds=_read_int(
                "SQL_CONNECT_TIMEOUT_SECONDS", 5, 1, 60
            ),
            source_path=os.getenv("SAP_SOURCE_PATH", "/data/source"),
            upload_path=_optional("SAP_UPLOAD_PATH"),
            archive_path=os.getenv("SAP_ARCHIVE_PATH", "/data/archive"),
            file_pattern=os.getenv("SAP_FILE_PATTERN", "export*.xlsx"),
            worksheet_name=_optional("SAP_WORKSHEET_NAME"),
            product=os.getenv("SAP_PRODUCT", "12").strip() or "12",
            sales_organization=os.getenv("SAP_SALES_ORGANIZATION", "SG50").strip() or "SG50",
            batch_size=_read_int("ETL_BATCH_SIZE", 500, 1, 10_000),
            minimum_file_age_seconds=_read_int("ETL_MIN_FILE_AGE_SECONDS", 10, 0, 3_600),
            soft_delete_enabled=_read_bool("ETL_ENABLE_SOFT_DELETE", False),
            daily_time=daily_time,
            timezone=timezone,
            run_once=_read_bool("ETL_RUN_ONCE", False),
            run_on_startup=_read_bool("ETL_RUN_ON_STARTUP", False),
            control_port=_read_int("ETL_CONTROL_PORT", 8090, 1, 65_535),
        )

    @property
    def connection_string(self) -> str:
        return ";".join(
            (
                f"DRIVER={_odbc_escape(self.sql_driver)}",
                f"SERVER={_odbc_escape(f'{self.sql_host},{self.sql_port}')}",
                f"DATABASE={_odbc_escape(self.sql_database)}",
                f"UID={_odbc_escape(self.sql_user)}",
                f"PWD={_odbc_escape(self.sql_password)}",
                f"Encrypt={'yes' if self.sql_encrypt else 'no'}",
                "TrustServerCertificate="
                f"{'yes' if self.sql_trust_server_certificate else 'no'}",
                f"Connection Timeout={self.sql_connect_timeout_seconds}",
            )
        )


def _required(name: str) -> str:
    value = os.getenv(name)
    if value is None or not value.strip():
        raise ValueError(f"{name} is required")
    return value


def _optional(name: str) -> str | None:
    value = os.getenv(name)
    return value if value and value.strip() else None


def _read_bool(name: str, default: bool) -> bool:
    raw = os.getenv(name)
    if raw is None or not raw.strip():
        return default
    normalized = raw.strip().lower()
    if normalized in {"1", "true", "yes", "on"}:
        return True
    if normalized in {"0", "false", "no", "off"}:
        return False
    raise ValueError(f"{name} must be true or false")


def _read_int(name: str, default: int, minimum: int, maximum: int) -> int:
    raw = os.getenv(name)
    if raw is None or not raw.strip():
        return default
    try:
        value = int(raw)
    except ValueError as exception:
        raise ValueError(f"{name} must be an integer") from exception
    if not minimum <= value <= maximum:
        raise ValueError(f"{name} must be from {minimum} to {maximum}")
    return value


def _read_time(name: str, default: str) -> time:
    raw = os.getenv(name, default).strip()
    parts = raw.split(":")
    if len(parts) != 2:
        raise ValueError(f"{name} must use HH:MM format")
    try:
        hour, minute = (int(part) for part in parts)
        return time(hour=hour, minute=minute)
    except ValueError as exception:
        raise ValueError(f"{name} must be a valid time in HH:MM format") from exception


def _odbc_escape(value: str) -> str:
    return "{" + value.replace("}", "}}") + "}"
