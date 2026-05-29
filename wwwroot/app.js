const state = {
    cameras: [],
    busyIds: new Set(),
    loading: false,
};

const elements = {
    form: document.querySelector("#cameraForm"),
    autoStart: document.querySelector("#autoStart"),
    grid: document.querySelector("#cameraGrid"),
    empty: document.querySelector("#emptyState"),
    refresh: document.querySelector("#refreshButton"),
    apiHealth: document.querySelector("#apiHealth"),
    total: document.querySelector("#totalCameras"),
    running: document.querySelector("#runningCameras"),
    connected: document.querySelector("#connectedCameras"),
    qrFound: document.querySelector("#qrFound"),
    lastUpdated: document.querySelector("#lastUpdated"),
    toast: document.querySelector("#toast"),
};

let toastTimer;

elements.form.addEventListener("submit", handleAddCamera);
elements.refresh.addEventListener("click", () => loadCameras({ force: true }));
elements.grid.addEventListener("click", handleCameraAction);

loadCameras({ force: true });
window.setInterval(() => loadCameras(), 2500);

async function handleAddCamera(event) {
    event.preventDefault();

    const formData = new FormData(elements.form);
    const payload = {
        id: String(formData.get("id") ?? "").trim(),
        name: String(formData.get("name") ?? "").trim(),
        rtspUrl: String(formData.get("rtspUrl") ?? "").trim(),
    };

    if (!payload.id || !payload.name || !payload.rtspUrl) {
        showToast("Nhập đủ thông tin camera.");
        return;
    }

    setFormBusy(true);
    try {
        await sendJson("/api/cameras", {
            method: "POST",
            body: JSON.stringify(payload),
        });

        if (elements.autoStart.checked) {
            await sendJson(`/api/cameras/${encodeURIComponent(payload.id)}/start`, {
                method: "POST",
            });
        }

        elements.form.reset();
        elements.autoStart.checked = true;
        showToast(`Đã thêm camera ${payload.id}.`);
        await loadCameras({ force: true });
    } catch (error) {
        showToast(error.message);
    } finally {
        setFormBusy(false);
    }
}

async function handleCameraAction(event) {
    const button = event.target.closest("button[data-action][data-camera-id]");
    if (!button) {
        return;
    }

    const action = button.dataset.action;
    const cameraId = button.dataset.cameraId;

    if (!action || !cameraId || state.busyIds.has(cameraId)) {
        return;
    }

    state.busyIds.add(cameraId);
    render();

    try {
        await sendJson(`/api/cameras/${encodeURIComponent(cameraId)}/${action}`, {
            method: "POST",
        });
        showToast(action === "start" ? `Đang start ${cameraId}.` : `Đã stop ${cameraId}.`);
        await loadCameras({ force: true });
    } catch (error) {
        showToast(error.message);
    } finally {
        state.busyIds.delete(cameraId);
        render();
    }
}

async function loadCameras(options = {}) {
    if (state.loading && !options.force) {
        return;
    }

    state.loading = true;
    elements.refresh.disabled = true;

    try {
        const cameras = await fetchJson("/api/cameras");
        state.cameras = Array.isArray(cameras) ? cameras : [];
        elements.apiHealth.textContent = "API đang kết nối";
        elements.apiHealth.classList.remove("is-offline");
        elements.lastUpdated.textContent = `Cập nhật ${new Date().toLocaleTimeString("vi-VN")}`;
        render();
    } catch (error) {
        elements.apiHealth.textContent = "API mất kết nối";
        elements.apiHealth.classList.add("is-offline");
        showToast(error.message);
    } finally {
        state.loading = false;
        elements.refresh.disabled = false;
    }
}

function render() {
    const total = state.cameras.length;
    const running = state.cameras.filter(camera => camera.status?.isRunning).length;
    const connected = state.cameras.filter(camera => camera.status?.isConnected).length;
    const qrFound = state.cameras.filter(camera => camera.latestQr?.found).length;

    elements.total.textContent = total;
    elements.running.textContent = running;
    elements.connected.textContent = connected;
    elements.qrFound.textContent = qrFound;

    elements.empty.hidden = total > 0;
    elements.grid.innerHTML = state.cameras.map(renderCameraCard).join("");
    wireSnapshotFallbacks();
}

