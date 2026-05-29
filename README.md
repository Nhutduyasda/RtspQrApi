# RtspQrApi

> ASP.NET Core Web API thực hành đọc RTSP stream từ IP Camera hoặc fake IP Camera, cache latest frame trên backend, phát hiện QR thật bằng OpenCV, lưu lịch sử QR, và trả status, snapshot, QR result cho nhiều users.

## Kiến trúc tổng quan

```text
Fake IP Camera / Real IP Camera
        → RTSP
ASP.NET Core Web API
        → CameraManager
            → CameraWorker (1 worker / 1 camera)
                → FrameStore (latest JPEG frame)
                → QrProcessor (OpenCV QRCodeDetector)
                → SQL Server LocalDB (QR history)
        → API users
```

**Ý tưởng quan trọng:** User không gọi trực tiếp RTSP URL. Backend chỉ mở **1 RTSP connection** cho mỗi camera, sau đó 20–30 users gọi API đọc status, snapshot, hoặc QR result từ cache.

---

## Yêu cầu

| Công cụ | Ghi chú |
|---------|---------|
| .NET SDK 10 | Runtime chính |
| [MediaMTX](https://github.com/bluenviron/mediamtx) | RTSP server giả lập |
| [FFmpeg](https://ffmpeg.org/) | Push stream vào MediaMTX |
| VLC hoặc RTSP viewer | Kiểm tra stream trước khi chạy API |
| SQL Server LocalDB | Lưu lịch sử QR result |

> `ffmpeg`, `mediamtx`, và `vlc` chưa nằm trong PATH trên máy lúc project được tạo. Nếu dùng Windows, có thể cài bằng `winget`:

```powershell
winget install --id Gyan.FFmpeg
winget install --id bluenviron.mediamtx
winget install --id VideoLAN.VLC
```

Nếu không dùng `winget`, MediaMTX có thể tải file release từ GitHub, giải nén, rồi chạy `mediamtx.exe` trong thư mục vừa giải nén.

---

## Hướng dẫn chạy từng bước

### Bước 1 — Chạy MediaMTX

**Cài bằng winget:**

```powershell
$mediamtx = Get-Command mediamtx
$config = Join-Path (Split-Path $mediamtx.Source -Parent) "mediamtx.yml"
mediamtx $config
```

**Tải ZIP thủ công:** Mở terminal tại thư mục vừa giải nén và chạy:

```powershell
.\mediamtx.exe .\mediamtx.yml
```

> Mặc định MediaMTX nhận RTSP tại port `8554`. Nếu log báo `configuration file not found` thì FFmpeg có thể gặp lỗi `Server returned 400 Bad Request` khi push stream.

---

### Bước 2 — Push video test bằng FFmpeg

**Dùng test pattern (fake camera):**

```powershell
ffmpeg -re -f lavfi -i testsrc=size=1280x720:rate=25 `
       -f lavfi -i sine=frequency=1000:sample_rate=44100 `
       -c:v libx264 -preset veryfast -tune zerolatency `
       -g 25 -keyint_min 25 -pix_fmt yuv420p `
       -c:a aac -f rtsp -rtsp_transport tcp `
       rtsp://localhost:8554/cam01
```

**Dùng file video có sẵn:**

```powershell
ffmpeg -re -stream_loop -1 -i .\sample.mp4 -c copy -f rtsp rtsp://localhost:8554/cam01
```

**Kiểm tra bằng VLC:**

```
rtsp://localhost:8554/cam01
```

> Nếu VLC xem được stream thì API mới nên start camera.

---

### Bước 3 — Chạy C# API

```powershell
dotnet restore
dotnet run --launch-profile http
```

| Endpoint | URL |
|----------|-----|
| API base | `http://localhost:5295` |
| OpenAPI JSON | `http://localhost:5295/openapi/v1.json` |

### Đăng nhập dashboard/API

Trong môi trường Development, dashboard và API được bảo vệ bằng Basic Auth:

| Username | Password |
|----------|----------|
| `admin` | `admin123` |

Khi gọi API bằng PowerShell, tạo header một lần rồi thêm `-Headers $headers` vào các lệnh:

```powershell
$pair = "admin:admin123"
$token = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($pair))
$headers = @{ Authorization = "Basic $token" }
```

Có thể đổi credential bằng environment variables:

```powershell
$env:BasicAuth__Username = "your-user"
$env:BasicAuth__Password = "your-password"
dotnet run --launch-profile http
```

---

### Bước 4 — Thêm camera

```powershell
Invoke-RestMethod -Method Post `
  -Uri "http://localhost:5295/api/cameras" `
  -Headers $headers `
  -ContentType "application/json" `
  -Body '{
    "id": "cam01",
    "name": "QR Gate Camera",
    "rtspUrl": "rtsp://localhost:8554/cam01"
  }'
```

---

### Bước 5 — Bắt đầu camera

```powershell
Invoke-RestMethod -Method Post -Uri "http://localhost:5295/api/cameras/cam01/start" -Headers $headers
```

**Log mong đợi:**

```
[INFO]  Camera cam01 connected
[DEBUG] Frame received from cam01: 1280x720, FPS: 25
```

---

### Bước 6 — Lấy trạng thái camera

```powershell
Invoke-RestMethod -Uri "http://localhost:5295/api/cameras/cam01/status" -Headers $headers
```

**Response mẫu:**

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

---

### Bước 7 — Lấy snapshot

```powershell
Invoke-WebRequest -Uri "http://localhost:5295/api/cameras/cam01/snapshot" -Headers $headers -OutFile ".\snapshot.jpg"
```

Mở `snapshot.jpg` để xem frame mới nhất. Endpoint trả về `404` nếu camera chưa có frame nào.

---

### Bước 8 — Lấy QR result mới nhất

```powershell
Invoke-RestMethod -Uri "http://localhost:5295/api/cameras/cam01/latest-qr" -Headers $headers
```

**Response khi chưa thấy QR:**

```json
{
  "cameraId": "cam01",
  "found": false,
  "value": null,
  "detectedAt": null
}
```

Khi camera thấy QR thật, `found` sẽ là `true`, `value` là nội dung QR và `detectedAt` là thời điểm phát hiện.

---

### Bước 9 — Lấy lịch sử QR đã lưu

```powershell
Invoke-RestMethod -Uri "http://localhost:5295/api/cameras/cam01/qr-results?take=50" -Headers $headers
```

### Bước 10 — Xem live MJPEG trên browser

Khi camera đang chạy, dashboard tự dùng endpoint MJPEG:

```text
http://localhost:5295/api/cameras/cam01/mjpeg
```

---

## Danh sách API endpoints

| Method | Path | Mục đích |
|--------|------|----------|
| `GET` | `/api/cameras` | Danh sách camera và status hiện tại |
| `POST` | `/api/cameras` | Thêm camera config |
| `DELETE` | `/api/cameras/{id}` | Dừng worker và xóa camera config, giữ QR history |
| `POST` | `/api/cameras/start-all` | Start tất cả camera đã lưu |
| `POST` | `/api/cameras/stop-all` | Stop tất cả camera đang chạy |
| `POST` | `/api/cameras/{id}/start` | Bắt đầu đọc RTSP |
| `POST` | `/api/cameras/{id}/stop` | Dừng worker |
| `GET` | `/api/cameras/{id}/status` | Lấy trạng thái camera |
| `GET` | `/api/cameras/{id}/snapshot` | Lấy JPEG frame mới nhất |
| `GET` | `/api/cameras/{id}/mjpeg` | Xem live stream MJPEG trong browser |
| `GET` | `/api/cameras/{id}/latest-qr` | Lấy QR result mới nhất |
| `GET` | `/api/cameras/{id}/qr-results?take=50` | Lấy lịch sử QR đã lưu |

---

## Kiểm thử reconnect

1. Đang chạy API và FFmpeg.
2. Dừng FFmpeg (Ctrl+C).
3. Gọi status:

```powershell
Invoke-RestMethod -Uri "http://localhost:5295/api/cameras/cam01/status" -Headers $headers
```

`isConnected` sẽ về `false`, `reconnectCount` tăng, log có dòng reconnect.

4. Chạy lại FFmpeg với cùng URL `rtsp://localhost:8554/cam01`.
5. Gọi status tiếp để thấy camera connected lại.

---

## Load test 20–30 users

**Test status song song:**

```powershell
1..30 | ForEach-Object -Parallel {
  Invoke-RestMethod -Uri "http://localhost:5295/api/cameras/cam01/status" -Headers $using:headers
} -ThrottleLimit 30
```

**Test snapshot song song:**

```powershell
1..30 | ForEach-Object -Parallel {
  Invoke-WebRequest -Uri "http://localhost:5295/api/cameras/cam01/snapshot" -Headers $using:headers -OutFile ".\snapshot-$_.jpg"
} -ThrottleLimit 30
```

**Mục tiêu:** API không crash, các users lấy dữ liệu từ cache, backend vẫn chỉ có **1 `CameraWorker`** và **1 RTSP connection** cho `cam01`.

---

## Cấu trúc project

```text
RtspQrApi/
├── Controllers/
│   └── CamerasController.cs
├── Services/
│   ├── CameraManager.cs
│   ├── CameraWorker.cs
│   ├── FrameStore.cs
│   └── QrProcessor.cs
├── Models/
│   ├── CameraConfig.cs
│   ├── CameraStatus.cs
│   └── QrResult.cs
├── Program.cs
└── appsettings.json
```

---

## Các bước tiếp theo

- [x] Thay `QrProcessor` mock bằng OpenCV `QRCodeDetector`
- [x] Lưu QR result vào SQL Server LocalDB
- [x] Thêm authentication cho API/dashboard bằng Basic Auth
- [x] Thêm nhiều cameras, lưu camera config vào SQL Server, start-all/stop-all/delete
- [x] Thêm MJPEG endpoint và dashboard live view/status real-time
