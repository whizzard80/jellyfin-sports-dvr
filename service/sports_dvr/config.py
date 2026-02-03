"""Configuration settings for Sports DVR service."""

from pydantic_settings import BaseSettings


class Settings(BaseSettings):
    """Application settings."""

    # Service settings
    host: str = "0.0.0.0"
    port: int = 8765
    debug: bool = False

    # Recording settings
    output_path: str = "/media/sports"
    max_concurrent_recordings: int = 2
    segment_duration: int = 6  # HLS segment duration in seconds

    # Integration settings
    teamarr_url: str = ""
    dispatcharr_url: str = ""

    # FFmpeg settings
    ffmpeg_path: str = "ffmpeg"
    ffmpeg_options: str = "-c copy"

    class Config:
        env_prefix = "SPORTS_DVR_"
        env_file = ".env"


settings = Settings()
