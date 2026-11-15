/* ==========================================================================
   Glovebox client: shell navigation, sheets, quick actions, markdown,
   the attachment viewer and small conveniences. Loaded on every page.
   ========================================================================== */

/* ---------- colour mode ---------- */
function gbApplyColorMode() {
    var root = document.documentElement;
    var mode = root.getAttribute('data-gb-color-mode');
    if (mode === 'system') {
        root.setAttribute('data-bs-theme', window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light');
    }
}
(function () {
    try {
        window.matchMedia('(prefers-color-scheme: dark)').addEventListener('change', gbApplyColorMode);
    } catch (e) { }
})();

/* ---------- count labels ("12 records") ---------- */
function gbSetCountLabel(label, count) {
    var $label = $(label);
    if ($label.length == 0 || $label.attr('data-many') === undefined) {
        return false;
    }
    var noun = count == 1 ? $label.attr('data-one') : $label.attr('data-many');
    $label.text(`${Number(count).toLocaleString()} ${noun}`);
    return true;
}

/* ---------- charts ---------- */
function gbCssVar(name) {
    try {
        return getComputedStyle(document.documentElement).getPropertyValue(name).trim();
    } catch (e) {
        return '';
    }
}
function gbIsDark() {
    return document.documentElement.getAttribute('data-bs-theme') === 'dark';
}
function gbChartInk() {
    return gbCssVar('--gb-text-2') || (gbIsDark() ? '#fff' : '#000');
}
function gbChartPaper() {
    return gbCssVar('--gb-surface') || (gbIsDark() ? '#000' : '#fff');
}
function gbChartBar() {
    return gbIsDark() ? 'rgba(236, 233, 226, .46)' : 'rgba(24, 24, 22, .72)';
}
function gbChartDefaults() {
    if (!window.Chart || !Chart.defaults) {
        return;
    }
    try {
        Chart.defaults.font.family = getComputedStyle(document.body).fontFamily;
        Chart.defaults.font.size = 12;
        Chart.defaults.color = gbChartInk();
        Chart.defaults.borderColor = gbCssVar('--gb-line') || 'rgba(128,128,128,.2)';
        Chart.defaults.plugins.legend.labels.usePointStyle = true;
        Chart.defaults.plugins.legend.labels.boxWidth = 8;
        Chart.defaults.plugins.tooltip.backgroundColor = gbCssVar('--gb-ink') || '#111';
        Chart.defaults.plugins.tooltip.titleColor = gbCssVar('--gb-on-ink') || '#fff';
        Chart.defaults.plugins.tooltip.bodyColor = gbCssVar('--gb-on-ink') || '#fff';
        Chart.defaults.plugins.tooltip.cornerRadius = 10;
        Chart.defaults.plugins.tooltip.padding = 10;
    } catch (e) { }
}
$(gbChartDefaults);

/* ---------- markdown (replaces drawdown; sanitised) ---------- */
function gbRenderMarkdown(text) {
    if (text == undefined || text == null) {
        return '';
    }
    var html = '';
    try {
        if (window.marked) {
            html = window.marked.parse(String(text), { gfm: true, breaks: true });
        } else {
            html = $('<div>').text(text).html().replace(/\n/g, '<br>');
        }
    } catch (e) {
        html = $('<div>').text(text).html().replace(/\n/g, '<br>');
    }
    if (window.DOMPurify) {
        html = window.DOMPurify.sanitize(html, { ADD_ATTR: ['target'] });
    }
    return html;
}
//kept for existing callers (stickers, kiosk, markdown overlays)
function markdown(text) {
    return gbRenderMarkdown(text);
}
function gbHydrateMarkdown(scope) {
    $(scope || document).find('[data-gb-markdown-source]').each(function () {
        var source = $(this);
        var target = $(source.attr('data-gb-markdown-source'));
        if (target.length) {
            target.html(gbRenderMarkdown(source.val ? source.val() : source.text()));
            target.find('a[href]').attr('target', '_blank').attr('rel', 'noopener');
        }
    });
}

/* ---------- tab proxies (bottom nav, sheets, quick links) ---------- */
function gbShowTab(tabId) {
    var elem = document.getElementById(tabId);
    if (!elem) {
        return false;
    }
    if (elem.classList.contains('d-none')) {
        return false;
    }
    bootstrap.Tab.getOrCreateInstance(elem).show();
    gbCloseSheets();
    window.scrollTo({ top: 0, behavior: 'auto' });
    return true;
}
function gbSyncTabProxies(activeId) {
    $('[data-gb-tab]').each(function () {
        $(this).toggleClass('active', $(this).attr('data-gb-tab') === activeId);
    });
    //"More" lights up when the active section only lives in the more sheet
    var moreButton = $('[data-gb-more]');
    if (moreButton.length) {
        var visibleInBar = $('.gb-bottomnav [data-gb-tab="' + activeId + '"]').length > 0;
        moreButton.toggleClass('active', !visibleInBar && activeId != undefined);
    }
}
function gbBindTabProxies() {
    $(document).on('click', '[data-gb-tab]', function (e) {
        e.preventDefault();
        gbShowTab($(this).attr('data-gb-tab'));
    });
    $(document).on('shown.bs.tab', 'button[data-bs-toggle="tab"]', function (e) {
        gbSyncTabProxies(e.target.id);
        var label = $(e.target).attr('data-gb-title') || $(e.target).text().trim();
        var head = $('#gbSectionTitle');
        if (head.length) {
            head.text(label);
            $('#gbSectionHead').toggleClass('d-none', e.target.id === 'report-tab');
        }
    });
}

/* ---------- sheets ---------- */
function gbOpenSheet(id) {
    gbCloseSheets();
    var sheet = $('#' + id);
    if (!sheet.length) {
        return;
    }
    sheet.addClass('open').attr('aria-hidden', 'false');
    $('.gb-sheet-backdrop').addClass('open');
    setTimeout(function () { sheet.find('button, a').first().trigger('focus'); }, 50);
}
function gbCloseSheets() {
    $('.gb-sheet.open').removeClass('open').attr('aria-hidden', 'true');
    $('.gb-sheet-backdrop').removeClass('open');
}
$(document).on('click', '.gb-sheet-backdrop', gbCloseSheets);
$(document).on('keydown', function (e) {
    if (e.key === 'Escape' && $('.gb-sheet.open').length) {
        gbCloseSheets();
    }
});

/* ---------- vehicle page helpers ---------- */
var gbAddFunctions = {
    'servicerecord': function () { return typeof showAddServiceRecordModal === 'function' && showAddServiceRecordModal(); },
    'accident': function () { return typeof showAddCollisionRecordModal === 'function' && showAddCollisionRecordModal(); },
    'upgrade': function () { return typeof showAddUpgradeRecordModal === 'function' && showAddUpgradeRecordModal(); },
    'gas': function () { return typeof showAddGasRecordModal === 'function' && showAddGasRecordModal(); },
    'odometer': function () { return typeof showAddOdometerRecordModal === 'function' && showAddOdometerRecordModal(); },
    'notes': function () { return typeof showAddNoteModal === 'function' && showAddNoteModal(); },
    'tax': function () { return typeof showAddTaxRecordModal === 'function' && showAddTaxRecordModal(); },
    'plan': function () { return typeof showAddPlanRecordModal === 'function' && showAddPlanRecordModal(); },
    'supply': function () { return typeof showAddSupplyRecordModal === 'function' && showAddSupplyRecordModal(); },
    'equipment': function () { return typeof showAddEquipmentRecordModal === 'function' && showAddEquipmentRecordModal(); },
    'inspection': function () { return typeof useInspectionRecordTemplate === 'function' && useInspectionRecordTemplate(); }
};
var gbModalContentIds = {
    'servicerecord': '#serviceRecordModalContent',
    'accident': '#collisionRecordModalContent',
    'upgrade': '#upgradeRecordModalContent',
    'gas': '#gasRecordModalContent',
    'odometer': '#odometerRecordModalContent',
    'notes': '#noteModalContent',
    'tax': '#taxRecordModalContent',
    'plan': '#planRecordModalContent',
    'supply': '#supplyRecordModalContent',
    'equipment': '#equipmentRecordModalContent',
    'inspection': '#inspectionRecordModalContent'
};
function gbCurrentTab() {
    var active = $('#vehicleTab button.active[data-bs-toggle="tab"]');
    return active.length ? active.attr('id').replace('-tab', '') : '';
}
//Open the "add" form of any section, switching to that section first.
function gbQuickAdd(tab) {
    gbCloseSheets();
    if (tab === 'reminder') {
        showAddReminderModal();
        return;
    }
    if (gbCurrentTab() === tab && gbAddFunctions[tab]) {
        gbAddFunctions[tab]();
        return;
    }
    setBrowserHistory('add', '1');
    if (!gbShowTab(tab + '-tab')) {
        setBrowserHistory('add', '');
        errorToast('That section is turned off in Settings');
    }
}
//Open an existing record in its section.
function gbOpenRecord(tab, recordId) {
    gbCloseSheets();
    if (gbCurrentTab() === tab) {
        var editFunctions = {
            'servicerecord': 'showEditServiceRecordModal', 'accident': 'showEditCollisionRecordModal', 'upgrade': 'showEditUpgradeRecordModal',
            'gas': 'showEditGasRecordModal', 'odometer': 'showEditOdometerRecordModal', 'notes': 'showNoteViewer', 'tax': 'showEditTaxRecordModal',
            'plan': 'showEditPlanRecordModal', 'supply': 'showEditSupplyRecordModal', 'equipment': 'showEditEquipmentRecordModal',
            'inspection': 'showEditInspectionRecordModal', 'reminder': 'showEditReminderRecordModal'
        };
        var fn = window[editFunctions[tab]];
        if (typeof fn === 'function') {
            fn(recordId);
        }
        return;
    }
    setBrowserHistory('id', recordId);
    if (!gbShowTab(tab + '-tab')) {
        setBrowserHistory('id', '');
    }
}
//Log a service record that completes (and rolls forward) a recurring reminder.
function gbLogReminder(reminderId, description, isRecurring) {
    gbCloseSheets();
    var target = 'servicerecord';
    if ($('#servicerecord-tab').length == 0 || $('#servicerecord-tab').hasClass('d-none')) {
        if (isRecurring) {
            gbMarkReminderDone(reminderId);
        } else {
            showEditReminderRecordModal(reminderId);
        }
        return;
    }
    var apply = function () {
        $('#serviceRecordDescription').val(description).trigger('change');
        if (isRecurring) {
            recurringReminderRecordId = [reminderId];
            if ($('.gb-linked-reminder').length == 0) {
                $('#serviceRecordDescription').after(`<div class="gb-callout mt-2 gb-linked-reminder"><i class="bi bi-arrow-repeat"></i><span>Saving this record rolls the reminder forward to its next interval.</span></div>`);
            }
        }
    };
    var openWhenReady = function () {
        waitForElement('#serviceRecordDescription', apply, '');
    };
    //drop any previously loaded form so the prefill lands on the fresh one
    $('#serviceRecordModalContent').html('');
    if (gbCurrentTab() === target) {
        showAddServiceRecordModal();
        openWhenReady();
        return;
    }
    setBrowserHistory('add', '1');
    gbShowTab(target + '-tab');
    openWhenReady();
}
function gbMarkReminderDone(reminderId) {
    $.post(`/Vehicle/PushbackRecurringReminderRecord?reminderRecordId=${reminderId}`, function (data) {
        if (data.success) {
            successToast("Reminder rolled forward");
            gbRefreshActiveTab();
        } else {
            errorToast(data.message);
        }
    });
}
function gbRefreshActiveTab() {
    if (typeof GetVehicleId !== 'function') {
        return;
    }
    var vehicleId = GetVehicleId().vehicleId;
    switch (gbCurrentTab()) {
        case 'report': getVehicleReport(vehicleId); break;
        case 'reminder': getVehicleReminders(vehicleId); break;
        default: getVehicleHaveImportantReminders(vehicleId); break;
    }
}
//When a record is changed from the overview, refresh the overview instead of the (hidden) section list.
function gbRefreshAfterChange() {
    if (typeof GetVehicleId === 'function' && gbCurrentTab() === 'report') {
        getVehicleReport(GetVehicleId().vehicleId);
        return true;
    }
    return false;
}
function gbTogglePin(noteId) {
    $.post('/Vehicle/PinNotes', { noteIds: [noteId], isToggle: true, pinStatus: false }, function (data) {
        if (data.success) {
            $('#noteViewerModal').modal('hide');
            successToast('Note updated');
            if (!gbRefreshAfterChange()) {
                getVehicleNotes(GetVehicleId().vehicleId);
            }
        } else {
            errorToast(data.message);
        }
    });
}
function gbLoadVehicleSwitcher(vehicleId) {
    var menu = $('#gbSwitcherMenu');
    if (menu.attr('data-loaded') === 'true') {
        return;
    }
    $.get(`/Vehicle/GetVehicleSwitcher?vehicleId=${vehicleId}`, function (data) {
        menu.html(data).attr('data-loaded', 'true');
    });
}

/* ---------- notes ---------- */
function showNoteViewer(noteId) {
    $.get(`/Vehicle/GetNoteForViewById?noteId=${noteId}`, function (data) {
        if (data) {
            $('#noteViewerModalContent').html(data);
            gbHydrateMarkdown('#noteViewerModalContent');
            gbHydrateFileThumbs('#noteViewerModalContent');
            $('#noteViewerModal').modal('show');
        }
    }).fail(function () {
        errorToast(genericErrorMessage());
    });
}
function gbEditNoteFromViewer(noteId) {
    $('#noteViewerModal').one('hidden.bs.modal', function () {
        showEditNoteModal(noteId, true);
    }).modal('hide');
}

/* ---------- attachments ---------- */
var gbViewer = { items: [], index: 0, pdf: null, zoom: 1, renderToken: 0 };
function gbFileKind(location) {
    if (location == undefined) {
        return 'other';
    }
    var loc = String(location);
    if (loc.startsWith('::')) {
        return 'record';
    }
    var isLocal = loc.startsWith('/documents') || loc.startsWith('documents') || loc.startsWith('/temp') || loc.startsWith('temp') || loc.startsWith('/images');
    if (!isLocal) {
        return 'link';
    }
    var ext = loc.split('?')[0].split('.').pop().toLowerCase();
    if (['png', 'jpg', 'jpeg', 'gif', 'webp', 'bmp', 'avif', 'svg', 'heic', 'heif'].includes(ext)) return ext === 'heic' || ext === 'heif' ? 'other' : 'image';
    if (ext === 'pdf') return 'pdf';
    if (['mp4', 'webm', 'mov', 'm4v', 'ogv'].includes(ext)) return 'video';
    if (['mp3', 'm4a', 'wav', 'ogg', 'oga', 'aac', 'flac'].includes(ext)) return 'audio';
    if (['md', 'markdown'].includes(ext)) return 'markdown';
    if (ext === 'csv' || ext === 'tsv') return 'csv';
    if (['txt', 'log', 'json', 'xml', 'yml', 'yaml', 'ini', 'cfg', 'conf', 'gpx', 'kml', 'html', 'htm'].includes(ext)) return 'text';
    return 'other';
}
function gbFileIcon(kind, location) {
    var ext = String(location || '').split('?')[0].split('.').pop().toLowerCase();
    switch (kind) {
        case 'image': return 'bi-file-earmark-image';
        case 'pdf': return 'bi-file-earmark-pdf';
        case 'video': return 'bi-file-earmark-play';
        case 'audio': return 'bi-file-earmark-music';
        case 'markdown': case 'text': return 'bi-file-earmark-text';
        case 'csv': return 'bi-file-earmark-spreadsheet';
        case 'link': return 'bi-link-45deg';
        case 'record': return 'bi-box-arrow-in-up-right';
    }
    if (['xls', 'xlsx', 'xlsm', 'ods'].includes(ext)) return 'bi-file-earmark-spreadsheet';
    if (['doc', 'docx', 'odt', 'rtf'].includes(ext)) return 'bi-file-earmark-word';
    if (['zip', '7z', 'rar', 'gz', 'tar'].includes(ext)) return 'bi-file-earmark-zip';
    return 'bi-file-earmark';
}
function gbDownloadName(name, location) {
    var ext = String(location || '').split('?')[0].split('.').pop();
    if (!name) return 'attachment.' + ext;
    return name.toLowerCase().endsWith('.' + ext.toLowerCase()) ? name : name + '.' + ext;
}
//Collect the sibling attachments of an element so the viewer can page through them.
function gbCollectGallery(elem) {
    var container = $(elem).closest('[data-gb-gallery]');
    if (!container.length) {
        return null;
    }
    var items = [];
    container.find('[data-gb-file]').each(function () {
        items.push({ name: $(this).attr('data-name'), location: $(this).attr('data-location') });
    });
    return items;
}
$(document).on('click', '[data-gb-file] .gb-file-preview, [data-gb-file].gb-file-open, [data-gb-file] .gb-file-name', function (e) {
    e.preventDefault();
    e.stopPropagation();
    var file = $(this).closest('[data-gb-file]');
    var gallery = gbCollectGallery(file) || [{ name: file.attr('data-name'), location: file.attr('data-location') }];
    var index = gallery.findIndex(x => x.location == file.attr('data-location'));
    gbOpenViewer(gallery, Math.max(0, index));
});
//Kept name: called from many places with (fileName, fileLocation).
function openAttachmentPreview(fileName, fileLocation, gallery, index) {
    if (gallery && gallery.length) {
        gbOpenViewer(gallery, index || 0);
    } else {
        gbOpenViewer([{ name: fileName, location: fileLocation }], 0);
    }
}
function closeAttachmentPreview() {
    $('#attachmentPreviewModal').modal('hide');
}
function gbOpenViewer(items, index) {
    gbViewer.items = items;
    gbViewer.index = index;
    var content = $('#attachmentPreviewModalContent');
    content.html(`
        <div class="gb-viewer">
            <div class="gb-viewer-bar">
                <button type="button" class="btn btn-ghost btn-icon" onclick="closeAttachmentPreview()" aria-label="Close"><i class="bi bi-x-lg"></i></button>
                <div class="gb-viewer-title"><i class="bi gb-viewer-kind"></i><strong></strong><small class="gb-viewer-count"></small></div>
                <div class="d-flex gap-1 gb-viewer-tools"></div>
            </div>
            <div class="gb-viewer-stage"></div>
        </div>`);
    var modal = $('#attachmentPreviewModal');
    modal.off('hidden.bs.modal.gbviewer').on('hidden.bs.modal.gbviewer', function () {
        gbViewer.renderToken++;
        if (gbViewer.pdf) { try { gbViewer.pdf.destroy(); } catch (e) { } gbViewer.pdf = null; }
        content.html('');
    });
    modal.modal('show');
    gbRenderViewerItem();
}
function gbViewerStep(delta) {
    if (gbViewer.items.length < 2) return;
    gbViewer.index = (gbViewer.index + delta + gbViewer.items.length) % gbViewer.items.length;
    gbRenderViewerItem();
}
$(document).on('keydown', function (e) {
    if (!$('#attachmentPreviewModal').hasClass('show')) return;
    if (e.key === 'ArrowRight') gbViewerStep(1);
    if (e.key === 'ArrowLeft') gbViewerStep(-1);
});
function gbRenderViewerItem() {
    var item = gbViewer.items[gbViewer.index];
    var kind = gbFileKind(item.location);
    var token = ++gbViewer.renderToken;
    if (gbViewer.pdf) { try { gbViewer.pdf.destroy(); } catch (e) { } gbViewer.pdf = null; }
    gbViewer.zoom = 1;
    var root = $('#attachmentPreviewModalContent');
    root.find('.gb-viewer-title strong').text(item.name || 'Attachment');
    root.find('.gb-viewer-kind').attr('class', 'bi gb-viewer-kind ' + gbFileIcon(kind, item.location));
    root.find('.gb-viewer-count').text(gbViewer.items.length > 1 ? `${gbViewer.index + 1} of ${gbViewer.items.length}` : '');
    var tools = root.find('.gb-viewer-tools').empty();
    var stage = root.find('.gb-viewer-stage').removeClass('zoomed').empty();
    if (kind === 'pdf') {
        tools.append(`<button type="button" class="btn btn-ghost btn-icon" onclick="gbPdfZoom(-0.25)" aria-label="Zoom out"><i class="bi bi-zoom-out"></i></button>`);
        tools.append(`<button type="button" class="btn btn-ghost btn-icon" onclick="gbPdfZoom(0.25)" aria-label="Zoom in"><i class="bi bi-zoom-in"></i></button>`);
    }
    if (kind !== 'record') {
        tools.append($('<a class="btn btn-ghost btn-icon" target="_blank" rel="noopener" aria-label="Open in new tab" title="Open in new tab"><i class="bi bi-box-arrow-up-right"></i></a>').attr('href', item.location));
    }
    if (kind !== 'link' && kind !== 'record') {
        tools.append($('<a class="btn btn-ghost btn-icon" aria-label="Download" title="Download"><i class="bi bi-download"></i></a>').attr('href', item.location).attr('download', gbDownloadName(item.name, item.location)));
    }
    if (kind !== 'record' && !String(item.location).startsWith('/temp')) {
        tools.append(`<button type="button" class="btn btn-ghost btn-icon gb-hide-sm" aria-label="Copy link" title="Copy link" onclick="gbCopyAttachmentLink()"><i class="bi bi-link-45deg"></i></button>`);
    }
    if (gbViewer.items.length > 1) {
        stage.append(`<button type="button" class="gb-viewer-nav prev" onclick="gbViewerStep(-1)" aria-label="Previous"><i class="bi bi-chevron-left"></i></button>`);
        stage.append(`<button type="button" class="gb-viewer-nav next" onclick="gbViewerStep(1)" aria-label="Next"><i class="bi bi-chevron-right"></i></button>`);
    }
    switch (kind) {
        case 'image':
            var img = $('<img alt="">').attr('src', item.location);
            img.on('click', function (e) {
                e.stopPropagation();
                stage.toggleClass('zoomed');
            });
            stage.append(img);
            break;
        case 'pdf':
            var pdfContainer = $('<div class="gb-viewer-pdf"><div class="gb-spinner"></div></div>');
            stage.append(pdfContainer);
            gbRenderPdf(item.location, pdfContainer, token);
            break;
        case 'video':
            stage.append($('<video controls playsinline preload="metadata"></video>').attr('src', item.location));
            break;
        case 'audio':
            stage.append($('<audio controls preload="metadata"></audio>').attr('src', item.location));
            break;
        case 'markdown':
        case 'text':
        case 'csv':
            var textContainer = $('<div class="gb-viewer-text"><div class="gb-spinner mx-auto"></div></div>');
            stage.append(textContainer);
            fetch(item.location, { credentials: 'same-origin' }).then(r => r.text()).then(text => {
                if (token !== gbViewer.renderToken) return;
                textContainer.empty();
                if (kind === 'markdown') {
                    textContainer.append($('<div class="gb-markdown"></div>').html(gbRenderMarkdown(text)));
                } else if (kind === 'csv') {
                    textContainer.append(gbCsvTable(text, item.location.toLowerCase().endsWith('.tsv') ? '\t' : ','));
                } else {
                    textContainer.append($('<pre></pre>').text(text));
                }
            }).catch(() => textContainer.html(gbViewerFallbackCard(item, kind)));
            break;
        case 'link':
            stage.append(`<div class="gb-viewer-card"><i class="bi bi-link-45deg gb-viewer-icon"></i><h3></h3><p class="text-break gb-link-text"></p><div class="d-flex gap-2 justify-content-center flex-wrap"><a class="btn btn-primary" target="_blank" rel="noopener"><i class="bi bi-box-arrow-up-right"></i>Open link</a><button type="button" class="btn btn-secondary" onclick="gbCopyAttachmentLink()"><i class="bi bi-clipboard"></i>Copy link</button></div></div>`);
            stage.find('h3').text(item.name);
            stage.find('.gb-link-text').text(item.location);
            stage.find('a.btn-primary').attr('href', item.location);
            break;
        case 'record':
            var parts = String(item.location).split(':');
            stage.append(`<div class="gb-viewer-card"><i class="bi bi-box-arrow-in-up-right gb-viewer-icon"></i><h3></h3><p>This attachment points to another record.</p><button type="button" class="btn btn-primary"><i class="bi bi-arrow-right"></i>Go to record</button></div>`);
            stage.find('h3').text(item.name);
            stage.find('button.btn-primary').on('click', function () {
                if (typeof loadRecordAttachment === 'function') {
                    loadRecordAttachment(parts[3], parts[2]);
                }
            });
            break;
        default:
            stage.append(gbViewerFallbackCard(item, kind));
            break;
    }
    //swipe between attachments on touch screens
    var startX = null;
    stage.off('touchstart.gb touchend.gb').on('touchstart.gb', function (e) {
        if (e.originalEvent.touches.length === 1 && !stage.hasClass('zoomed') && kind !== 'pdf') {
            startX = e.originalEvent.touches[0].clientX;
        } else {
            startX = null;
        }
    }).on('touchend.gb', function (e) {
        if (startX === null) return;
        var dx = e.originalEvent.changedTouches[0].clientX - startX;
        if (Math.abs(dx) > 60) gbViewerStep(dx < 0 ? 1 : -1);
        startX = null;
    });
}
function gbViewerFallbackCard(item, kind) {
    var card = $(`<div class="gb-viewer-card"><i class="bi ${gbFileIcon(kind, item.location)} gb-viewer-icon"></i><h3></h3><p>This file type can't be previewed in the browser.</p><div class="d-flex gap-2 justify-content-center flex-wrap"><a class="btn btn-primary"><i class="bi bi-download"></i>Download</a><a class="btn btn-secondary" target="_blank" rel="noopener"><i class="bi bi-box-arrow-up-right"></i>Open in new tab</a></div></div>`);
    card.find('h3').text(item.name);
    card.find('a.btn-primary').attr('href', item.location).attr('download', gbDownloadName(item.name, item.location));
    card.find('a.btn-secondary').attr('href', item.location);
    return card;
}
function gbCsvTable(text, delimiter) {
    var rows = text.split(/\r?\n/).filter(x => x.trim() != '').slice(0, 500).map(line => {
        var cells = [];
        var current = '';
        var quoted = false;
        for (var i = 0; i < line.length; i++) {
            var ch = line[i];
            if (ch === '"') {
                if (quoted && line[i + 1] === '"') { current += '"'; i++; }
                else quoted = !quoted;
            } else if (ch === delimiter && !quoted) {
                cells.push(current); current = '';
            } else {
                current += ch;
            }
        }
        cells.push(current);
        return cells;
    });
    var table = $('<table class="gb-csv"></table>');
    rows.forEach((row, index) => {
        var tr = $('<tr></tr>');
        row.forEach(cell => tr.append($(index === 0 ? '<th></th>' : '<td></td>').text(cell)));
        table.append(tr);
    });
    return table;
}
function gbCopyAttachmentLink() {
    var item = gbViewer.items[gbViewer.index];
    if (!item) return;
    var link = item.location.startsWith('/') ? window.location.origin + item.location : item.location;
    if (navigator.clipboard) {
        navigator.clipboard.writeText(link).then(() => successToast('Link copied'));
    }
}
var gbPdfLib = null;
function gbLoadPdfLib() {
    if (gbPdfLib) {
        return Promise.resolve(gbPdfLib);
    }
    return import('/lib/pdfjs/pdf.min.mjs').then(lib => {
        lib.GlobalWorkerOptions.workerSrc = '/lib/pdfjs/pdf.worker.min.mjs';
        gbPdfLib = lib;
        return lib;
    });
}
function gbRenderPdf(location, container, token) {
    gbLoadPdfLib().then(lib => {
        var task = lib.getDocument({
            url: location,
            cMapUrl: '/lib/pdfjs/cmaps/',
            cMapPacked: true,
            standardFontDataUrl: '/lib/pdfjs/standard_fonts/',
            wasmUrl: '/lib/pdfjs/wasm/',
            withCredentials: true
        });
        return task.promise;
    }).then(pdf => {
        if (token !== gbViewer.renderToken) {
            pdf.destroy();
            return;
        }
        gbViewer.pdf = pdf;
        $('#attachmentPreviewModalContent .gb-viewer-count').text(`${pdf.numPages} page${pdf.numPages == 1 ? '' : 's'}`);
        gbDrawPdfPages(container, token);
    }).catch(err => {
        console.warn('Glovebox: falling back to the browser PDF viewer', err);
        if (token !== gbViewer.renderToken) return;
        container.replaceWith($('<iframe title="PDF"></iframe>').attr('src', location));
    });
}
function gbDrawPdfPages(container, token) {
    var pdf = gbViewer.pdf;
    if (!pdf) return;
    container.empty();
    var available = Math.max(280, Math.min(container.width() || window.innerWidth, 1100) - 32);
    var ratio = window.devicePixelRatio || 1;
    var pages = [];
    for (var i = 1; i <= pdf.numPages; i++) {
        var holder = $(`<div class="gb-pdf-page"><canvas></canvas><span class="gb-pdf-page-num">${i}</span></div>`);
        container.append(holder);
        pages.push(holder);
    }
    var renderPage = function (pageNumber) {
        if (token !== gbViewer.renderToken || pageNumber > pdf.numPages) return;
        pdf.getPage(pageNumber).then(page => {
            var base = page.getViewport({ scale: 1 });
            var scale = (available / base.width) * gbViewer.zoom;
            var viewport = page.getViewport({ scale: scale * ratio });
            var canvas = pages[pageNumber - 1].find('canvas')[0];
            canvas.width = viewport.width;
            canvas.height = viewport.height;
            canvas.style.width = (viewport.width / ratio) + 'px';
            canvas.style.height = (viewport.height / ratio) + 'px';
            return page.render({ canvasContext: canvas.getContext('2d'), viewport: viewport, canvas: canvas }).promise;
        }).then(() => renderPage(pageNumber + 1)).catch(err => console.warn(err));
    };
    renderPage(1);
}
function gbPdfZoom(delta) {
    gbViewer.zoom = Math.min(3, Math.max(0.5, gbViewer.zoom + delta));
    var container = $('#attachmentPreviewModalContent .gb-viewer-pdf');
    if (container.length) {
        gbDrawPdfPages(container, gbViewer.renderToken);
    }
}
//Render a first-page thumbnail for PDFs in attachment grids.
function gbHydrateFileThumbs(scope) {
    $(scope || document).find('[data-gb-file][data-kind="pdf"] .gb-file-preview:not([data-thumb])').each(function () {
        var preview = $(this).attr('data-thumb', '1');
        var location = preview.closest('[data-gb-file]').attr('data-location');
        gbLoadPdfLib().then(lib => lib.getDocument({ url: location, withCredentials: true, cMapUrl: '/lib/pdfjs/cmaps/', cMapPacked: true, standardFontDataUrl: '/lib/pdfjs/standard_fonts/', wasmUrl: '/lib/pdfjs/wasm/' }).promise)
            .then(pdf => pdf.getPage(1).then(page => {
                var base = page.getViewport({ scale: 1 });
                var width = preview.width() || 160;
                var scale = (width * 2) / base.width;
                var viewport = page.getViewport({ scale: scale });
                var canvas = document.createElement('canvas');
                canvas.width = viewport.width;
                canvas.height = viewport.height;
                return page.render({ canvasContext: canvas.getContext('2d'), viewport: viewport, canvas: canvas }).promise.then(() => {
                    var img = $('<img alt="">').attr('src', canvas.toDataURL('image/jpeg', 0.8)).css('object-position', 'top');
                    preview.empty().append(img);
                    pdf.destroy();
                });
            }))
            .catch(() => { });
    });
}

$(document).on('shown.bs.modal', '.modal', function () {
    gbHydrateFileThumbs(this);
});

/* ---------- cost hiding ---------- */
//With costs hidden, required cost inputs get a 0 so records can still be saved.
$(document).on('show.bs.modal', '.modal', function () {
    if (typeof getGlobalConfig !== 'function' || !getGlobalConfig().hideCosts) {
        return;
    }
    var modal = $(this);
    setTimeout(function () {
        modal.find('input[id$="RecordCost"]').each(function () {
            if ($(this).val().trim() === '') {
                $(this).val('0');
            }
        });
    }, 0);
});

/* ---------- save feedback ---------- */
var gbSavebarTimer = null;
function gbShowSaved(message) {
    var bar = $('#gbSavebar');
    bar.find('span').text(message || 'Saved');
    bar.addClass('show');
    clearTimeout(gbSavebarTimer);
    gbSavebarTimer = setTimeout(function () { bar.removeClass('show'); }, 1800);
}
