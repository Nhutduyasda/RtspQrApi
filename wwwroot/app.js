const state = {
    cameras: [],
    qrHistories: new Map(),
    busyIds: new Set(),
    loading: false,
    bulkBusy: false,
};

const elements = {
    form: document.querySelector("#cameraForm"),
    autoStart: document.querySelector("#autoStart"),
    grid: document.querySelector("#cameraGrid"),
    empty: document.querySelector("#emptyState"),
    refresh: document.querySelector("#refreshButton"),
    startAll: document.querySelector("#startAllButton"),
    stopAll: document.querySelector("#stopAllButton"),
    apiHealth: document.querySelector("#apiHealth"),
    total: document.querySelector("#totalCameras"),
    running: document.querySelector("#runningCameras"),
    connected: document.querySelector("#connectedCameras"),
    qrFound: document.querySelector("#qrFound"),
    lastUpdated: document.querySelector("#lastUpdated"),
    toast: document.querySelector("#toast"),
};

sanitizeCredentialUrl();

let toastTimer;

elements.form.addEventListener("submit", handleAddCamera);
elements.refresh.addEventListener("click", () => loadCameras({ force: true }));
elements.startAll.addEventListener("click", () => handleBulkAction("start-all"));
elements.stopAll.addEventListener("click", () => handleBulkAction("stop-all"));
elements.grid.addEventListener("click", handleCameraAction);

loadCameras({ force: true });
window.setInterval(() => loadCameras(), 2500);

function sanitizeCredentialUrl() {
    if (!window.location.username && !window.location.password) {
        return;
    }

    const cleanUrl = `${window.location.protocol}//${window.location.host}${window.location.pathname}${window.location.search}${window.location.hash}`;
    window.history.replaceState(null, "", cleanUrl);
}

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
        if (action === "delete") {
            const shouldDelete = window.confirm(`Xóa camera ${cameraId}? Lịch sử QR đã lưu vẫn được giữ trong database.`);
            if (!shouldDelete) {
                return;
            }

            await sendJson(`/api/cameras/${encodeURIComponent(cameraId)}`, {
                method: "DELETE",
            });
            state.qrHistories.delete(cameraId);
            showToast(`Đã xóa camera ${cameraId}.`);
        } else {
            await sendJson(`/api/cameras/${encodeURIComponent(cameraId)}/${action}`, {
                method: "POST",
            });
            showToast(action === "start" ? `Đang start ${cameraId}.` : `Đã stop ${cameraId}.`);
        }

        await loadCameras({ force: true });
    } catch (error) {
        showToast(error.message);
    } finally {
        state.busyIds.delete(cameraId);
        render();
    }
}

