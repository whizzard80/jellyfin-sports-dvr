"""API routes for Sports DVR service."""

from fastapi import APIRouter, HTTPException, Request
from pydantic import BaseModel

router = APIRouter()


class RecordingRequest(BaseModel):
    """Request model for starting a recording."""

    stream_url: str
    event_id: str
    event_name: str
    duration_minutes: int = 180  # Default 3 hours


class RecordingStatus(BaseModel):
    """Recording status response."""

    event_id: str
    event_name: str
    status: str
    progress_percent: float
    timeshift_url: str | None = None


@router.get("/recordings")
async def get_recordings(request: Request) -> list[RecordingStatus]:
    """Get all current and recent recordings."""
    manager = request.app.state.recording_manager
    return manager.get_all_status()


@router.post("/record/{event_id}")
async def start_recording(event_id: str, recording: RecordingRequest, request: Request):
    """Start recording a stream."""
    manager = request.app.state.recording_manager

    if manager.active_count >= manager.max_concurrent:
        raise HTTPException(
            status_code=429,
            detail=f"Maximum concurrent recordings ({manager.max_concurrent}) reached",
        )

    result = await manager.start_recording(
        event_id=event_id,
        stream_url=recording.stream_url,
        event_name=recording.event_name,
        duration_minutes=recording.duration_minutes,
    )

    return {"status": "started", "event_id": event_id, "recording": result}


@router.delete("/record/{event_id}")
async def stop_recording(event_id: str, request: Request):
    """Stop a recording."""
    manager = request.app.state.recording_manager
    await manager.stop_recording(event_id)
    return {"status": "stopped", "event_id": event_id}


@router.get("/timeshift/{event_id}")
async def get_timeshift_url(event_id: str, request: Request):
    """Get time-shift HLS URL for a live recording."""
    manager = request.app.state.recording_manager
    recording = manager.get_recording(event_id)

    if not recording:
        raise HTTPException(status_code=404, detail="Recording not found")

    if not recording.get("timeshift_url"):
        raise HTTPException(status_code=404, detail="Time-shift not available")

    return {
        "event_id": event_id,
        "timeshift_url": recording["timeshift_url"],
        "status": recording["status"],
    }