function renderCameraCard(camera) {
    const status = camera.status ?? {};
    const latestQr = camera.latestQr ?? {};
    const id = String(camera.id ?? status.cameraId ?? "");
    const safeId = escapeHtml(id);
    const encodedId = encodeURIComponent(id);
    const isBusy = state.busyIds.has(id);
    const isRunning = Boolean(status.isRunning);
    const isConnected = Boolean(status.isConnected);
    const hasFrame = Boolean(status.lastFrameAt);
    const snapshotUrl = `/api/cameras/${encodedId}/snapshot?t=${Date.now()}`;

    const signalClass = isConnected ? "is-ok" : isRunning ? "is-warning" : "is-danger";
    const signalText = isConnected ? "Có tín hiệu" : isRunning ? "Đang đợi frame" : "Đã dừng";
    const runClass = isRunning ? "is-ok" : "is-danger";
    const runText = isRunning ? "Đang chạy" : "Chưa chạy";
    const qrClass = latestQr.found ? "is-ok" : "is-warning";
    const qrText = latestQr.found ? "QR found" : "QR none";

    return `
        <article class="camera-card">
            <div class="camera-card-header">
                <div class="camera-title">
                    <h3 title="${escapeHtml(camera.name ?? id)}">${escapeHtml(camera.name ?? id)}</h3>
                    <span title="${escapeHtml(camera.rtspUrl ?? "")}">${escapeHtml(camera.rtspUrl ?? "")}</span>
                </div>
                <span class="id-chip">${safeId}</span>
            </div>

            <div class="snapshot">
                <img src="${snapshotUrl}" alt="Snapshot ${safeId}" data-snapshot="${safeId}" ${hasFrame ? "" : "class=\"is-hidden\""}>
                <div class="snapshot-empty" ${hasFrame ? "hidden" : ""}>
                    <strong>Chưa có snapshot</strong>
                    <span>${isRunning ? "Đang chờ frame đầu tiên từ RTSP stream." : "Start camera để lấy frame mới nhất."}</span>
                </div>
            </div>

            <div class="camera-body">
                <div class="status-row">
                    <span class="status-chip ${runClass}">${runText}</span>
                    <span class="status-chip ${signalClass}">${signalText}</span>
                    <span class="status-chip ${qrClass}">${qrText}</span>
                </div>

                <dl class="metrics">
                    <div>
                        <dt>FPS</dt>
                        <dd>${formatNumber(status.fps)}</dd>
                    </div>
                    <div>
                        <dt>Độ phân giải</dt>
                        <dd>${formatResolution(status.width, status.height)}</dd>
                    </div>
                    <div>
                        <dt>Frame cuối</dt>
                        <dd>${formatDateTime(status.lastFrameAt)}</dd>
                    </div>
                    <div>
                        <dt>Reconnect</dt>
                        <dd>${status.reconnectCount ?? 0}</dd>
                    </div>
                    <div>
                        <dt>QR value</dt>
                        <dd>${escapeHtml(latestQr.value ?? "-")}</dd>
                    </div>
                    <div>
                        <dt>QR time</dt>
                        <dd>${formatDateTime(latestQr.detectedAt)}</dd>
                    </div>
                </dl>

                ${status.lastError ? `<p class="last-error">${escapeHtml(status.lastError)}</p>` : ""}

                <div class="actions-row">
                    <button class="camera-action" type="button" data-action="start" data-camera-id="${safeId}" ${isBusy || isRunning ? "disabled" : ""}>Start</button>
                    <button class="camera-action is-danger" type="button" data-action="stop" data-camera-id="${safeId}" ${isBusy || !isRunning ? "disabled" : ""}>Stop</button>
                </div>
            </div>
        </article>
    `;
}

function wireSnapshotFallbacks() {
    document.querySelectorAll("[data-snapshot]").forEach(image => {
        image.addEventListener("error", () => {
            image.classList.add("is-hidden");
            const fallback = image.nextElementSibling;
            if (fallback) {
                fallback.hidden = false;
            }
        }, { once: true });

        image.addEventListener("load", () => {
            image.classList.remove("is-hidden");
            const fallback = image.nextElementSibling;
            if (fallback) {
                fallback.hidden = true;
            }
        }, { once: true });
    });
}

async function fetchJson(url) {
    const response = await fetch(url, {
        headers: {
            "Accept": "application/json",
        },
    });

    if (!response.ok) {
        throw new Error(await readError(response));
    }

    return response.json();
}

async function sendJson(url, options) {
    const response = await fetch(url, {
        ...options,
        headers: {
            "Accept": "application/json",
            "Content-Type": "application/json",
            ...(options.headers ?? {}),
        },
    });

    if (!response.ok) {
        throw new Error(await readError(response));
    }

    const text = await response.text();
    return text ? JSON.parse(text) : null;
}

async function readError(response) {
    const contentType = response.headers.get("content-type") ?? "";

    if (contentType.includes("application/json")) {
        const body = await response.json();
        if (body.message) {
            return body.message;
        }

        if (body.title) {
            return body.title;
        }

        if (body.errors) {
            return Object.values(body.errors).flat().join(" ");
        }
    }

    return `Request lỗi ${response.status}.`;
}

function setFormBusy(isBusy) {
    elements.form.querySelectorAll("input, button").forEach(control => {
        control.disabled = isBusy;
    });
}

function showToast(message) {
    window.clearTimeout(toastTimer);
    elements.toast.textContent = message;
    elements.toast.classList.add("is-visible");
    toastTimer = window.setTimeout(() => {
        elements.toast.classList.remove("is-visible");
    }, 3200);
}

function formatDateTime(value) {
    if (!value) {
        return "-";
    }

    const date = new Date(value);
    if (Number.isNaN(date.getTime())) {
        return "-";
    }

    return date.toLocaleString("vi-VN");
}

function formatNumber(value) {
    const number = Number(value);
    if (!Number.isFinite(number) || number <= 0) {
        return "-";
    }

    return number.toLocaleString("vi-VN", {
        maximumFractionDigits: 2,
    });
}

function formatResolution(width, height) {
    if (!width || !height) {
        return "-";
    }

    return `${width} x ${height}`;
}

function escapeHtml(value) {
    return String(value)
        .replaceAll("&", "&amp;")
        .replaceAll("<", "&lt;")
        .replaceAll(">", "&gt;")
        .replaceAll('"', "&quot;")
        .replaceAll("'", "&#039;");
}
