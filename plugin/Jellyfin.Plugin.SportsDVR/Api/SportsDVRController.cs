using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SportsDVR.Api;

/// <summary>
/// API controller for Sports DVR operations.
/// </summary>
[ApiController]
[Authorize]
[Route("SportsDVR")]
public class SportsDVRController : ControllerBase
{
    private readonly ILogger<SportsDVRController> _logger;
    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="SportsDVRController"/> class.
    /// </summary>
    /// <param name="logger">Instance of the <see cref="ILogger{SportsDVRController}"/> interface.</param>
    /// <param name="httpClientFactory">Instance of the <see cref="IHttpClientFactory"/> interface.</param>
    public SportsDVRController(ILogger<SportsDVRController> logger, IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>
    /// Gets the list of current and upcoming recordings.
    /// </summary>
    /// <returns>List of recordings.</returns>
    [HttpGet("Recordings")]
    public async Task<ActionResult> GetRecordings()
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null)
        {
            return StatusCode(500, "Plugin not configured");
        }

        var client = _httpClientFactory.CreateClient();
        var response = await client.GetAsync($"{config.RecordingServiceUrl}/api/recordings");
        
        if (!response.IsSuccessStatusCode)
        {
            return StatusCode((int)response.StatusCode, "Failed to get recordings from service");
        }

        var content = await response.Content.ReadAsStringAsync();
        return Content(content, "application/json");
    }

    /// <summary>
    /// Starts recording a specific event.
    /// </summary>
    /// <param name="eventId">The event ID to record.</param>
    /// <returns>Recording status.</returns>
    [HttpPost("Record/{eventId}")]
    public async Task<ActionResult> StartRecording(string eventId)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null)
        {
            return StatusCode(500, "Plugin not configured");
        }

        var client = _httpClientFactory.CreateClient();
        var response = await client.PostAsync($"{config.RecordingServiceUrl}/api/record/{eventId}", null);
        
        if (!response.IsSuccessStatusCode)
        {
            return StatusCode((int)response.StatusCode, "Failed to start recording");
        }

        _logger.LogInformation("Started recording for event: {EventId}", eventId);
        return Ok(new { Status = "Recording started", EventId = eventId });
    }

    /// <summary>
    /// Gets time-shift stream URL for a live recording.
    /// </summary>
    /// <param name="eventId">The event ID.</param>
    /// <returns>HLS stream URL for time-shifted playback.</returns>
    [HttpGet("TimeShift/{eventId}")]
    public async Task<ActionResult> GetTimeShiftUrl(string eventId)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null || !config.EnableTimeShift)
        {
            return StatusCode(500, "Time-shift not enabled");
        }

        var client = _httpClientFactory.CreateClient();
        var response = await client.GetAsync($"{config.RecordingServiceUrl}/api/timeshift/{eventId}");
        
        if (!response.IsSuccessStatusCode)
        {
            return StatusCode((int)response.StatusCode, "Time-shift not available");
        }

        var content = await response.Content.ReadAsStringAsync();
        return Content(content, "application/json");
    }
}
