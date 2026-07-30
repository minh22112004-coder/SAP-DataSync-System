from __future__ import annotations

import logging
import sys

from apscheduler.schedulers.blocking import BlockingScheduler
from apscheduler.triggers.cron import CronTrigger

from .config import Settings
from .control import ControlServer, RunCoordinator
from .service import EtlWorker


def main() -> int:
    logging.basicConfig(
        level=logging.INFO,
        format="%(asctime)s %(levelname)s %(name)s %(message)s",
    )

    try:
        settings = Settings.from_environment()
    except ValueError as exception:
        logging.getLogger(__name__).error("Invalid configuration: %s", exception)
        return 2

    worker = EtlWorker(settings)
    coordinator = RunCoordinator(worker)
    logger = logging.getLogger(__name__)
    logger.info("SAP DataSync Python ETL Worker started")
    logger.info("Source directory: %s", settings.source_path)
    logger.info("Upload directory: %s", settings.upload_path or "disabled")
    logger.info("Snapshot archive directory: %s", settings.archive_path)
    logger.info("File pattern: %s", settings.file_pattern)
    logger.info("Worksheet: %s", settings.worksheet_name or "first worksheet")
    logger.info("Soft delete: %s", "enabled" if settings.soft_delete_enabled else "disabled")
    logger.info(
        "The source directory is read-only; each new file hash is snapshotted before import"
    )

    if settings.run_once:
        return 1 if coordinator.run_blocking("run-once") else 0

    control_server = ControlServer(coordinator, "0.0.0.0", settings.control_port)
    control_server.start()

    if settings.run_on_startup:
        coordinator.run_blocking("startup")

    trigger = CronTrigger(
        hour=settings.daily_time.hour,
        minute=settings.daily_time.minute,
        timezone=settings.timezone,
    )
    scheduler = BlockingScheduler(timezone=settings.timezone)
    scheduler.add_job(
        coordinator.run_scheduled,
        trigger=trigger,
        id="sap-datasync-daily-etl",
        replace_existing=True,
        max_instances=1,
        coalesce=True,
        misfire_grace_time=3_600,
    )
    logger.info(
        "Daily schedule: %02d:%02d (%s)",
        settings.daily_time.hour,
        settings.daily_time.minute,
        settings.timezone.key,
    )

    try:
        scheduler.start()
    except (KeyboardInterrupt, SystemExit):
        logger.info("SAP DataSync Python ETL Worker stopped")
    finally:
        control_server.stop()
    return 0


if __name__ == "__main__":
    sys.exit(main())
