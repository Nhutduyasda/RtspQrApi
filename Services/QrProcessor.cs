using RtspQrApi.Models;

namespace RtspQrApi.Services;

public sealed class QrProcessor
{
    public QrResult GetLatest(string cameraId)
    {
        return new QrResult(cameraId, Found: false, Value: null, DetectedAt: null);
    }
}
