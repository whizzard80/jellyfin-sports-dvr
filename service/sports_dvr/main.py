"""Sports DVR Recording Service - Main entry point."""

import uvicorn
from fastapi import FastAPI
from contextlib import asynccontextmanager

from sports_dvr.api.routes import router
from sports_dvr.recording.manager import RecordingManager
from sports_dvr.config import settings


@asynccontextmanager
async def lifespan(app: FastAPI):
    """Application lifespan manager."""
    # Startup
    app.state.recording_manager = RecordingManager(
        output_path=settings.output_path,
        max_concurrent=settings.max_concurrent_recordings,
    )
    yield
    # Shutdown
    await app.state.recording_manager.stop_all()


app = FastAPI(
    title="Sports DVR Service",
    description="Recording service for Jellyfin Sports DVR plugin",
    version="0.1.0",
    lifespan=lifespan,
)

app.include_router(router, prefix="/api")


@app.get("/health")
async def health_check():
    """Health check endpoint."""
    return {"status": "healthy", "service": "sports-dvr"}


def main():
    """Run the service."""
    uvicorn.run(
        "sports_dvr.main:app",
        host=settings.host,
        port=settings.port,
        reload=settings.debug,
    )


if __name__ == "__main__":
    main()
