/**
 * 打开选择器：OpenPanel 的无文件系统替代品（OPEN-01@web）。
 * mountPicker(windowEl, ctx, library) → { show(kind), update(state, regions) }
 * 模态 sheet 覆盖窗口内容区：左侧合成文件夹，右侧所选文件夹的文件列表。
 * 打开期间调用 app.setMenuOpen(true) 抑制照片命令（INPUT-02 模态），关闭后恢复并归还焦点。
 */
import { el, setText, touches } from "./dom.js";

const SIZE_UNITS = ["B", "KB", "MB", "GB"];
const PRIMARY_ZONE = { folder: "folders", file: "files" };

function pad(value) {
    return String(value).padStart(2, "0");
}

function formatSize(bytes) {
    const value = Number(bytes);
    if (!Number.isFinite(value) || value < 0) return "—";
    let size = value;
    let unit = 0;
    while (size >= 1000 && unit < SIZE_UNITS.length - 1) {
        size /= 1000;
        unit += 1;
    }
    return `${unit === 0 ? Math.round(size) : size.toFixed(1)} ${SIZE_UNITS[unit]}`;
}

function formatDate(milliseconds) {
    const value = Number(milliseconds);
    if (!Number.isFinite(value)) return "—";
    const date = new Date(value);
    return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())} ${pad(date.getHours())}:${pad(date.getMinutes())}`;
}

function visiblePhotos(folder) {
    const photos = Array.isArray(folder?.photos) ? folder.photos : [];
    return photos.filter((photo) => photo && typeof photo.name === "string" && !photo.name.startsWith("."));
}

export function mountPicker(windowEl, ctx, library) {
    const app = ctx.app;
    windowEl.tabIndex = -1;

    let kind = "file";
    let folderId = null;
    let fileId = null;
    let zone = "files";
    let isOpen = false;
    let returnFocus = null;
    let folderRows = new Map();
    let fileRows = new Map();

    const title = el("div", { class: "pv-picker-title", id: "pv-picker-title" });
    const sidebar = el("div", {
        class: "pv-picker-sidebar",
        role: "listbox",
        "aria-label": "文件夹",
        tabindex: "0",
    });
    const header = el("div", { class: "pv-picker-header", "aria-hidden": "true" },
        el("span", {}, "名称"),
        el("span", {}, "大小"),
        el("span", {}, "修改日期"));
    const list = el("div", {
        class: "pv-picker-list",
        role: "listbox",
        "aria-label": "文件",
        tabindex: "0",
    });
    const cancelButton = el("button", { class: "pv-picker-button", type: "button" }, "取消");
    const openButton = el("button", { class: "pv-picker-button is-primary", type: "button", disabled: true }, "打开");
    const sheet = el("div", { class: "pv-picker-sheet" },
        title,
        el("div", { class: "pv-picker-body" },
            sidebar,
            el("div", { class: "pv-picker-content" }, header, list)),
        el("div", { class: "pv-picker-footer" }, cancelButton, openButton));
    const backdrop = el("div", { class: "pv-picker-backdrop" });
    const root = el("div", {
        class: "pv-picker",
        role: "dialog",
        "aria-modal": "true",
        "aria-labelledby": "pv-picker-title",
        hidden: true,
    }, backdrop, sheet);
    windowEl.append(root);

    const canOpen = (photo) => app.canOpen(photo);

    function findPhoto(id) {
        for (const folder of library.folders) {
            const found = folder.photos?.find((photo) => photo.id === id);
            if (found) return found;
        }
        return null;
    }

    function folderById(id) {
        return library.folders.find((folder) => folder.id === id) ?? null;
    }

    function syncFolderSelection() {
        for (const [id, row] of folderRows) {
            const selected = id === folderId;
            row.classList.toggle("is-selected", selected);
            row.setAttribute("aria-selected", String(selected));
        }
    }

    function syncFileSelection() {
        for (const [id, row] of fileRows) {
            const selected = id === fileId;
            row.classList.toggle("is-selected", selected);
            row.setAttribute("aria-selected", String(selected));
        }
    }

    function updateOpenButton() {
        const photo = fileId ? findPhoto(fileId) : null;
        const enabled = kind === "folder" ? Boolean(folderId) : Boolean(photo && canOpen(photo));
        openButton.disabled = !enabled;
    }

    function renderSidebar() {
        sidebar.textContent = "";
        folderRows = new Map();
        for (const folder of library.folders) {
            const count = visiblePhotos(folder).length;
            const row = el("div", {
                class: "pv-picker-folder",
                role: "option",
                tabindex: "-1",
                "aria-selected": "false",
                "aria-label": `${folder.name}，${count} 项`,
                dataset: { id: folder.id },
            },
            el("span", { class: "pv-picker-folder-name" }, folder.name),
            el("span", { class: "pv-picker-folder-count" }, String(count)));
            row.addEventListener("click", () => {
                zone = "folders";
                chooseFolder(folder.id);
                sidebar.focus({ preventScroll: true });
            });
            folderRows.set(folder.id, row);
            sidebar.append(row);
        }
        syncFolderSelection();
    }

    function renderFiles() {
        list.textContent = "";
        fileRows = new Map();
        const folder = folderById(folderId);
        for (const photo of visiblePhotos(folder)) {
            const enabled = canOpen(photo);
            const sizeText = formatSize(photo.size);
            const dateText = formatDate(photo.modified);
            const row = el("div", {
                class: enabled ? "pv-picker-row" : "pv-picker-row is-disabled",
                role: "option",
                tabindex: "-1",
                "aria-selected": "false",
                "aria-disabled": String(!enabled),
                "aria-label": `${photo.name}，${sizeText}，${dateText}`,
                dataset: { id: photo.id },
            },
            el("span", { class: "pv-picker-name" }, photo.name),
            el("span", { class: "pv-picker-size" }, sizeText),
            el("span", { class: "pv-picker-date" }, dateText));
            if (enabled) {
                row.addEventListener("click", () => {
                    zone = "files";
                    selectFile(photo.id);
                    list.focus({ preventScroll: true });
                });
                row.addEventListener("dblclick", () => {
                    selectFile(photo.id);
                    openSelected();
                });
            }
            fileRows.set(photo.id, row);
            list.append(row);
        }
        if (fileId) {
            const row = fileRows.get(fileId);
            if (!row || row.classList.contains("is-disabled")) fileId = null;
        }
        syncFileSelection();
        updateOpenButton();
    }

    function chooseFolder(id) {
        if (folderId !== id) fileId = null;
        folderId = id;
        syncFolderSelection();
        renderFiles();
    }

    function selectFile(id) {
        const row = fileRows.get(id);
        if (!row || row.classList.contains("is-disabled")) return;
        fileId = id;
        syncFileSelection();
        updateOpenButton();
        row.scrollIntoView?.({ block: "nearest" });
    }

    function focusZone(next) {
        zone = next === "folders" ? "folders" : "files";
        (zone === "folders" ? sidebar : list).focus({ preventScroll: true });
    }

    function close() {
        if (!isOpen) return;
        isOpen = false;
        root.hidden = true;
        app.setMenuOpen(false);
        const target = returnFocus && returnFocus.isConnected ? returnFocus : windowEl;
        returnFocus = null;
        target.focus?.({ preventScroll: true });
    }

    function openSelected() {
        if (kind === "folder") {
            if (!folderId) return;
            const id = folderId;
            close();
            app.openFolder(id);
            return;
        }
        const photo = fileId ? findPhoto(fileId) : null;
        if (!photo || !canOpen(photo)) return;
        close();
        app.openFile(photo.id);
    }

    function stepFolder(delta) {
        const ids = [...folderRows.keys()];
        if (!ids.length) return;
        const index = ids.indexOf(folderId);
        const next = index < 0
            ? (delta > 0 ? 0 : ids.length - 1)
            : (index + delta + ids.length) % ids.length;
        chooseFolder(ids[next]);
        folderRows.get(ids[next])?.scrollIntoView?.({ block: "nearest" });
    }

    function stepFile(delta) {
        const ids = [...fileRows.entries()]
            .filter(([, row]) => !row.classList.contains("is-disabled"))
            .map(([id]) => id);
        if (!ids.length) return;
        const index = ids.indexOf(fileId);
        const next = index < 0
            ? (delta > 0 ? 0 : ids.length - 1)
            : (index + delta + ids.length) % ids.length;
        selectFile(ids[next]);
    }

    function show(nextKind = "file") {
        kind = nextKind === "folder" ? "folder" : "file";
        if (!isOpen) {
            returnFocus = document.activeElement instanceof HTMLElement ? document.activeElement : null;
        }
        setText(title, kind === "folder" ? "打开文件夹" : "打开文件");
        const current = app.state.folder?.id ?? null;
        const initial = folderById(current) ? current : (library.folders[0]?.id ?? null);
        renderSidebar();
        fileId = null;
        chooseFolder(initial);
        if (kind === "file") {
            const displayed = app.state.displayedId;
            if (displayed && fileRows.has(displayed)) selectFile(displayed);
        }
        isOpen = true;
        root.hidden = false;
        app.setMenuOpen(true);
        focusZone(PRIMARY_ZONE[kind]);
    }

    function update(state, regions) {
        if (!isOpen) return;
        if (!touches(regions, "settings", "list")) return;
        renderSidebar();
        chooseFolder(folderId);
        if (kind === "file" && fileId && fileRows.has(fileId)) selectFile(fileId);
    }

    function focusables() {
        return [sidebar, list, cancelButton, openButton].filter((node) => !node.disabled);
    }

    function keyDown(event) {
        if (!isOpen) return;
        event.stopPropagation();
        const key = event.key;
        if (key === "Tab") {
            event.preventDefault();
            const items = focusables();
            const index = items.indexOf(document.activeElement);
            const step = event.shiftKey ? -1 : 1;
            const next = index < 0 ? 0 : (index + step + items.length) % items.length;
            items[next].focus();
            return;
        }
        if (key === "Escape") {
            event.preventDefault();
            close();
            return;
        }
        const inList = document.activeElement === sidebar || document.activeElement === list;
        if (key === "ArrowDown" || key === "ArrowUp") {
            if (!inList) return;
            event.preventDefault();
            const delta = key === "ArrowDown" ? 1 : -1;
            if (zone === "folders") stepFolder(delta);
            else stepFile(delta);
            return;
        }
        if (key === "ArrowLeft") {
            event.preventDefault();
            focusZone("folders");
            return;
        }
        if (key === "ArrowRight") {
            event.preventDefault();
            focusZone("files");
            return;
        }
        if (key === "Enter") {
            if (inList) {
                event.preventDefault();
                openSelected();
            }
        }
    }

    backdrop.addEventListener("pointerdown", (event) => {
        event.preventDefault();
        close();
    });
    backdrop.addEventListener("contextmenu", (event) => {
        event.preventDefault();
        close();
    });
    cancelButton.addEventListener("click", () => close());
    openButton.addEventListener("click", () => openSelected());
    root.addEventListener("keydown", keyDown);

    return { show, update };
}
