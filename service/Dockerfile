FROM python:3.12-slim

# Install FFmpeg
RUN apt-get update && apt-get install -y --no-install-recommends \
    ffmpeg \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app

# Install Python dependencies
COPY pyproject.toml .
RUN pip install --no-cache-dir .

# Copy application
COPY sports_dvr/ sports_dvr/

# Create recordings directory
RUN mkdir -p /app/recordings

EXPOSE 8765

CMD ["sports-dvr"]
