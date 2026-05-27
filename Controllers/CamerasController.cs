using Microsoft.AspNetCore.Mvc;
using RtspQrApi.Models;
using RtspQrApi.Services;

namespace RtspQrApi.Controllers;

[ApiController]
[Route("api/cameras")]
public sealed class CamerasController : ControllerBase
{
    private readonly CameraManager _cameraManager;
    private readonly QrProcessor _qrProcessor;

    public CamerasController(CameraManager cameraManager, QrProcessor qrProcessor)
    {
        _cameraManager = cameraManager;
        _qrProcessor = qrProcessor;
    }

    [HttpPost]
    public IActionResult AddCamera(CameraConfig config)
    {
        if (!_cameraManager.AddCamera(config))
        {
            return Conflict(new { message = $"Camera '{config.Id}' already exists." });
        }

        return CreatedAtAction(nameof(GetStatus), new { id = config.Id }, config);
    }

    [HttpPost("{id}/start")]
    public async Task<IActionResult> Start(string id)
    {
        var started = await _cameraManager.StartAsync(id).ConfigureAwait(false);
        return started ? Ok(new { cameraId = id, isRunning = true }) : NotFound();
    }

    [HttpPost("{id}/stop")]
    public async Task<IActionResult> Stop(string id)
    {
        var stopped = await _cameraManager.StopAsync(id).ConfigureAwait(false);
        return stopped ? Ok(_cameraManager.GetStatus(id)) : NotFound();
    }

    [HttpGet("{id}/status")]
    public IActionResult GetStatus(string id)
    {
        var status = _cameraManager.GetStatus(id);
        return status is null ? NotFound() : Ok(status);
    }

    [HttpGet("{id}/snapshot")]
    public IActionResult GetSnapshot(string id)
    {
        if (!_cameraManager.CameraExists(id))
        {
            return NotFound();
        }

        if (!_cameraManager.TryGetSnapshot(id, out var jpeg, out var lastFrameAt))
        {
            return NotFound(new { message = $"Camera '{id}' does not have a snapshot yet." });
        }

        if (lastFrameAt.HasValue)
        {
            Response.Headers.LastModified = lastFrameAt.Value.ToString("R");
        }

        return File(jpeg, "image/jpeg");
    }

    [HttpGet("{id}/latest-qr")]
    public IActionResult GetLatestQr(string id)
    {
        return _cameraManager.CameraExists(id) ? Ok(_qrProcessor.GetLatest(id)) : NotFound();
    }
}
