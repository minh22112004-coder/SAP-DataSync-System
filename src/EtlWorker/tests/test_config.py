from __future__ import annotations

import os
import unittest
from unittest.mock import patch

from etl_worker.config import Settings


class SettingsTests(unittest.TestCase):
    def _environment(self, **overrides: str) -> dict[str, str]:
        values = {
            "SQL_HOST": "host.docker.internal",
            "SQL_PORT": "1433",
            "SQL_DATABASE": "SapDataSync",
            "SQL_USER": "sa",
            "SQL_PASSWORD": "test-password",
            "SQL_CONNECT_TIMEOUT_SECONDS": "7",
        }
        values.update(overrides)
        return values

    def test_external_sql_server_connection_string(self) -> None:
        with patch.dict(os.environ, self._environment(), clear=True):
            settings = Settings.from_environment()

        self.assertEqual("host.docker.internal", settings.sql_host)
        self.assertEqual(1433, settings.sql_port)
        self.assertEqual(7, settings.sql_connect_timeout_seconds)
        self.assertIn("SERVER={host.docker.internal,1433}", settings.connection_string)
        self.assertIn("DATABASE={SapDataSync}", settings.connection_string)
        self.assertIn("UID={sa}", settings.connection_string)
        self.assertIn("Encrypt=yes", settings.connection_string)
        self.assertIn("TrustServerCertificate=yes", settings.connection_string)
        self.assertIn("Connection Timeout=7", settings.connection_string)

    def test_host_port_and_security_are_configurable(self) -> None:
        environment = self._environment(
            SQL_HOST="sql.customer.local",
            SQL_PORT="15433",
            SQL_ENCRYPT="false",
            SQL_TRUST_SERVER_CERTIFICATE="false",
        )
        with patch.dict(os.environ, environment, clear=True):
            settings = Settings.from_environment()

        self.assertIn("SERVER={sql.customer.local,15433}", settings.connection_string)
        self.assertIn("Encrypt=no", settings.connection_string)
        self.assertIn("TrustServerCertificate=no", settings.connection_string)

    def test_invalid_port_is_rejected(self) -> None:
        with patch.dict(os.environ, self._environment(SQL_PORT="70000"), clear=True):
            with self.assertRaisesRegex(ValueError, "SQL_PORT"):
                Settings.from_environment()

    def test_missing_password_is_rejected(self) -> None:
        environment = self._environment()
        del environment["SQL_PASSWORD"]
        with patch.dict(os.environ, environment, clear=True):
            with self.assertRaisesRegex(ValueError, "SQL_PASSWORD"):
                Settings.from_environment()


if __name__ == "__main__":
    unittest.main()
