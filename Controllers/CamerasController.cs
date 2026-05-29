using Microsoft.AspNetCore.Mvc;
using RtspQrApi.Models;
using RtspQrApi.Services;

namespace RtspQrApi.Controllers;

[ApiController]
[Route("api/cameras")]
public sealed class CamerasController : ControllerBase
{
    private const string MjpegBoundary = "frame";
    private static readonly TimeSpan MjpegWaitTimeout = TimeSpan.FromSeconds(1);

    private readonly CameraManager _cameraManager;
    private readonly QrProcessor _qrProcessor;

    public CamerasController(CameraManager cameraManager, QrProcessor qrProcessor)
    {
        _cameraManager = cameraManager;
        _qrProcessor = qrProcessor;
    }

    [HttpGet]
    public IActionResult GetCameras()
    {
        var cameras = _cameraManager.GetCameras()
            .Select(config => new CameraSummary(
                config.Id,
                config.Name,
                config.RtspUrl,
                _cameraManager.GetStatus(config.Id)!,
                _qrProcessor.GetLatest(config.Id)));

        return Ok(cameras);
    }

    [HttpPost]
    public async Task<IActionResult> AddCamera(CameraConfig config, CancellationToken cancellationToken)
    {
        if (!await _cameraManager.AddCameraAsync(config, cancellationToken).ConfigureAwait(false))
        {
            return Conflict(new { message = $"Camera '{config.Id}' already exists." });
        }

        return CreatedAtAction(nameof(GetStatus), new { id = config.Id.Trim() }, config);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, CancellationToken cancellationToken)
    {
        if (!_cameraManager.CameraExists(id))
        {
            return NotFound();
        }

        var deleted = await _cameraManager.DeleteCameraAsync(id, cancellationToken).ConfigureAwait(false);
        return deleted
            ? NoContent()
            : Problem($"Camera '{id}' could not be stopped before delete.", statusCode: StatusCodes.Status409Conflict);
    }

    [HttpPost("{id}/start")]
    public async Task<IActionResult> Start(string id)
    {
        if (!_cameraManager.CameraExists(id))
        {
            return NotFound();
        }

        var started = await _cameraManager.StartAsync(id).ConfigureAwait(false);
        return started
            ? Ok(new { cameraId = id, isRunning = true })
            : Problem($"Camera '{id}' could not be started.", statusCode: StatusCodes.Status409Conflict);
    }

    [HttpPost("start-all")]
    public async Task<IActionResult> StartAll()
    {
        var statuses = await _cameraManager.StartAllAsync().ConfigureAwait(false);
        return Ok(statuses);
    }

    [HttpPost("{id}/stop")]
    public async Task<IActionResult> Stop(string id)
    {
        if (!_cameraManager.CameraExists(id))
        {
            return NotFound();
        }

        var stopped = await _cameraManager.StopAsync(id).ConfigureAwait(false);
        return stopped
            ? Ok(_cameraManager.GetStatus(id))
            : Problem($"Camera '{id}' did not stop within the timeout.", statusCode: StatusCodes.Status409Conflict);
    }

    [HttpPost("stop-all")]
    public async Task<IActionResult> StopAll()
    {
        var statuses = await _cameraManager.StopAllAsync().ConfigureAwait(false);
        return Ok(statuses);
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

    [HttpGet("{id}/mjpeg")]
    public async Task GetMjpeg(string id, CancellationToken cancellationToken)
    {
        if (!_cameraManager.CameraExists(id))
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        Response.StatusCode = StatusCodes.Status200OK;
        Response.ContentType = $"multipart/x-mixed-replace; boundary={MjpegBoundary}";
        Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        Response.Headers.Pragma = "no-cache";
        Response.Headers.Expires = "0";

        var lastVersion = 0L;

        try
        {
            while (!cancellationToken.IsCancellationRequested && _cameraManager.CameraExists(id))
            {
                FrameSnapshot? snapshot;
                using (var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    waitCts.CancelAfter(MjpegWaitTimeout);

                    try
                    {
                        snapshot = await _cameraManager.WaitForSnapshotAsync(id, lastVersion, waitCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        if (_cameraManager.GetStatus(id)?.IsRunning != true)
                        {
                            break;
                        }

                        continue;
                    }
                }

                if (snapshot is null)
                {
                    break;
                }

                lastVersion = snapshot.Version;
                await WriteMjpegFrameAsync(snapshot, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The browser closed the MJPEG connection.
        }
    }

    [HttpGet("{id}/latest-qr")]
    public IActionResult GetLatestQr(string id)
    {
        return _cameraManager.CameraExists(id) ? Ok(_qrProcessor.GetLatest(id)) : NotFound();
    }

    [HttpGet("{id}/qr-results")]
    public async Task<IActionResult> GetQrResults(string id, [FromQuery] int take = 50, CancellationToken cancellationToken = default)
    {
        if (!_cameraManager.CameraExists(id))
        {
            return NotFound();
        }

        var results = await _qrProcessor.GetHistoryAsync(id, take, cancellationToken).ConfigureAwait(false);
        return Ok(results);
    }

    private async Task WriteMjpegFrameAsync(FrameSnapshot snapshot, CancellationToken cancellationToken)
    {
        await Response.WriteAsync($"--{MjpegBoundary}\r\n", cancellationToken).ConfigureAwait(false);
        await Response.WriteAsync("Content-Type: image/jpeg\r\n", cancellationToken).ConfigureAwait(false);
        await Response.WriteAsync($"Content-Length: {snapshot.Jpeg.Length}\r\n\r\n", cancellationToken).ConfigureAwait(false);
        await Response.Body.WriteAsync(snapshot.Jpeg, cancellationToken).ConfigureAwait(false);
        await Response.WriteAsync("\r\n", cancellationToken).ConfigureAwait(false);
        await Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
