"""Recording manager for handling FFmpeg processes."""

import asyncio
import logging
import os
from datetime import datetime
from pathlib import Path

logger = logging.getLogger(__name__)


class RecordingManager:
    """Manages recording processes."""

    def __init__(self, output_path: str, max_concurrent: int = 2):
        """Initialize the recording manager."""
        self.output_path = Path(output_path)
        self.max_concurrent = max_concurrent
        self._recordings: dict[str, dict] = {}
        self._processes: dict[str, asyncio.subprocess.Process] = {}

        # Ensure output directory exists
        self.output_path.mkdir(parents=True, exist_ok=True)

    @property
    def active_count(self) -> int:
        """Get count of active recordings."""
        return len([r for r in self._recordings.values() if r["status"] == "recording"])

    def get_all_status(self) -> list[dict]:
        """Get status of all recordings."""
        return list(self._recordings.values())

    def get_recording(self, event_id: str) -> dict | None:
        """Get a specific recording."""
        return self._recordings.get(event_id)

    async def start_recording(
        self,
        event_id: str,
        stream_url: str,
        event_name: str,
        duration_minutes: int = 180,
    ) -> dict:
        """Start a new recording."""
        if event_id in self._recordings:
            logger.warning("Recording already exists for event: %s", event_id)
            return self._recordings[event_id]

        # Create output directory for this recording
        safe_name = "".join(c if c.isalnum() or c in "- _" else "_" for c in event_name)
        timestamp = datetime.now().strftime("%Y%m%d_%H%M%S")
        recording_dir = self.output_path / f"{safe_name}_{timestamp}"
        recording_dir.mkdir(parents=True, exist_ok=True)

        # Output files
        hls_path = recording_dir / "stream.m3u8"
        mp4_path = recording_dir / f"{safe_name}.mp4"

        # FFmpeg command for HLS with time-shift support
        # This creates HLS segments while also recording to MP4
        cmd = [
            "ffmpeg",
            "-i", stream_url,
            "-c", "copy",
            "-f", "hls",
            "-hls_time", "6",
            "-hls_list_size", "0",  # Keep all segments for full DVR
            "-hls_flags", "append_list+delete_segments+omit_endlist",
            str(hls_path),
            "-c", "copy",
            "-t", str(duration_minutes * 60),
            str(mp4_path),
        ]

        recording_info = {
            "event_id": event_id,
            "event_name": event_name,
            "status": "starting",
            "progress_percent": 0.0,
            "started_at": datetime.now().isoformat(),
            "duration_minutes": duration_minutes,
            "output_dir": str(recording_dir),
            "hls_path": str(hls_path),
            "mp4_path": str(mp4_path),
            "timeshift_url": f"/recordings/{recording_dir.name}/stream.m3u8",
        }
        self._recordings[event_id] = recording_info

        # Start FFmpeg process
        try:
            process = await asyncio.create_subprocess_exec(
                *cmd,
                stdout=asyncio.subprocess.PIPE,
                stderr=asyncio.subprocess.PIPE,
            )
            self._processes[event_id] = process
            recording_info["status"] = "recording"
            logger.info("Started recording: %s", event_name)

            # Monitor process in background
            asyncio.create_task(self._monitor_recording(event_id, process, duration_minutes))

        except Exception as e:
            logger.error("Failed to start recording: %s", e)
            recording_info["status"] = "failed"
            recording_info["error"] = str(e)

        return recording_info

    async def _monitor_recording(
        self,
        event_id: str,
        process: asyncio.subprocess.Process,
        duration_minutes: int,
    ):
        """Monitor a recording process."""
        start_time = datetime.now()
        total_seconds = duration_minutes * 60

        while process.returncode is None:
            await asyncio.sleep(10)
            
            if event_id not in self._recordings:
                break

            elapsed = (datetime.now() - start_time).total_seconds()
            progress = min(100.0, (elapsed / total_seconds) * 100)
            self._recordings[event_id]["progress_percent"] = progress

        # Recording finished
        if event_id in self._recordings:
            self._recordings[event_id]["status"] = "completed"
            self._recordings[event_id]["progress_percent"] = 100.0
            self._recordings[event_id]["completed_at"] = datetime.now().isoformat()
            logger.info("Recording completed: %s", event_id)

        if event_id in self._processes:
            del self._processes[event_id]

    async def stop_recording(self, event_id: str):
        """Stop a recording."""
        if event_id in self._processes:
            process = self._processes[event_id]
            process.terminate()
            await process.wait()
            del self._processes[event_id]

        if event_id in self._recordings:
            self._recordings[event_id]["status"] = "stopped"
            logger.info("Stopped recording: %s", event_id)

    async def stop_all(self):
        """Stop all recordings."""
        for event_id in list(self._processes.keys()):
            await self.stop_recording(event_id)