async function handleBulkAction(action) {
    if (state.bulkBusy) {
        return;
    }

    state.bulkBusy = true;
    render();

    try {
        await sendJson(`/api/cameras/${action}`, {
            method: "POST",
        });
        showToast(action === "start-all" ? "Đang start tất cả camera." : "Đã stop tất cả camera.");
        await loadCameras({ force: true });
    } catch (error) {
        showToast(error.message);
    } finally {
        state.bulkBusy = false;
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
        await refreshQrHistories(Boolean(options.force));
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

async function refreshQrHistories(force) {
    const targets = state.cameras.filter(camera => {
        return force || camera.status?.isRunning || camera.latestQr?.found;
    });

    await Promise.all(targets.map(async camera => {
        const id = String(camera.id ?? camera.status?.cameraId ?? "");
        if (!id) {
            return;
        }

        try {
            const history = await fetchJson(`/api/cameras/${encodeURIComponent(id)}/qr-results?take=200&t=${Date.now()}`);
            state.qrHistories.set(id, Array.isArray(history) ? history : []);
        } catch {
            state.qrHistories.set(id, []);
        }
    }));
}

function render() {
    const total = state.cameras.length;
    const running = state.cameras.filter(camera => camera.status?.isRunning).length;
    const connected = state.cameras.filter(camera => camera.status?.isConnected).length;
    const qrFound = state.cameras.reduce((sum, camera) => {
        const id = String(camera.id ?? camera.status?.cameraId ?? "");
        const history = state.qrHistories.get(id);

        if (history) {
            return sum + history.length;
        }

        return sum + (camera.latestQr?.found ? 1 : 0);
    }, 0);

    elements.total.textContent = total;
    elements.running.textContent = running;
    elements.connected.textContent = connected;
    elements.qrFound.textContent = qrFound;
    elements.startAll.disabled = state.bulkBusy || total === 0 || running === total;
    elements.stopAll.disabled = state.bulkBusy || total === 0 || running === 0;

    elements.empty.hidden = total > 0;
    renderCameraGrid();
}

function renderCameraGrid() {
    const renderedCards = state.cameras.map(camera => {
        const nextCard = createCardElement(camera);
        const id = String(camera.id ?? camera.status?.cameraId ?? "");
        const existingCard = elements.grid.querySelector(`[data-camera-card="${id}"]`);

        if (existingCard?.dataset.isRunning === "true" && nextCard.dataset.isRunning === "true") {
            existingCard.querySelector(".camera-card-header")?.replaceWith(nextCard.querySelector(".camera-card-header"));
            existingCard.querySelector(".camera-body")?.replaceWith(nextCard.querySelector(".camera-body"));
            existingCard.dataset.isRunning = "true";
            return existingCard;
        }

        return nextCard;
    });

    elements.grid.replaceChildren(...renderedCards);
    wireSnapshotFallbacks();
}

function createCardElement(camera) {
    const template = document.createElement("template");
    template.innerHTML = renderCameraCard(camera).trim();
    return template.content.firstElementChild;
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
    const history = state.qrHistories.get(id) ?? [];
    const currentQrValue = latestQr.found ? latestQr.value : null;
    const currentQrTime = latestQr.found ? latestQr.detectedAt : null;
    const mediaUrl = isRunning
        ? apiUrl(`/api/cameras/${encodedId}/mjpeg`)
        : apiUrl(`/api/cameras/${encodedId}/snapshot?t=${Date.now()}`);
    const mediaLabel = isRunning ? "Live view" : "Snapshot";

    const signalClass = isConnected ? "is-ok" : isRunning ? "is-warning" : "is-danger";
    const signalText = isConnected ? "Có tín hiệu" : isRunning ? "Đang đợi frame" : "Đã dừng";
    const runClass = isRunning ? "is-ok" : "is-danger";
    const runText = isRunning ? "Đang chạy" : "Chưa chạy";
    const qrClass = currentQrValue ? "is-ok" : "is-warning";
    const qrText = currentQrValue ? "QR found" : "QR none";

    return `
        <article class="camera-card" data-camera-card="${safeId}" data-is-running="${isRunning ? "true" : "false"}">
            <div class="camera-card-header">
                <div class="camera-title">
                    <h3 title="${escapeHtml(camera.name ?? id)}">${escapeHtml(camera.name ?? id)}</h3>
                    <span title="${escapeHtml(camera.rtspUrl ?? "")}">${escapeHtml(camera.rtspUrl ?? "")}</span>
                </div>
                <span class="id-chip">${safeId}</span>
            </div>

            <div class="snapshot">
                <img src="${mediaUrl}" alt="${mediaLabel} ${safeId}" data-snapshot="${safeId}" ${hasFrame ? "" : "class=\"is-hidden\""}>
                <div class="snapshot-empty" ${hasFrame ? "hidden" : ""}>
                    <strong>Chưa có snapshot</strong>
                    <span>${isRunning ? "Đang chờ frame đầu tiên từ RTSP stream." : "Start camera để xem live view."}</span>
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
                    <div class="metric-wide">
                        <dt>QR hiện tại</dt>
                        <dd>${escapeHtml(currentQrValue ?? "-")}</dd>
                    </div>
                    <div>
                        <dt>QR time</dt>
                        <dd>${formatDateTime(currentQrTime)}</dd>
                    </div>
                </dl>

                ${renderQrHistory(history)}

                ${status.lastError ? `<p class="last-error">${escapeHtml(status.lastError)}</p>` : ""}

                <div class="actions-row">
                    <button class="camera-action" type="button" data-action="start" data-camera-id="${safeId}" ${isBusy || isRunning ? "disabled" : ""}>Start</button>
                    <button class="camera-action is-danger" type="button" data-action="stop" data-camera-id="${safeId}" ${isBusy || !isRunning ? "disabled" : ""}>Stop</button>
                    <button class="camera-action is-danger" type="button" data-action="delete" data-camera-id="${safeId}" ${isBusy ? "disabled" : ""}>Xóa</button>
                </div>
            </div>
        </article>
    `;
}

function renderQrHistory(history) {
    const items = history.slice(0, 5);
    const countText = items.length > 0 ? `${items.length} gần đây` : "Chưa có dữ liệu";

    return `
        <section class="qr-history" aria-label="Lịch sử QR">
            <div class="qr-history-title">
                <strong>Lịch sử QR</strong>
                <span>${countText}</span>
            </div>
            ${
                items.length > 0
                    ? `<ol>${items.map(renderQrHistoryItem).join("")}</ol>`
                    : `<p>Chưa có QR được lưu.</p>`
            }
        </section>
    `;
}

function renderQrHistoryItem(item) {
    return `
        <li>
            <span>${escapeHtml(item.value ?? "-")}</span>
            <time>${formatDateTime(item.detectedAt)}</time>
        </li>
    `;
}

function wireSnapshotFallbacks() {
    document.querySelectorAll("[data-snapshot]").forEach(image => {
        if (image.dataset.fallbackWired === "true") {
            return;
        }

        image.dataset.fallbackWired = "true";

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
    const response = await fetch(apiUrl(url), {
        cache: "no-store",
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
    const response = await fetch(apiUrl(url), {
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

function apiUrl(path) {
    if (/^https?:\/\//i.test(path)) {
        return path;
    }

    const prefix = path.startsWith("/") ? "" : "/";
    return `${window.location.protocol}//${window.location.host}${prefix}${path}`;
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
