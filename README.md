# RtspQrApi

ASP.NET Core Web API thuc hanh doc RTSP stream tu IP Camera hoac fake IP Camera, cache latest frame tren backend, va tra status, snapshot, QR result mock cho nhieu users.

## Architecture

```text
Fake IP Camera / Real IP Camera
        -> RTSP
ASP.NET Core Web API
        -> CameraManager
        -> CameraWorker (1 worker / 1 camera)
        -> FrameStore (latest JPEG frame)
        -> QrProcessor (mock)
        -> API users
```

Y tuong quan trong: user khong goi truc tiep RTSP URL. Backend chi mo 1 RTSP connection cho moi camera, sau do 20-30 users goi API doc status, snapshot, hoac QR result tu cache.

## Requirements

- .NET SDK 10
- MediaMTX: https://github.com/bluenviron/mediamtx
- FFmpeg: https://ffmpeg.org/
- VLC hoac tool RTSP viewer de kiem tra stream truoc khi chay API

`ffmpeg`, `mediamtx`, va `vlc` chua nam trong PATH tren may luc project duoc tao. Neu dung Windows, co the cai bang winget:

```powershell
winget install --id Gyan.FFmpeg
winget install --id VideoLAN.VLC
```

MediaMTX co the tai file release tu GitHub, giai nen, roi chay `mediamtx.exe` trong thu muc vua giai nen.

## Step 1: Run MediaMTX

Mo terminal tai thu muc MediaMTX:

```powershell
.\mediamtx.exe
```

Mac dinh MediaMTX nhan RTSP tai port `8554`.

## Step 2: Push test video using FFmpeg

Dung test pattern lam fake camera:

```powershell
ffmpeg -re -f lavfi -i testsrc=size=1280x720:rate=25 -f lavfi -i sine=frequency=1000:sample_rate=44100 -shortest -c:v libx264 -preset veryfast -tune zerolatency -pix_fmt yuv420p -c:a aac -f rtsp rtsp://localhost:8554/cam01
```

Neu co file video:

```powershell
ffmpeg -re -stream_loop -1 -i .\sample.mp4 -c copy -f rtsp rtsp://localhost:8554/cam01
```

Kiem tra bang VLC:

```text
rtsp://localhost:8554/cam01
```

Neu VLC xem duoc stream thi API moi nen start camera.

## Step 3: Run C# API

```powershell
dotnet restore
dotnet run --launch-profile http
```

API chay tai:

```text
http://localhost:5295
```

OpenAPI JSON:

```text
http://localhost:5295/openapi/v1.json
```

## Step 4: Add camera

```powershell
Invoke-RestMethod -Method Post -Uri "http://localhost:5295/api/cameras" -ContentType "application/json" -Body '{
  "id": "cam01",
  "name": "QR Gate Camera",
  "rtspUrl": "rtsp://localhost:8554/cam01"
}'
```

## Step 5: Start camera

```powershell
Invoke-RestMethod -Method Post -Uri "http://localhost:5295/api/cameras/cam01/start"
```

Log mong doi:

```text
[INFO] Camera cam01 connected
[DEBUG] Frame received from cam01: 1280x720, FPS: 25
```

## Step 6: Get status

```powershell
Invoke-RestMethod -Uri "http://localhost:5295/api/cameras/cam01/status"
```

Response mau:

```json
{
  "cameraId": "cam01",
  "isRunning": true,
  "isConnected": true,
  "fps": 25,
  "width": 1280,
  "height": 720,
  "lastFrameAt": "2026-05-27T09:30:00Z",
  "reconnectCount": 0,
  "lastError": null
}
```

## Step 7: Get snapshot

```powershell
Invoke-WebRequest -Uri "http://localhost:5295/api/cameras/cam01/snapshot" -OutFile ".\snapshot.jpg"
```

Mo `snapshot.jpg` de xem frame moi nhat. Endpoint tra ve `404` neu camera chua co frame nao.

## Step 8: Get latest QR result

```powershell
Invoke-RestMethod -Uri "http://localhost:5295/api/cameras/cam01/latest-qr"
```

Response ban dau la mock:

```json
{
  "cameraId": "cam01",
  "found": false,
  "value": null,
  "detectedAt": null
}
```

## API endpoints

| Method | Path | Muc dich |
| --- | --- | --- |
| POST | `/api/cameras` | Them camera config |
| POST | `/api/cameras/{id}/start` | Bat dau doc RTSP |
| POST | `/api/cameras/{id}/stop` | Dung worker |
| GET | `/api/cameras/{id}/status` | Lay trang thai camera |
| GET | `/api/cameras/{id}/snapshot` | Lay JPEG frame moi nhat |
| GET | `/api/cameras/{id}/latest-qr` | Lay QR result mock |

## Reconnect test

1. Dang chay API va FFmpeg.
2. Stop FFmpeg.
3. Goi status:

```powershell
Invoke-RestMethod -Uri "http://localhost:5295/api/cameras/cam01/status"
```

`isConnected` se ve `false`, `reconnectCount` tang, log co dong reconnect.

4. Chay lai FFmpeg voi cung URL `rtsp://localhost:8554/cam01`.
5. Goi status tiep de thay camera connected lai.

## Load test 20-30 users

Dung PowerShell tao 30 request song song toi status:

```powershell
1..30 | ForEach-Object -Parallel {
  Invoke-RestMethod -Uri "http://localhost:5295/api/cameras/cam01/status"
} -ThrottleLimit 30
```

Hoac test snapshot:

```powershell
1..30 | ForEach-Object -Parallel {
  Invoke-WebRequest -Uri "http://localhost:5295/api/cameras/cam01/snapshot" -OutFile ".\snapshot-$_.jpg"
} -ThrottleLimit 30
```

Muc tieu: API khong crash, cac users lay du lieu tu cache, backend van chi co 1 `CameraWorker` va 1 RTSP connection cho `cam01`.

## Project structure

```text
RtspQrApi/
|-- Controllers/
|   |-- CamerasController.cs
|-- Services/
|   |-- CameraManager.cs
|   |-- CameraWorker.cs
|   |-- FrameStore.cs
|   |-- QrProcessor.cs
|-- Models/
|   |-- CameraConfig.cs
|   |-- CameraStatus.cs
|   |-- QrResult.cs
|-- Program.cs
|-- appsettings.json
```

## Next steps

- Thay `QrProcessor` mock bang ZXing.Net hoac OpenCV `QRCodeDetector`.
- Luu QR result vao database.
- Them authentication cho API.
- Them nhieu cameras va endpoint list cameras.
- Them MJPEG endpoint hoac dashboard xem status.
